#pragma once
#include <windows.h>
#include <atomic>

// ============================================================================
// PingService：主程式對 PhantomKey 健康檢查的回應通道
// ============================================================================
//
// 可區分「死掉」、「卡住」、「忙碌」、「健康」四種狀態。
//
// 架構：
//   1. 主迴圈每圈呼叫 UpdateHeartbeat()
//   2. 獨立 ping 執行緒持有一個 message-only window（class: OmniConsole.PhantomKey.PingWnd）
//   3. WndProc 處理 WM_OMNICONSOLE_PING(WM_APP+1)：直接回傳「距離最後心跳的毫秒數」
//   4. 主程式用 SendMessageTimeoutW(SMTO_ABORTIFHUNG, 100ms) 取值
//
// 解讀回傳：
//   - 0-150ms      → 健康 (Responsive)：涵蓋閒置 100ms 的自然間隔
//   - 150-1000ms   → 忙碌 (Busy)：正在處理輸入或短暫連發
//   - >1000ms      → 卡住 (Stuck)：主迴圈超過一秒沒推進，疑似殭屍狀態
//   - SendMessageTimeout 失敗 → 整個行程沒回應 (Hung)，死掉了
// ============================================================================

namespace PingService {

    // ── 公開介面 ────────────────────────────────────────────────────────────────

    // 啟動 ping 執行緒；必須在主迴圈進入前呼叫。失敗不拋例外、僅 Log。
    void Start();

    // 主迴圈每圈呼叫，更新心跳時間戳。
    inline void UpdateHeartbeat();

    // 視窗類別名稱（主程式靠這個 FindWindowExW 找）
    constexpr const wchar_t* kWindowClassName = L"OmniConsole.PhantomKey.PingWnd";

    // Ping 訊息 ID：WM_APP + 1
    constexpr UINT kPingMessage = WM_APP + 1;

    // 心跳時間戳（GetTickCount64 值）；由主迴圈寫、ping 執行緒讀
    extern std::atomic<unsigned long long> g_lastHeartbeat;

    inline void UpdateHeartbeat() {
        g_lastHeartbeat.store(GetTickCount64(), std::memory_order_relaxed);
    }

} // namespace PingService
