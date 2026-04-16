#pragma once
#include <string>

// ============================================================================
// 規則表
// ============================================================================

struct InputRule {
    const wchar_t* processName;   // 比對前景程式名（不含 .exe，大小寫不敏感）
    const wchar_t* shortCombo;    // View 短按送出的快速鍵
    const wchar_t* longCombo;     // View 長按送出的快速鍵（空字串=不觸發）
};

// 取得前景視窗的行程名（不含 .exe）
std::wstring GetForegroundProcessName();

// 從規則表中查詢與前景程式匹配的規則
const InputRule* FindRuleForForeground();

// 判定前景程式是否為 Mouse Mode 目標（瀏覽器、Epic Games Launcher、檔案總管、桌面 Steam）
// explorer 與 steamwebhelper 依視窗特徵動態判斷（排除 FSE Task View / Steam Big Picture）
bool IsMouseModeTarget(const std::wstring& processName);

// ForceOn 模式下仍應排除的程式（本身用手把導覽或會撞到 Shell）
// 排除項：
//   - OmniConsole 主程式、Playnite Fullscreen（依 process name 比對）
//   - Xbox App / Armoury Crate SE（UWP 由 ApplicationFrameHost 宿主，依視窗 title 比對）
//   - explorer 的 FSE Task View 子模式（依視窗 class XamlExplorerHostIslandWindow 比對）
//   - steamwebhelper 的 Steam Big Picture 子模式（依視窗 title 比對）
bool IsMouseModeForceExcluded(const std::wstring& processName);

// 診斷：記錄前景視窗的 proc / class / title / coversMonitor / cloaked 狀態，
// 用以辨識 explorer 子類（檔案總管 vs FSE Task View）與 Steam 模式（桌面 vs Big Picture）
void LogForegroundWindowDiagnostics();
