#include "pch.h"
#include "PhantomBridgeFactory.h"
#include "Log.h"
#include <appmodel.h>
#include <winrt/Windows.ApplicationModel.h>
#include <winrt/Windows.Management.Deployment.h>

#pragma comment(lib, "version.lib")

namespace
{
    // ── 判斷 process 是否以提升權限執行（admin token， High IL 以上） ──
    //
    // 流程：OpenProcess(QUERY_LIMITED_INFORMATION) → OpenProcessToken → GetTokenInformation(TokenElevation)。
    // PhantomKey 跑在 Medium IL，UIPI 會靜默丟棄 SendInput 給高 IL 視窗，故 Widget 收到 true 時要
    // 停用「自訂此 App」按鈕（自訂 profile 對 admin 程式無效）。
    // 取不到 token / 行程已結束 / 權限不足 → 一律回 false。
    bool IsProcessElevated(DWORD pid)
    {
        if (pid == 0) return false;
        HANDLE hp = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (hp == nullptr) return false;
        bool elevated = false;
        HANDLE hToken = nullptr;
        if (::OpenProcessToken(hp, TOKEN_QUERY, &hToken))
        {
            TOKEN_ELEVATION te = {};
            DWORD cb = 0;
            if (::GetTokenInformation(hToken, TokenElevation, &te, sizeof(te), &cb))
            {
                elevated = te.TokenIsElevated != 0;
            }
            ::CloseHandle(hToken);
        }
        ::CloseHandle(hp);
        return elevated;
    }

