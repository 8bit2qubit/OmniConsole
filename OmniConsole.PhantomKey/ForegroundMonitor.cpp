#include "ForegroundMonitor.h"
#include "GamepadProfiles.h"
#include "Log.h"
#include <cwctype>
#include <dwmapi.h>

#pragma comment(lib, "dwmapi.lib")

// ============================================================================
// 規則表
// ============================================================================

// steamwebhelper.exe 為 Big Picture 與桌面 Steam 共用；此規則僅在前景判定為 Big Picture 時套用
// （見 FindRuleForForeground 內的 IsSteamBigPicture() 過濾）。Ctrl+1 = Steam 選單、Ctrl+2 = 快速存取選單。
static const InputRule g_rules[] = {
    { L"steamwebhelper", L"Ctrl+1", L"Ctrl+2" },
};
static const int g_ruleCount = _countof(g_rules);

// ============================================================================
// 前景程式偵測
// ============================================================================

HWND GetForegroundHwnd() {
    return GetForegroundWindow();
}

std::wstring GetForegroundProcessName() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return L"";

    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0) return L"";

    HANDLE hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!hProc) return L"";

    WCHAR path[MAX_PATH] = {};
    DWORD size = MAX_PATH;
    std::wstring result;
    if (QueryFullProcessImageNameW(hProc, 0, path, &size)) {
        std::wstring fullPath = path;
        size_t slash = fullPath.find_last_of(L'\\');
        std::wstring filename = (slash != std::wstring::npos) ? fullPath.substr(slash + 1) : fullPath;
        size_t dot = filename.rfind(L'.');
        if (dot != std::wstring::npos) filename = filename.substr(0, dot);
        result = filename;
    }
    CloseHandle(hProc);
    return result;
}

static bool IsSteamBigPicture();

const InputRule* FindRuleForForeground() {
    std::wstring fgName = GetForegroundProcessName();
    if (fgName.empty()) return nullptr;

    for (int i = 0; i < g_ruleCount; i++) {
        if (_wcsicmp(fgName.c_str(), g_rules[i].processName) == 0) {
            // steamwebhelper 快捷鍵只在 Big Picture 模式下觸發
            if (_wcsicmp(fgName.c_str(), L"steamwebhelper") == 0 && !IsSteamBigPicture())
                return nullptr;
            return &g_rules[i];
        }
    }
    return nullptr;
}

// ============================================================================
// Mouse Mode 目標前景程式
// ============================================================================

static bool IsExplorerTaskView();

static const wchar_t* g_mouseModeTargets[] = {
    L"msedge", L"chrome", L"firefox", L"opera", L"brave",
    L"EpicGamesLauncher", L"Discord"
};

bool IsMouseModeTarget(const std::wstring& processName) {
    if (processName.empty()) return false;
    for (auto t : g_mouseModeTargets)
        if (_wcsicmp(processName.c_str(), t) == 0) return true;
    // 檔案總管納入 Auto，但 FSE Task View 同行程需排除
    if (_wcsicmp(processName.c_str(), L"explorer") == 0 && !IsExplorerTaskView())
        return true;
    // 桌面 Steam（steamwebhelper，非 Big Picture）納入 Auto；Big Picture 不介入
    if (_wcsicmp(processName.c_str(), L"steamwebhelper") == 0 && !IsSteamBigPicture())
        return true;
    return false;
}

// ForceOn 模式排除清單：即使強制也不該介入（本身用手把操作或會撞到 Shell）
// explorer 由 IsExplorerTaskView() 動態判斷：FSE Task View 才排除，一般檔案總管不排除
// packaged app（Xbox App / Armoury Crate SE / Windows 設定 / Microsoft Store）由 IsExcludedPackagedApp() 動態判斷
static const wchar_t* g_mouseModeForceExclusions[] = {
    L"OmniConsole",              // 主程式本身用手把導航
    L"Playnite.FullscreenApp",   // Playnite Fullscreen 內建手把導覽
    // steamwebhelper 由 IsSteamBigPicture() 動態判斷：Big Picture 排除，桌面 Steam 不排除
    // packaged app 由 IsExcludedPackagedApp() 動態判斷（雙清單 title + PFN 子字串）
};

