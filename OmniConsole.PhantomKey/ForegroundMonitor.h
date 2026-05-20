#pragma once
#include <windows.h>
#include <string>

// ============================================================================
// 規則表
// ============================================================================

struct InputRule {
    const wchar_t* processName;   // 比對前景程式名稱（不含 .exe，大小寫不敏感）
    const wchar_t* shortCombo;    // View 短按送出的快速鍵
    const wchar_t* longCombo;     // View 長按送出的快速鍵（空字串=不觸發）
};

// 取得前景視窗的行程名稱（不含 .exe）
std::wstring GetForegroundProcessName();

// 取得前景視窗 HWND；無前景窗回 nullptr
HWND GetForegroundHwnd();

// 從規則表中查詢與前景程式匹配的規則
const InputRule* FindRuleForForeground();

// 判定前景程式是否為 Mouse Mode 目標（瀏覽器、Epic Games Launcher、Discord、檔案總管、桌面 Steam）
// explorer 與 steamwebhelper 依視窗特徵動態判斷（排除 FSE Task View / Steam Big Picture）
bool IsMouseModeTarget(const std::wstring& processName);

// ForceOn 模式下仍應排除的程式（本身用手把導覽或會撞到 Shell）
// 排除項：
//   - OmniConsole 主程式、Playnite Fullscreen（依行程名比對）
//   - Xbox App / Armoury Crate SE / Windows 設定 / Microsoft Store / OmniConsole 主程式（packaged 路徑，
//     依 title 表 + PFN 子字串表雙清單比對；涵蓋 ApplicationFrameHost 宿主與自跑 exe 兩種型態）
//   - explorer 的 FSE Task View 子模式（依視窗 class XamlExplorerHostIslandWindow 比對）
//   - steamwebhelper 的 Steam Big Picture 子模式（依視窗 style 無 WS_CAPTION + 相對 monitor 尺寸判斷）
bool IsMouseModeForceExcluded(const std::wstring& processName);

// 診斷：記錄前景視窗的 proc / class / title / coversMonitor / cloaked 狀態，
// 用以辨識 explorer 子類（檔案總管 vs FSE Task View）與 Steam 模式（桌面 vs Big Picture）
void LogForegroundWindowDiagnostics();

// 判定前景對 XInput D-pad 是否已有原生反應（送方向鍵會雙跳）
// 目前已知：Windows 11 檔案總管（explorer + 非 FSE Task View）
bool ForegroundHandlesDpadNatively(const std::wstring& processName);
