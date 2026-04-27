#include "pch.h"
#include "PhantomBridgeFactoryClsid.h"
#include "PhantomBridgeFactory.h"

#pragma comment(lib, "Rpcrt4.lib")

// ============================================================================
// PhantomBridge Full Trust COM Server 進入點
// ============================================================================
//
// 生命週期由 client（PhantomLink Widget）持有的 COM reference 決定：
// Windows 於首次 CoCreateInstance 時 spawn 本行程，client 結束後本行程自動退出。
//
// 架構（事件驅動，無輪詢，主執行緒不耗 CPU）：
//   1. init_apartment / CoRegisterClassObject / CoResumeClassObjects
//   2. WaitForSingleObject(shutdownEvent, INFINITE) 純 kernel 等待
//   3. shutdown 觸發：module_lock 計數歸零（COM 正常釋放）或
//      RegisterWaitForSingleObject callback（client 行程死亡）
//   4. CoRevokeClassObject + 退出

using namespace winrt;

// ── 全域狀態：module_lock 計數 + client watchdog handles ─────────────────────
//
// module_lock 歸零或 client 行程死亡時 SetEvent(g_shutdownEvent) 喚醒主執行緒。
// 冷啟動時 wWinMain 主動 ++ 一次持有 5 秒，避免 client 還沒 CoCreateInstance 就退出。

namespace
{
    std::atomic<uint32_t> g_moduleLockCount{ 0 };
    HANDLE g_shutdownEvent{ nullptr };

    std::atomic<bool> g_watchdogRegistered{ false };
    HANDLE g_clientProcess{ nullptr };
    HANDLE g_clientWaitHandle{ nullptr };

    void CALLBACK OnClientExit(PVOID, BOOLEAN) noexcept
    {
        if (g_shutdownEvent != nullptr)
        {
            ::SetEvent(g_shutdownEvent);
        }
    }
}

// ── TryInstallClientWatchdog：方法 entry 呼叫的 client 行程監聽安裝 ──────────
//
// 透過 RPC 取得 client（PhantomLink）PID → OpenProcess → 丟給 thread pool 等其死亡，
// client 終止瞬間 kernel 自動觸發 callback SetEvent(shutdownEvent)，達成 kernel 級低延遲偵測。
// 必須由 RPC dispatch thread 呼叫；IClassFactory::CreateInstance 不一定走 RPC 層
// （OLE32 內部派發可能繞過），故移到 runtime class 方法 entry 確保
// RpcServerInqCallAttributesW 有效。只裝一次（atomic CAS），失敗時重置旗標讓下次重試。
void TryInstallClientWatchdog() noexcept
{
    bool expected = false;
    if (!g_watchdogRegistered.compare_exchange_strong(expected, true))
        return;

    RPC_CALL_ATTRIBUTES_V2_W attrs = {};
    attrs.Version = 2;
    attrs.Flags = RPC_QUERY_CLIENT_PID;
    RPC_STATUS status = ::RpcServerInqCallAttributesW(nullptr, &attrs);
    if (status != RPC_S_OK)
    {
        // 取不到 RPC client PID（不在 RPC dispatch thread）→ 重置旗標讓下次重試
        g_watchdogRegistered.store(false);
        return;
    }

    DWORD pid = static_cast<DWORD>(reinterpret_cast<uintptr_t>(attrs.ClientPID));
    if (pid == 0)
    {
        g_watchdogRegistered.store(false);
        return;
    }

    g_clientProcess = ::OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (g_clientProcess == nullptr)
    {
        g_watchdogRegistered.store(false);
        return;
    }

    ::RegisterWaitForSingleObject(
        &g_clientWaitHandle,
        g_clientProcess,
        OnClientExit,
        nullptr,
        INFINITE,
        WT_EXECUTEONLYONCE);
}

