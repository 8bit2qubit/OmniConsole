#include "PingService.h"
#include "Log.h"

// ============================================================================
// PingService：主迴圈健康檢查回應通道
// ============================================================================

namespace PingService {

    // ============================================================================
    // 全域狀態
    // ============================================================================

    // 記錄最後一次主迴圈更新的時間戳（單位：毫秒）
    std::atomic<unsigned long long> g_lastHeartbeat{ 0 };

    // ping 執行緒控制代碼
    static HANDLE s_pingThread = nullptr;

    // ============================================================================
    // WndProc：處理 ping 訊息
    // ============================================================================
    //
    // 收到 WM_OMNICONSOLE_PING → 回傳「距離最後心跳的毫秒數（DWORD）」
    // 主程式據此判讀主迴圈推進狀況。
    // 此 proc 在 ping 執行緒上跑，不碰主迴圈狀態。
    //
    static LRESULT CALLBACK PingWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam) {
        if (msg == kPingMessage) {
            unsigned long long lastHb = g_lastHeartbeat.load(std::memory_order_relaxed);
            unsigned long long now = GetTickCount64();
            unsigned long long lag = (lastHb == 0 || now < lastHb) ? 0 : (now - lastHb);

            // 限制回傳值在 DWORD 範圍；極端值（>49 天）限制到最大值
            DWORD lagMs = (lag > 0xFFFFFFFFull) ? 0xFFFFFFFFu : static_cast<DWORD>(lag);
            return static_cast<LRESULT>(lagMs);
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ============================================================================
    // Ping 執行緒主體
    // ============================================================================
    //
    // 註冊類別 → 建立 message-only window → 跑 message loop
    // 與主執行緒完全解耦；主迴圈卡死時 ping 執行緒仍能回應，藉以區分
    // 「整個行程沒回應」（連這條執行緒都掛）vs「主迴圈卡住」（這條還活、但回傳延遲很大）。
    //
    static DWORD WINAPI PingThreadProc(LPVOID) {
        // 降一級優先度，確保與主迴圈搶 CPU 時主迴圈贏
        SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);

        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = PingWndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpszClassName = kWindowClassName;
        if (!RegisterClassExW(&wc)) {
            Log(L"[PingService] RegisterClassExW failed (err=%lu).", GetLastError());
            return 1;
        }

        // HWND_MESSAGE → message-only window
        HWND hwnd = CreateWindowExW(
            0, kWindowClassName, L"", 0, 0, 0, 0, 0,
            HWND_MESSAGE, nullptr, wc.hInstance, nullptr);
        if (!hwnd) {
            Log(L"[PingService] CreateWindowExW failed (err=%lu).", GetLastError());
            return 1;
        }

        Log(L"[PingService] ping window ready (class=%s).", kWindowClassName);

        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }

        return 0;
    }

    // ============================================================================
    // 公開介面
    // ============================================================================

    void Start() {
        g_lastHeartbeat.store(GetTickCount64(), std::memory_order_relaxed);

        s_pingThread = CreateThread(nullptr, 0, PingThreadProc, nullptr, 0, nullptr);
        if (!s_pingThread) {
            Log(L"[PingService] CreateThread failed (err=%lu); health check unavailable.", GetLastError());
            return;
        }
        Log(L"[PingService] thread started.");
    }

} // namespace PingService
