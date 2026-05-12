#include "ForegroundMonitor.h"
#include "Log.h"
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
    L"EpicGamesLauncher"
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
// UWP app（Xbox App / Armoury Crate SE）實際 proc 為 ApplicationFrameHost，
// 靠視窗 title 判斷，見 IsExcludedAppFrame()
static const wchar_t* g_mouseModeForceExclusions[] = {
    L"OmniConsole",              // 主程式本身用手把導航
    L"Playnite.FullscreenApp",   // Playnite Fullscreen 內建手把導覽
    // steamwebhelper 由 IsSteamBigPicture() 動態判斷：Big Picture 排除，桌面 Steam 不排除
    // ApplicationFrameHost 由 IsExcludedAppFrame() 動態判斷（Xbox / Armoury Crate SE）
};

// UWP app 由 ApplicationFrameHost 宿主，靠視窗 title 識別
// Xbox App title="Xbox"（不隨語系變動），Armoury Crate SE title="Armoury Crate SE"
static bool IsExcludedAppFrame() {
    HWND hwnd = GetForegroundWindow();
    if (!hwnd) return false;
    WCHAR title[256] = {};
    GetWindowTextW(hwnd, title, _countof(title));
    return _wcsicmp(title, L"Xbox") == 0
        || _wcsicmp(title, L"Armoury Crate SE") == 0;
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
// 其他鍵（ABXY、滾輪、游標、LB/RB 等）仍由 Mouse Mode 處理，因此只擋 D-pad 而非整個 Mouse Mode。
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
    // Xbox App / Armoury Crate SE 由 ApplicationFrameHost 宿主，靠視窗 title 辨識
    if (_wcsicmp(processName.c_str(), L"ApplicationFrameHost") == 0 && IsExcludedAppFrame())
        return true;
    return false;
}