// Packaged UWP / Store app — title 清單
// 適用於官方名稱不隨語系變動的 app（Xbox App title="Xbox"，Armoury Crate SE title="Armoury Crate SE"）
static const wchar_t* g_excludedPackagedAppTitles[] = {
    L"Xbox",                // Xbox App
    L"Armoury Crate SE",
};

// Packaged UWP / Store app — AUMID 比對用的 PFN 子字串集合
// title 隨系統語系變動的 app 改走「對 AUMID 整段做 PFN 子字串比對」
// （AUMID 形如 <PFN>!<AppId>，搜尋的目標子字串實質為 PFN）
static const wchar_t* g_excludedPackagedAppPfnSubstrings[] = {
    L"windows.immersivecontrolpanel",               // Windows 設定（SystemSettings.exe，packaged 但自跑 exe）
    L"Microsoft.WindowsStore",                      // Microsoft Store（WinStore.App.exe，packaged 但自跑 exe）
    L"b5fbce6b-2d7d-4da0-b419-4beb30e2b808",        // OmniConsole 主程式自己
};

// 大小寫不敏感的子字串搜尋；自寫實作（CRT 無 case-insensitive 版的 wcsstr）
static bool ContainsIgnoreCase(const std::wstring& haystack, const wchar_t* needle) {
    if (!needle || !*needle) return false;
    auto toLower = [](const std::wstring& s) {
        std::wstring r; r.reserve(s.size());
        for (wchar_t c : s) r.push_back((wchar_t)std::towlower(c));
        return r;
    };
    std::wstring h = toLower(haystack);
    std::wstring n = toLower(std::wstring(needle));
    return h.find(n) != std::wstring::npos;
}

// 前景視窗是否屬於 packaged app 硬性排除清單
// title 表處理官方名稱穩定的 app； PFN 表處理 title 隨語系變動的 app
// 適用：ApplicationFrameHost 宿主的 UWP（Xbox / Armoury Crate SE）與 packaged 但自跑 exe 的 app（Windows 設定 / Microsoft Store）
static bool IsExcludedPackagedApp() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return false;

    WCHAR title[256] = {};
    GetWindowTextW(hwnd, title, _countof(title));
    for (auto t : g_excludedPackagedAppTitles) {
        if (_wcsicmp(title, t) == 0) return true;
    }

    std::wstring aumid = GetForegroundAumid(hwnd);
    if (!aumid.empty()) {
        for (auto p : g_excludedPackagedAppPfnSubstrings) {
            if (ContainsIgnoreCase(aumid, p)) return true;
        }
    }
    return false;
}

// FSE Task View 與檔案總管同為 explorer.exe，靠視窗 class 區分
// XamlExplorerHostIslandWindow = FSE Task View（不隨語系變動）
// CabinetWClass = 一般檔案總管
static bool IsExplorerTaskView() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return false;
    WCHAR cls[128] = {};
    GetClassNameW(hwnd, cls, _countof(cls));
    return _wcsicmp(cls, L"XamlExplorerHostIslandWindow") == 0;
}