    // ── 由 process handle 取 AUMID ──
    //
    // 用 GetApplicationUserModelId（appmodel.h）直接從 process handle 拿 AUMID。
    // 桌面 process 回 APPMODEL_ERROR_NO_PACKAGE → 視為非 packaged，回空字串。
    // 比 SHGetPropertyStoreForWindow 跨 process 更可靠：實測 hwnd 的 PKEY_AppUserModel_ID
    // 跨 process 取回 VT_EMPTY，不能用。
    //
    // 注意：對 ApplicationFrameHost.exe 直接呼會回 host 自己的 AUMID 而非宿主 UWP；
    // 由 GetForegroundAppInfo 上層特殊處理（看 procName 決定走子視窗 enum 還是直接呼）。
    std::wstring GetAumidFromProcess(DWORD pid)
    {
        if (pid == 0) return std::wstring{};
        HANDLE hp = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (hp == nullptr) return std::wstring{};

        UINT32 len = 0;
        LONG rc = ::GetApplicationUserModelId(hp, &len, nullptr);
        if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0)
        {
            ::CloseHandle(hp);
            return std::wstring{};
        }
        std::wstring aumid(len, L'\0');
        rc = ::GetApplicationUserModelId(hp, &len, aumid.data());
        ::CloseHandle(hp);
        if (rc != ERROR_SUCCESS) return std::wstring{};
        // 砍掉結尾的 NUL（GetApplicationUserModelId 寫入含 NUL，len 也含 NUL）
        while (!aumid.empty() && aumid.back() == L'\0') aumid.pop_back();
        return aumid;
    }

    // ── 對 ApplicationFrameHost 視窗，列舉子視窗找 CoreWindow 取宿主 UWP 的 pid ──
    //
    // ApplicationFrameHost 是宿主行程，被它代管的 UWP 視窗（Windows.UI.Core.CoreWindow）
    // 是它的子視窗。從子 CoreWindow 的 thread id 反查 pid，就能取到真正 UWP 的 pid。
    struct FindCoreWindowCtx { DWORD pid = 0; };
    BOOL CALLBACK FindCoreWindowProc(HWND hwndChild, LPARAM lParam)
    {
        WCHAR cls[64] = {};
        ::GetClassNameW(hwndChild, cls, ARRAYSIZE(cls));
        if (_wcsicmp(cls, L"Windows.UI.Core.CoreWindow") == 0)
        {
            DWORD childPid = 0;
            ::GetWindowThreadProcessId(hwndChild, &childPid);
            if (childPid != 0)
            {
                auto* ctx = reinterpret_cast<FindCoreWindowCtx*>(lParam);
                ctx->pid = childPid;
                return FALSE;  // 停止列舉
            }
        }
        return TRUE;
    }
    DWORD GetHostedUwpPid(HWND frameHwnd)
    {
        FindCoreWindowCtx ctx;
        ::EnumChildWindows(frameHwnd, FindCoreWindowProc, reinterpret_cast<LPARAM>(&ctx));
        return ctx.pid;
    }

    // ── 對 packaged process 取 manifest 的 DisplayName ──
    //
    // packaged app 的 exe 通常沒 PE VersionInfo / FileDescription（自跑 exe 如 Notepad）
    // 或宿主在 ApplicationFrameHost.exe（如 Calculator / 設定，FileDescription 是 host 自己的）；
    // 兩者都改走 manifest 的 <DisplayName> 取得乾淨在地化 App 名稱。
    // 流程：GetPackageFullName(processHandle) → PackageManager.FindPackageForUser → Package.DisplayName。
    // 失敗回空字串。
    std::wstring GetPackageDisplayNameFromProcess(DWORD pid)
    {
        if (pid == 0) return std::wstring{};
        HANDLE hp = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (hp == nullptr) return std::wstring{};

        UINT32 len = 0;
        LONG rc = ::GetPackageFullName(hp, &len, nullptr);
        if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0) { ::CloseHandle(hp); return std::wstring{}; }
        std::wstring pfn(len, L'\0');
        rc = ::GetPackageFullName(hp, &len, pfn.data());
        ::CloseHandle(hp);
        if (rc != ERROR_SUCCESS) return std::wstring{};
        while (!pfn.empty() && pfn.back() == L'\0') pfn.pop_back();

        try
        {
            winrt::Windows::Management::Deployment::PackageManager pm;
            auto pkg = pm.FindPackageForUser(L"", pfn);
            if (pkg == nullptr) return std::wstring{};
            return std::wstring{ pkg.DisplayName() };
        }
        catch (...) { return std::wstring{}; }
    }

    // ── 讀 exe 版本資源的 FileDescription（像工作管理員的「描述」欄；失敗回空字串） ──
    //
    // 流程：GetFileVersionInfoSizeW → GetFileVersionInfoW → VerQueryValueW(\VarFileInfo\Translation)
    // 取第一個語言/碼頁，再用 \StringFileInfo\<lang><cp>\FileDescription 拿字串。
    std::wstring GetExeFileDescription(const std::wstring& exePath)
    {
        if (exePath.empty()) return std::wstring{};
        DWORD dummy = 0;
        DWORD size = ::GetFileVersionInfoSizeW(exePath.c_str(), &dummy);
        if (size == 0) return std::wstring{};

        std::vector<BYTE> data(size);
        if (!::GetFileVersionInfoW(exePath.c_str(), 0, size, data.data())) return std::wstring{};

        struct LangCp { WORD lang; WORD cp; };
        LangCp* translations = nullptr;
        UINT translationsLen = 0;
        if (!::VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation",
                              reinterpret_cast<LPVOID*>(&translations), &translationsLen) ||
            translations == nullptr || translationsLen < sizeof(LangCp))
        {
            return std::wstring{};
        }

        // 取第一個 lang/cp
        WCHAR subBlock[64] = {};
        swprintf_s(subBlock, L"\\StringFileInfo\\%04x%04x\\FileDescription",
                   translations[0].lang, translations[0].cp);

        LPWSTR desc = nullptr;
        UINT descLen = 0;
        if (!::VerQueryValueW(data.data(), subBlock,
                              reinterpret_cast<LPVOID*>(&desc), &descLen) ||
            desc == nullptr || descLen == 0)
        {
            return std::wstring{};
        }
        // descLen 含結尾 NUL； trim 掉
        std::wstring result(desc, descLen);
        while (!result.empty() && result.back() == L'\0') result.pop_back();
        return result;
    }
}

