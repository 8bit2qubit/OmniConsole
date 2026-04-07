#include <windows.h>
#include <xinput.h>
#include <appmodel.h>
#include <shlobj.h>
#include <string>
#include <vector>

#pragma comment(lib, "xinput.lib")

// ============================================================================
// 規則表
// ============================================================================

struct InputRule {
    const wchar_t* processName;   // 比對前景程式名（不含 .exe，大小寫不敏感）
    const wchar_t* shortCombo;    // View 短按送出的快速鍵
    const wchar_t* longCombo;     // View 長按送出的快速鍵（空字串=不觸發）
};

// steamwebhelper 是 Steam Big Picture 模式下的前景行程名
// Ctrl+1 = Steam 選單、Ctrl+2 = 快速存取選單
static const InputRule g_rules[] = {
    { L"steamwebhelper", L"Ctrl+1", L"Ctrl+2" },
};
static const int g_ruleCount = _countof(g_rules);

// ============================================================================
// 除錯日誌（Release 建置時完全移除）
// ============================================================================

#ifdef _DEBUG
static std::wstring g_logPath;

static void InitLog() {
    WCHAR localAppData[MAX_PATH];
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, localAppData))) {
        std::wstring dir = std::wstring(localAppData) + L"\\OmniConsole";
        CreateDirectoryW(dir.c_str(), NULL);
        g_logPath = dir + L"\\PhantomKeyTrace.log";
    }
}