// ── module_lock operator++/-- 實作 ───────────────────────────────────────────
//
// get_module_lock() 定義在 pch.h。
namespace winrt
{
    uint32_t module_lock::operator++() noexcept
    {
        return g_moduleLockCount.fetch_add(1, std::memory_order_relaxed) + 1;
    }

    uint32_t module_lock::operator--() noexcept
    {
        auto previous = g_moduleLockCount.fetch_sub(1, std::memory_order_acq_rel);
        if (previous == 1 && g_shutdownEvent != nullptr)
        {
            ::SetEvent(g_shutdownEvent);
        }
        return previous - 1;
    }
}

// ── IClassFactory：橋接 COM 與 WinRT ─────────────────────────────────────────
//
// WinRT factory 由 CppWinRT 產生在 .g.h；此處包成傳統 IClassFactory 供 COM 註冊使用。
struct FactoryClassObject : implements<FactoryClassObject, IClassFactory>
{
    HRESULT STDMETHODCALLTYPE CreateInstance(
        IUnknown* outer, REFIID iid, void** object) noexcept override
    {
        if (outer != nullptr) return CLASS_E_NOAGGREGATION;
        if (object == nullptr) return E_POINTER;
        *object = nullptr;

        try
        {
            auto instance = make<PhantomBridge::implementation::PhantomBridgeFactory>();
            return instance.as(iid, object);
        }
        catch (...)
        {
            return to_hresult();
        }
    }

    HRESULT STDMETHODCALLTYPE LockServer(BOOL) noexcept override
    {
        return S_OK;
    }
};

// ── wWinMain 進入點 ──────────────────────────────────────────────────────────

int APIENTRY wWinMain(
    _In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int)
{
    // ── 初始化 ──
    // shutdownEvent 在任何 module_lock 變動之前建立，確保 operator-- 歸零時
    // SetEvent 對象已存在。
    g_shutdownEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (g_shutdownEvent == nullptr) return HRESULT_FROM_WIN32(::GetLastError());

    // WinRT 單執行緒 apartment（ASTA/STA 由 Windows 依 client 協商決定）
    init_apartment();

    // ── 註冊 class object ──
    DWORD registration = 0;
    HRESULT hr = CoRegisterClassObject(
        CLSID_PhantomBridgeFactory,
        make<FactoryClassObject>().get(),
        CLSCTX_LOCAL_SERVER,
        REGCLS_SUSPENDED | REGCLS_MULTIPLEUSE,
        &registration);
    if (FAILED(hr)) return hr;

    hr = CoResumeClassObjects();
    if (FAILED(hr))
    {
        CoRevokeClassObject(registration);
        return hr;
    }

    // ── 冷啟動 debounce ──
    // 手動 ++ module_lock 持有 5 秒，避免主迴圈在 client 第一次 CoCreateInstance 前
    // 看到計數=0 就退出；5 秒到期後 detached thread 釋放 ref。
    ++get_module_lock();
    std::thread([]() {
        ::Sleep(5000);
        --get_module_lock();
    }).detach();

    // ── 主等待迴圈（kernel wait，不耗 CPU；觸發路徑見檔頭） ──
    ::WaitForSingleObject(g_shutdownEvent, INFINITE);

    // ── 清理 ──
    // 先解除 wait 註冊（INVALID_HANDLE_VALUE 阻塞等待 callback 完成），再關閉 client
    // process handle，避免 callback 競態存取已釋放資源。
    if (g_clientWaitHandle != nullptr)
    {
        ::UnregisterWaitEx(g_clientWaitHandle, INVALID_HANDLE_VALUE);
        g_clientWaitHandle = nullptr;
    }
    if (g_clientProcess != nullptr)
    {
        ::CloseHandle(g_clientProcess);
        g_clientProcess = nullptr;
    }

    CoRevokeClassObject(registration);
    ::CloseHandle(g_shutdownEvent);
    g_shutdownEvent = nullptr;
    return 0;
}
