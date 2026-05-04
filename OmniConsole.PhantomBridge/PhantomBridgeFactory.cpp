#include "pch.h"
#include "PhantomBridgeFactory.h"

// ============================================================================
// PhantomBridgeFactory：所有跨 AppContainer 動作的實作
// ============================================================================
//
// 本 COM server 以 fulltrust 桌面行程執行（非 UWP AppContainer），
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

    // ── 公開方法：SendTaskView ───────────────────────────────────────────────
    //
    // 開啟 Windows 工作檢視。先送 Win+G 收合 Game Bar 再觸發，否則畫面彈一下會跳回。
    // 主路徑：shell namespace CLSID；回退：ShellExecute 回傳 <= 32 時退回 Win+Tab。
    void PhantomBridgeFactory::SendTaskView()
    {
        TryInstallClientWatchdog();
        SendKeyCombo2(VK_LWIN, 'G');
        Sleep(500); // 讓 Game Bar 完成收合動畫 + 焦點回到桌面/遊戲

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
    // 冷啟動 OmniConsole 主程式進入設定頁。
    // 註：目前 Widget UI 未提供入口，保留實作供未來啟用；現存桌面模式有未解的焦點問題。
    void PhantomBridgeFactory::OpenSettings()
    {
        TryInstallClientWatchdog();
        SendKeyCombo2(VK_LWIN, 'G');
        Sleep(300); // 等 Game Bar 收合 + 前景回到下層

        const bool fseActive = IsFseActive();

        if (fseActive)
        {
            // ── FSE 路徑 ──
            HWND fg = ::GetForegroundWindow();
            if (fg != nullptr)
            {
                WCHAR className[256] = {};
                ::GetClassNameW(fg, className, ARRAYSIZE(className));
                if (_wcsicmp(className, L"Progman") != 0 &&
                    _wcsicmp(className, L"WorkerW") != 0)
                {
                    ::ShowWindow(fg, SW_MINIMIZE);
                    Sleep(150); // 讓最小化動畫完成
                }
            }

            try
            {
                winrt::Windows::Foundation::Uri uri{ L"omniconsole://show-settings" };
                winrt::Windows::System::Launcher::LaunchUriAsync(uri).get();
                return;
            }
            catch (...)
            {
                // LaunchUriAsync 失敗
            }
        }

        // ── 桌面路徑（或 FSE 失敗時的回退）──
        ::ShellExecuteW(
            nullptr,
            L"open",
            L"omniconsole://show-settings",
            nullptr,
            nullptr,
            SW_SHOWNORMAL);
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
        SendKeyCombo2(VK_LWIN, 'G');
        Sleep(300); // 等 Game Bar 收合 + 焦點回到 Steam（SteamBigPicture 或遊戲）

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
    // 與 OpenSettings 同模式：FSE 中最小化前景，桌面模式走 ShellExecute。
    // 用途：Steam 玩家遊玩 Xbox 平台遊戲時提供進入 Xbox 媒體櫃的入口。
    void PhantomBridgeFactory::OpenXboxLibrary()
    {
        TryInstallClientWatchdog();
        SendKeyCombo2(VK_LWIN, 'G');
        Sleep(300);

        const bool fseActive = IsFseActive();

        if (fseActive)
        {
            // ── FSE 路徑 ──
            // 前景已是 Xbox App 時略過最小化，否則會出現「縮小再放大」的回彈動畫。
            HWND fg = ::GetForegroundWindow();
            if (fg != nullptr)
            {
                WCHAR className[256] = {};
                ::GetClassNameW(fg, className, ARRAYSIZE(className));

                // Xbox App 為 UWP，視窗由 ApplicationFrameHost 代管，class=ApplicationFrameWindow + title="Xbox"。
                // 前景已是 Xbox App 時略過最小化，否則會出現「縮小再放大」的回彈動畫。
                bool fgIsXboxApp = false;
                if (_wcsicmp(className, L"ApplicationFrameWindow") == 0)
                {
                    WCHAR title[256] = {};
                    ::GetWindowTextW(fg, title, ARRAYSIZE(title));
                    fgIsXboxApp = (_wcsicmp(title, L"Xbox") == 0);
                }

                if (!fgIsXboxApp &&
                    _wcsicmp(className, L"Progman") != 0 &&
                    _wcsicmp(className, L"WorkerW") != 0)
                {
                    ::ShowWindow(fg, SW_MINIMIZE);
                    Sleep(150);
                }
            }

            try
            {
                winrt::Windows::Foundation::Uri uri{ L"xbox://library" };
                winrt::Windows::System::Launcher::LaunchUriAsync(uri).get();
                return;
            }
            catch (...)
            {
                // fall through
            }
        }

        // ── 桌面路徑（或 FSE 失敗時的回退）──
        ::ShellExecuteW(
            nullptr,
            L"open",
            L"xbox://library",
            nullptr,
            nullptr,
            SW_SHOWNORMAL);
    }
}