static void Log(const wchar_t* fmt, ...) {
    if (g_logPath.empty()) return;
    HANDLE hFile = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    WCHAR buf[1024];
    int prefix = wsprintfW(buf, L"[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list args;
    va_start(args, fmt);
    wvsprintfW(buf + prefix, fmt, args);
    va_end(args);

    lstrcatW(buf, L"\n");
    DWORD written;
    WriteFile(hFile, buf, (DWORD)(lstrlenW(buf) * sizeof(WCHAR)), &written, NULL);
    CloseHandle(hFile);
}
#else
static void InitLog() {}
static void Log(const wchar_t*, ...) {}
#endif

// ============================================================================
// 輔助函式
// ============================================================================

// 解析簡易快速鍵字串（如 "Ctrl+1"）為 VK code 序列
static std::vector<WORD> ParseCombo(const std::wstring& combo) {
    std::vector<WORD> keys;
    std::wstring token;

    for (size_t i = 0; i <= combo.size(); i++) {
        wchar_t c = (i < combo.size()) ? combo[i] : L'+';
        if (c == L'+') {
            // 去除前後空白
            while (!token.empty() && token.front() == L' ') token.erase(token.begin());
            while (!token.empty() && token.back() == L' ') token.pop_back();
            // 轉小寫
            for (auto& ch : token) ch = towlower(ch);

            if (token == L"ctrl")       keys.push_back(VK_LCONTROL);
            else if (token == L"alt")   keys.push_back(VK_LMENU);
            else if (token == L"shift") keys.push_back(VK_LSHIFT);
            else if (token == L"1")     keys.push_back(0x31);
            else if (token == L"2")     keys.push_back(0x32);
            token.clear();
        } else {
            token += c;
        }
    }
    return keys;
}

// 送出鍵盤快速鍵組合
static void SendKeyCombo(const std::wstring& combo) {
    auto keys = ParseCombo(combo);
    if (keys.empty()) return;

    std::vector<INPUT> inputs;

    // 依序按下
    for (auto vk : keys) {
        INPUT inp = {};
        inp.type = INPUT_KEYBOARD;
        inp.ki.wVk = vk;
        inp.ki.wScan = (WORD)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        inp.ki.dwFlags = 0;
        inputs.push_back(inp);
    }
    SendInput((UINT)inputs.size(), inputs.data(), sizeof(INPUT));

    // 短暫按住，確保目標應用程式接收到組合鍵
    Sleep(50);

    // 反序放開
    inputs.clear();
    for (int i = (int)keys.size() - 1; i >= 0; i--) {
        INPUT inp = {};
        inp.type = INPUT_KEYBOARD;
        inp.ki.wVk = keys[i];
        inp.ki.wScan = (WORD)MapVirtualKeyW(keys[i], MAPVK_VK_TO_VSC);
        inp.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs.push_back(inp);
    }
    SendInput((UINT)inputs.size(), inputs.data(), sizeof(INPUT));
}

// 取得前景視窗的行程名（不含 .exe）
static std::wstring GetForegroundProcessName() {
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

// 從規則表中查詢與前景程式匹配的規則
static const InputRule* FindRuleForForeground() {
    std::wstring fgName = GetForegroundProcessName();
    if (fgName.empty()) return nullptr;

    for (int i = 0; i < g_ruleCount; i++) {
        if (_wcsicmp(fgName.c_str(), g_rules[i].processName) == 0)
            return &g_rules[i];
    }
    return nullptr;
}

// ============================================================================
// 程式進入點
// ============================================================================

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int) {
    InitLog();
    Log(L"PhantomKey started.");

    // 全域單例 Mutex：同時只允許一個 PhantomKey 實例
    HANDLE hMutex = CreateMutexW(NULL, TRUE, L"Local\\OmniConsole_PhantomKey");
    if (!hMutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        Log(L"Another PhantomKey instance already running, exiting.");
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    Log(L"Singleton acquired.");

    // 驗證 OmniConsole MSIX 套件是否已安裝
    {
        const wchar_t* familyName = L"b5fbce6b-2d7d-4da0-b419-4beb30e2b808_n7gpkx2kypjte";
        UINT32 count = 0, bufLen = 0;
        (void)FindPackagesByPackageFamily(familyName, PACKAGE_FILTER_HEAD, &count, NULL, &bufLen, NULL, NULL);
        if (count == 0) {
            Log(L"OmniConsole package not installed, exiting.");
            CloseHandle(hMutex);
            return 1;
        }
        Log(L"OmniConsole package verified (count=%u).", count);
    }

    Log(L"Entering main loop.");

    // 自適應輪詢狀態
    DWORD sleepMs = 100;        // 初始閒置頻率 ~10Hz
    int idleTicks = 0;

    // 按鍵偵測狀態
    LARGE_INTEGER freq, pressStart, now;
    QueryPerformanceFrequency(&freq);
    pressStart.QuadPart = 0;

    bool wasPressed = false;
    bool longPressFired = false;

    // 常駐主迴圈
    while (true) {
        Sleep(sleepMs);

        // XInput 輪詢（支援最多 4 個控制器）
        bool isPressed = false;
        for (DWORD i = 0; i < 4; i++) {
            XINPUT_STATE state = {};
            if (XInputGetState(i, &state) == ERROR_SUCCESS) {
                if (state.Gamepad.wButtons & XINPUT_GAMEPAD_BACK) {
                    isPressed = true;
                    break;
                }
            }
        }

        // 自適應輪詢頻率
        if (isPressed || wasPressed) {
            sleepMs = 8;        // 輸入偵測中 ~125Hz
            idleTicks = 0;
        } else {
            idleTicks++;
            if (idleTicks > 30) sleepMs = 100;  // 恢復閒置 ~10Hz
        }

        // 按鍵狀態機：短按（放開時觸發）/ 長按（按住 >500ms 觸發）
        if (isPressed && !wasPressed) {
            // 按下瞬間：開始計時
            QueryPerformanceCounter(&pressStart);
            longPressFired = false;
        } else if (isPressed && wasPressed && !longPressFired) {
            // 持續按住：檢查是否超過長按門檻
            QueryPerformanceCounter(&now);
            double holdMs = (double)(now.QuadPart - pressStart.QuadPart) / freq.QuadPart * 1000.0;

            if (holdMs > 500.0) {
                const InputRule* rule = FindRuleForForeground();
                if (rule && rule->longCombo[0] != L'\0') {
                    Log(L"Long press (%dms). FG matched [%s]. Sending: %s",
                        (int)holdMs, rule->processName, rule->longCombo);
                    SendKeyCombo(rule->longCombo);
                }
                longPressFired = true;
            }
        } else if (!isPressed && wasPressed) {
            // 放開：若長按未觸發過，視為短按
            if (!longPressFired) {
                const InputRule* rule = FindRuleForForeground();
                if (rule) {
                    Log(L"Short press released. FG matched [%s]. Sending: %s",
                        rule->processName, rule->shortCombo);
                    SendKeyCombo(rule->shortCombo);
                }
            }
        }

        wasPressed = isPressed;
    }

    // 以下程式碼正常情況下不會到達（常駐由外部終止）
    ReleaseMutex(hMutex);
    CloseHandle(hMutex);
    Log(L"PhantomKey ended.");
    return 0;
}