// Steam Big Picture 與桌面 Steam 同為 steamwebhelper.exe (SDL_app)，靠視窗特徵區分
// title 比對在多語系下不可靠（zh-tw "Steam Big Picture 模式"、bg "Steam режим „Голям екран"、
// th "โหมด Steam Big Picture" 其排列千變萬化），改以視窗 style + 相對尺寸判斷：
//   Big Picture：無 WS_CAPTION（無標題列）+ 視窗寬高皆 ≥ 所在 monitor 的 50%
//   桌面 Steam 主視窗及子視窗（設定/好友/關於）皆帶 WS_CAPTION → 第一條件擋下
//   桌面 Steam 偶見的無 caption 提示彈窗遠小於 monitor 一半 → 第二條件擋下
// 不用「覆蓋整個 monitor」當條件，因 Big Picture 偶會置中留黑邊未貼齊四邊 (Steam / Windows Bug)
// （實測 2560x1440 → 1618x1047；1920x1080 → 1296x839，皆超過 monitor 50%）。
// 用「相對 monitor 尺寸」，自動適應不同解析度與 DPI。
static bool IsSteamBigPicture() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return false;
    LONG style = GetWindowLongW(hwnd, GWL_STYLE);
    if ((style & WS_CAPTION) != 0) return false;  // 有標題列 → 桌面 Steam 任一視窗
    RECT wr = {};
    if (!GetWindowRect(hwnd, &wr)) return false;
    HMONITOR hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hMon, &mi)) return false;
    LONG winW = wr.right - wr.left, winH = wr.bottom - wr.top;
    LONG monW = mi.rcMonitor.right - mi.rcMonitor.left;
    LONG monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
    return winW * 2 >= monW && winH * 2 >= monH;
}

// 比對視窗 rect 四個邊是否都涵蓋所在 monitor rect（幾何比對，僅供診斷 log 參考）
static bool IsWindowCoveringMonitor(HWND hwnd) {
    RECT wr = {};
    if (!GetWindowRect(hwnd, &wr)) return false;
    HMONITOR hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi = { sizeof(mi) };
    if (!GetMonitorInfoW(hMon, &mi)) return false;
    return wr.left  <= mi.rcMonitor.left  && wr.top    <= mi.rcMonitor.top
        && wr.right >= mi.rcMonitor.right && wr.bottom >= mi.rcMonitor.bottom;
}

void LogForegroundWindowDiagnostics() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) { Log(L"[FGMonitor] diag: no foreground window."); return; }

    WCHAR cls[128] = {};
    GetClassNameW(hwnd, cls, _countof(cls));

    WCHAR title[256] = {};
    GetWindowTextW(hwnd, title, _countof(title));

    bool coversMonitor = IsWindowCoveringMonitor(hwnd);

    int cloaked = 0;
    DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked));

    std::wstring procName = GetForegroundProcessName();
    Log(L"[FGMonitor] diag: proc=[%s] class=[%s] title=[%s] coversMonitor=%d cloaked=%d",
        procName.c_str(), cls, title, coversMonitor ? 1 : 0, cloaked);
}

// Windows 11 檔案總管對 XInput D-pad 已有原生方向鍵反應，Mouse Mode 再送一次會雙跳。
// FSE Task View 同為 explorer 但 class=XamlExplorerHostIslandWindow，不適用 D-pad 跳過。
// 只擋 D-pad；ABXY / 滾輪 / 搖桿 / LB / RB 等其他鍵的檔案總管原生反應未實測，照常由 Mouse Mode 處理。
bool ForegroundHandlesDpadNatively(const std::wstring& processName) {
    if (processName.empty()) return false;
    if (_wcsicmp(processName.c_str(), L"explorer") == 0 && !IsExplorerTaskView())
        return true;
    return false;
}

bool IsMouseModeForceExcluded(const std::wstring& processName) {
    if (processName.empty()) return false;
    for (auto t : g_mouseModeForceExclusions)
        if (_wcsicmp(processName.c_str(), t) == 0) return true;
    if (_wcsicmp(processName.c_str(), L"explorer") == 0 && IsExplorerTaskView())
        return true;
    // Steam Big Picture 內建手把導覽，ForceOn 也不該介入
    if (_wcsicmp(processName.c_str(), L"steamwebhelper") == 0 && IsSteamBigPicture())
        return true;
    // Packaged app 雙清單（title + PFN 子字串）判斷：
    // ApplicationFrameHost 宿主的 UWP（Xbox / Armoury Crate SE）與 packaged 但自跑 exe 的 app
    // （Windows 設定 SystemSettings.exe / Microsoft Store WinStore.App.exe）皆涵蓋
    if (IsExcludedPackagedApp())
        return true;
    return false;
}