// ============================================================================
// PhantomBridgeFactory：所有跨 AppContainer 動作的實作
// ============================================================================
//
// 本 COM server 以 full trust 桌面行程執行（非 UWP AppContainer），
// SendInput / ShellExecute 不受限、不受 Game Bar 焦點捕獲干擾。

namespace winrt::PhantomBridge::implementation
{
    // ── 內部：解析 "Shift+Tab" 字串為 VK code 序列 ──────────────────────────
    //
    // 鍵名表與 PhantomKey/InputSender.cpp 的 ParseCombo 相同。
    static std::vector<WORD> ParseShortcut(const std::wstring& combo)
    {
        static const std::map<std::wstring, WORD> keyMap = {
            { L"ctrl", VK_LCONTROL }, { L"control", VK_LCONTROL },
            { L"alt", VK_LMENU },
            { L"shift", VK_LSHIFT },
            { L"tab", VK_TAB },
            { L"escape", VK_ESCAPE }, { L"esc", VK_ESCAPE },
            { L"space", VK_SPACE },
            { L"enter", VK_RETURN }, { L"return", VK_RETURN },
            { L"backspace", VK_BACK },
            { L"home", VK_HOME }, { L"end", VK_END },
            { L"insert", VK_INSERT }, { L"delete", VK_DELETE }, { L"del", VK_DELETE },
            { L"pageup", VK_PRIOR }, { L"pgup", VK_PRIOR },
            { L"pagedown", VK_NEXT }, { L"pgdn", VK_NEXT },
            { L"up", VK_UP }, { L"down", VK_DOWN }, { L"left", VK_LEFT }, { L"right", VK_RIGHT },
            { L"f1", VK_F1 }, { L"f2", VK_F2 }, { L"f3", VK_F3 }, { L"f4", VK_F4 },
            { L"f5", VK_F5 }, { L"f6", VK_F6 }, { L"f7", VK_F7 }, { L"f8", VK_F8 },
            { L"f9", VK_F9 }, { L"f10", VK_F10 }, { L"f11", VK_F11 }, { L"f12", VK_F12 },
        };

        std::vector<WORD> keys;
        std::wstring token;
        for (size_t i = 0; i <= combo.size(); i++)
        {
            wchar_t c = (i < combo.size()) ? combo[i] : L'+';
            if (c == L'+')
            {
                while (!token.empty() && token.front() == L' ') token.erase(token.begin());
                while (!token.empty() && token.back() == L' ') token.pop_back();
                if (token.empty()) continue;
                std::wstring lower = token;
                for (auto& ch : lower) ch = (wchar_t)towlower(ch);
                auto it = keyMap.find(lower);
                if (it != keyMap.end())
                    keys.push_back(it->second);
                else if (lower.size() == 1 && lower[0] >= L'a' && lower[0] <= L'z')
                    keys.push_back((WORD)(0x41 + (lower[0] - L'a')));
                else if (lower.size() == 1 && lower[0] >= L'0' && lower[0] <= L'9')
                    keys.push_back((WORD)(0x30 + (lower[0] - L'0')));
                token.clear();
            }
            else
            {
                token += c;
            }
        }
        return keys;
    }

