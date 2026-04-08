#include "ForegroundMonitor.h"
#include "Log.h"

// ============================================================================
// 規則表
// ============================================================================

// steamwebhelper 是 Steam Big Picture 模式下的前景行程名
// Ctrl+1 = Steam 選單、Ctrl+2 = 快速存取選單
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

const InputRule* FindRuleForForeground() {
    std::wstring fgName = GetForegroundProcessName();
    if (fgName.empty()) return nullptr;

    for (int i = 0; i < g_ruleCount; i++) {
        if (_wcsicmp(fgName.c_str(), g_rules[i].processName) == 0)
            return &g_rules[i];
    }
    return nullptr;
}