    // ── 內部：批次送任意 modifier+key 組合 ───────────────────────────────────
    //
    // 一次 SendInput 按下所有鍵 → Sleep 50ms → 反序一次釋放。
    // 行為與 PhantomKey/InputSender.cpp 的 SendKeyCombo 相同。
    static void SendKeyComboFromVks(const std::vector<WORD>& keys) noexcept
    {
        if (keys.empty()) return;

        // ── 依序按下 ──
        std::vector<INPUT> inputs;
        inputs.reserve(keys.size());
        for (auto vk : keys)
        {
            INPUT inp = {};
            inp.type = INPUT_KEYBOARD;
            inp.ki.wVk = vk;
            inp.ki.wScan = static_cast<WORD>(MapVirtualKeyW(vk, MAPVK_VK_TO_VSC));
            inputs.push_back(inp);
        }
        ::SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));

        Sleep(50); // 確保目標應用程式收到組合鍵

        // ── 反序放開 ──
        inputs.clear();
        for (auto it = keys.rbegin(); it != keys.rend(); ++it)
        {
            INPUT inp = {};
            inp.type = INPUT_KEYBOARD;
            inp.ki.wVk = *it;
            inp.ki.wScan = static_cast<WORD>(MapVirtualKeyW(*it, MAPVK_VK_TO_VSC));
            inp.ki.dwFlags = KEYEVENTF_KEYUP;
            inputs.push_back(inp);
        }
        ::SendInput(static_cast<UINT>(inputs.size()), inputs.data(), sizeof(INPUT));
    }

    // ── 內部：兩鍵組合（modifier + key）的捷徑路徑 ──────────────────────────
    //
    // 行為與 SendKeyComboFromVks 相同（press → sleep → release）。
    static void SendKeyCombo2(WORD modifier, WORD key) noexcept
    {
        INPUT down[2] = {};
        down[0].type = INPUT_KEYBOARD;
        down[0].ki.wVk = modifier;
        down[0].ki.wScan = static_cast<WORD>(MapVirtualKeyW(modifier, MAPVK_VK_TO_VSC));
        down[1].type = INPUT_KEYBOARD;
        down[1].ki.wVk = key;
        down[1].ki.wScan = static_cast<WORD>(MapVirtualKeyW(key, MAPVK_VK_TO_VSC));
        ::SendInput(2, down, sizeof(INPUT));

        Sleep(50);

        INPUT up[2] = {};
        up[0].type = INPUT_KEYBOARD;
        up[0].ki.wVk = key;
        up[0].ki.wScan = static_cast<WORD>(MapVirtualKeyW(key, MAPVK_VK_TO_VSC));
        up[0].ki.dwFlags = KEYEVENTF_KEYUP;
        up[1].type = INPUT_KEYBOARD;
        up[1].ki.wVk = modifier;
        up[1].ki.wScan = static_cast<WORD>(MapVirtualKeyW(modifier, MAPVK_VK_TO_VSC));
        up[1].ki.dwFlags = KEYEVENTF_KEYUP;
        ::SendInput(2, up, sizeof(INPUT));
    }

    // ── 內部：偵測前景是否為 Steam Big Picture ───────────────────────────────
    //
    // 前提：前景行程為 steamwebhelper.exe（Big Picture 與桌面 Steam 共用此 exe，class 同為 SDL_app）。
    // 區分條件採視窗 style + 相對尺寸（與 PhantomKey/ForegroundMonitor.cpp 的 IsSteamBigPicture 行為相同）：
    //   Big Picture：無 WS_CAPTION（無標題列）+ 視窗寬高皆 ≥ 所在 monitor 的 50%
    // 用於 TriggerSteamInGameOverlay 分流：SteamBigPicture → Ctrl+1（Steam Menu）；其他 → INI 快捷鍵。
    static bool IsForegroundSteamBigPicture() noexcept
    {
        HWND fg = ::GetForegroundWindow();
        if (fg == nullptr) return false;

        DWORD pid = 0;
        ::GetWindowThreadProcessId(fg, &pid);
        if (pid == 0) return false;

        HANDLE hp = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
        if (hp == nullptr) return false;

        WCHAR path[MAX_PATH] = {};
        DWORD pathLen = MAX_PATH;
        bool gotPath = ::QueryFullProcessImageNameW(hp, 0, path, &pathLen) != FALSE;
        ::CloseHandle(hp);
        if (!gotPath) return false;

        const wchar_t* basename = wcsrchr(path, L'\\');
        basename = basename ? basename + 1 : path;
        if (_wcsicmp(basename, L"steamwebhelper.exe") != 0) return false;

        // ── 視窗 style：Big Picture 無 WS_CAPTION ──
        LONG style = ::GetWindowLongW(fg, GWL_STYLE);
        if ((style & WS_CAPTION) != 0) return false;

        // ── 相對尺寸：寬高皆 ≥ 所在 monitor 的 50% ──
        RECT wr = {};
        if (!::GetWindowRect(fg, &wr)) return false;
        HMONITOR hMon = ::MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi = { sizeof(mi) };
        if (!::GetMonitorInfoW(hMon, &mi)) return false;
        LONG winW = wr.right - wr.left, winH = wr.bottom - wr.top;
        LONG monW = mi.rcMonitor.right - mi.rcMonitor.left;
        LONG monH = mi.rcMonitor.bottom - mi.rcMonitor.top;
        return winW * 2 >= monW && winH * 2 >= monH;
    }

    // ── 內部：偵測是否處於 FSE（全螢幕體驗）模式 ─────────────────────────────
    //
    // 透過 api-ms-win-gaming-experience-l1-1-0.dll 的 IsGamingFullScreenExperienceActive。
    // 此 API Set 為 Windows 規格中的虛擬 DLL；不可解析時 LoadLibrary 回 nullptr，函式回 false。
    static bool IsFseActive() noexcept
    {
        typedef BOOL(WINAPI* PfnIsFseActive)();
        HMODULE hMod = ::LoadLibraryW(L"api-ms-win-gaming-experience-l1-1-0.dll");
        if (hMod == nullptr) return false;
        auto fn = reinterpret_cast<PfnIsFseActive>(
            ::GetProcAddress(hMod, "IsGamingFullScreenExperienceActive"));
        return fn != nullptr && fn() != FALSE;
    }

    // ── 內部：送 Win+G 收合 Game Bar，sleepMs 控制收合動畫 + 焦點回到下層的等待時間 ─────────────
    static void DismissGameBar(DWORD sleepMs)
    {
        SendKeyCombo2(VK_LWIN, 'G');
        Sleep(sleepMs);
    }

    // ── 內部：以指定 URI 喚起目標 App 並把它推到前景 ─────────────────────────
    //
    // 啟動 URI 前呼 AllowSetForegroundWindow(ASFW_ANY)，把前景許可權轉交給「下一個請求前景的 process」。
    static void LaunchUriAsForeground(std::wstring const& uriStr)
    {
        ::AllowSetForegroundWindow(ASFW_ANY);
        ::ShellExecuteW(nullptr, L"open", uriStr.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
    }

    // ── 公開方法：SendTaskView ───────────────────────────────────────────────
    //
    // 開啟 Windows 工作檢視。先送 Win+G 收合 Game Bar 再觸發，否則畫面彈一下會跳回。
    // 主路徑：shell namespace CLSID；回退：ShellExecute 回傳 <= 32 時退回 Win+Tab。
    void PhantomBridgeFactory::SendTaskView()
    {
        TryInstallClientWatchdog();
        DismissGameBar(500);

        HINSTANCE result = ::ShellExecuteW(
            nullptr,
            L"open",
            L"shell:::{3080F90E-D7AD-11D9-BD98-0000947B0257}",
            nullptr,
            nullptr,
            SW_SHOWNORMAL);

        // ShellExecuteW 成功時回傳值 > 32；失敗時退回 Win+Tab 鍵盤模擬作為回退
        if ((INT_PTR)result <= 32)
        {
            SendKeyCombo2(VK_LWIN, VK_TAB);
        }
    }

    // ── 公開方法：OpenSettings ───────────────────────────────────────────────
    //
    // 開啟 OmniConsole 主程式設定頁。
    // 註：目前 Widget UI 未提供入口（從 Game Bar Library 入口已能進入），保留供未來啟用。
    void PhantomBridgeFactory::OpenSettings()
    {
        TryInstallClientWatchdog();
        DismissGameBar(300);
        LaunchUriAsForeground(L"omniconsole://show-settings");
    }

    // ── 公開方法：TriggerSteamInGameOverlay ──────────────────────────────────
    //
    // 觸發 Steam In-Game Overlay（雙路徑）：
    //   - 前景判定為 Steam Big Picture（steamwebhelper + 無 WS_CAPTION + 視窗 ≥ monitor 50%）→ Ctrl+1（Steam Menu）
    //   - 其他（推定為遊戲中）→ 送 INI 中的 SteamInGameOverlayShortcut（典型 "Shift+Tab"）
    //
    // 先送 Win+G 收合 Game Bar 再送鍵盤事件（否則 Steam 收不到）。
    void PhantomBridgeFactory::TriggerSteamInGameOverlay(winrt::hstring const& shortcut)
    {
        TryInstallClientWatchdog();
        DismissGameBar(300);

        if (IsForegroundSteamBigPicture())
        {
            // ── SteamBigPicture 路徑：Ctrl+1 開啟 Steam Menu ──
            SendKeyCombo2(VK_LCONTROL, '1');
        }
        else
        {
            // ── 遊戲中路徑：送 INI 指定的 overlay 快捷鍵（典型 Shift+Tab）──
            std::wstring s{ shortcut };
            if (s.empty()) s = L"Shift+Tab"; // 防呆：Widget 傳空字串時用預設值
            auto keys = ParseShortcut(s);
            SendKeyComboFromVks(keys);
        }
    }

    // ── 公開方法：OpenXboxLibrary ────────────────────────────────────────────
    //
    // 啟動 xbox://library（Xbox 媒體櫃）。
    // 用途：Steam 玩家遊玩 Xbox 平台遊戲時提供進入 Xbox 媒體櫃的入口。
    void PhantomBridgeFactory::OpenXboxLibrary()
    {
        TryInstallClientWatchdog();
        DismissGameBar(300);
        LaunchUriAsForeground(L"xbox://library");
    }

    // ── 公開方法：GetForegroundAppInfo ───────────────────────────────────────
    //
    // 回傳前景視窗的：
    //   - title       ：視窗 title 純文字
    //   - processName ：行程名稱（不含 .exe，大小寫保留）
    //   - aumid       ：packaged app 由 GetApplicationUserModelId(processHandle) 取得；
    //                   ApplicationFrameHost 宿主的 UWP 走子視窗 CoreWindow 反查宿主 pid；
    //                   桌面 process 回 APPMODEL_ERROR_NO_PACKAGE，aumid 為空字串。
    // Widget 用於：頂部「目前程式」顯示 + 判定「自訂此 App」按鈕是否啟用。
    void PhantomBridgeFactory::GetForegroundAppInfo(winrt::hstring& title, winrt::hstring& processName, winrt::hstring& fullPath, winrt::hstring& aumid, winrt::hstring& displayName, bool& isElevated)
    {
        TryInstallClientWatchdog();
        title = winrt::hstring{};
        processName = winrt::hstring{};
        fullPath = winrt::hstring{};
        aumid = winrt::hstring{};
        displayName = winrt::hstring{};
        isElevated = false;

        HWND fg = ::GetForegroundWindow();
        if (fg == nullptr) return;

        // ── title ──
        WCHAR titleBuf[512] = {};
        ::GetWindowTextW(fg, titleBuf, ARRAYSIZE(titleBuf));
        title = winrt::hstring{ titleBuf };

        // ── processName + exePath（留給 FileDescription 用） ──
        DWORD pid = 0;
        ::GetWindowThreadProcessId(fg, &pid);
        isElevated = IsProcessElevated(pid);
        std::wstring procName;
        std::wstring exePath;
        if (pid != 0)
        {
            HANDLE hp = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
            if (hp != nullptr)
            {
                WCHAR path[MAX_PATH] = {};
                DWORD pathLen = MAX_PATH;
                if (::QueryFullProcessImageNameW(hp, 0, path, &pathLen))
                {
                    exePath = path;
                    size_t slash = exePath.find_last_of(L'\\');
                    std::wstring filename = (slash != std::wstring::npos) ? exePath.substr(slash + 1) : exePath;
                    size_t dot = filename.rfind(L'.');
                    if (dot != std::wstring::npos) filename = filename.substr(0, dot);
                    procName = filename;
                }
                ::CloseHandle(hp);
            }
        }
        processName = winrt::hstring{ procName };
        fullPath = winrt::hstring{ exePath };

        // ── 取 AUMID ──
        // 兩條路徑：
        //   1. ApplicationFrameHost 宿主的 UWP（Xbox / 設定 / 小算盤）：列舉子視窗找 CoreWindow，
        //      取被宿主 UWP 的 pid，再用 GetApplicationUserModelId 取 AUMID
        //   2. 自跑 exe 的 packaged（Notepad / WinUI Gallery / WinStore.App / SystemSettings）：
        //      直接對前景 pid 呼 GetApplicationUserModelId
        // 桌面 process（Steam / Brave / Discord）：GetApplicationUserModelId 回 APPMODEL_ERROR_NO_PACKAGE，
        // aumid 為空字串。
        DWORD aumidPid = pid;
        if (_wcsicmp(procName.c_str(), L"ApplicationFrameHost") == 0)
        {
            DWORD hostedPid = GetHostedUwpPid(fg);
            if (hostedPid != 0) aumidPid = hostedPid;
        }
        std::wstring aumidStr = GetAumidFromProcess(aumidPid);
        if (!aumidStr.empty()) aumid = winrt::hstring{ aumidStr };

        // ── displayName 優先序：
        //   1. packaged → PackageManager 取 manifest DisplayName（最乾淨，例 Notepad 回「記事本」）
        //   2. ApplicationFrameHost-hosted → title（Xbox / 設定 等 title 就是 App 名稱）
        //   3. Win32 桌面 → exe 版本資源 FileDescription（例 Brave 回「Brave」）
        //   4. 回退 → title → process 名稱
        bool isAppFrameHostHosted = (_wcsicmp(procName.c_str(), L"ApplicationFrameHost") == 0);
        bool isPackaged = !aumid.empty();
        std::wstring pkgDisplay;
        if (isPackaged) pkgDisplay = GetPackageDisplayNameFromProcess(aumidPid);
        Log(L"[GetForegroundAppInfo] proc=[%s] hosted=%d packaged=%d title=[%s] exePath=[%s] pkgDisplay=[%s]",
            procName.c_str(), isAppFrameHostHosted ? 1 : 0, isPackaged ? 1 : 0,
            title.c_str(), exePath.c_str(), pkgDisplay.c_str());

        if (!pkgDisplay.empty())
        {
            displayName = winrt::hstring{ pkgDisplay };
        }
        else if (isAppFrameHostHosted)
        {
            displayName = !title.empty() ? title : processName;
        }
        else
        {
            std::wstring desc = GetExeFileDescription(exePath);
            if (!desc.empty())
                displayName = winrt::hstring{ desc };
            else if (!title.empty())
                displayName = title;
            else
                displayName = processName;
        }
        Log(L"  → final displayName=[%s]", std::wstring{ displayName }.c_str());
    }

    // ── 公開方法：OpenProfileEditor ──────────────────────────────────────────
    //
    // 開啟 OmniConsole 主程式手把映射編輯器（omniconsole://edit-gamepad-profile?appId=...&displayName=...）。
    static winrt::hstring PercentEncode(winrt::hstring const& s)
    {
        return winrt::Windows::Foundation::Uri::EscapeComponent(s);
    }

    void PhantomBridgeFactory::OpenProfileEditor(winrt::hstring const& appId, winrt::hstring const& name, winrt::hstring const& fullPath)
    {
        TryInstallClientWatchdog();
        DismissGameBar(300);

        std::wstring uriStr = L"omniconsole://edit-gamepad-profile?appId=";
        uriStr += std::wstring{ PercentEncode(appId) };
        uriStr += L"&displayName=";
        uriStr += std::wstring{ PercentEncode(name) };
        if (!fullPath.empty())
        {
            uriStr += L"&fullPath=";
            uriStr += std::wstring{ PercentEncode(fullPath) };
        }

        LaunchUriAsForeground(uriStr);
    }
}
