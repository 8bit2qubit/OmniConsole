#include <windows.h>
#include <xinput.h>
#include <appmodel.h>

#pragma comment(lib, "xinput.lib")

#include "Log.h"
#include "Config.h"
#include "SteamConfig.h"
#include "ForegroundMonitor.h"
#include "InputSender.h"

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

    // 讀取設定
    AppConfig config = ReadConfig();
    SteamOverlayConfig steamCfg = ReadSteamOverlayConfig();

    Log(L"Entering main loop.");

    // 自適應輪詢狀態
    DWORD sleepMs = 100;        // 初始閒置頻率 ~10Hz
    int idleTicks = 0;

    // 按鍵偵測狀態
    LARGE_INTEGER freq, pressStart, now;
    QueryPerformanceFrequency(&freq);
    pressStart.QuadPart = 0;

    // View（⧉）按鍵狀態
    bool viewWasPressed = false;
    bool viewLongPressFired = false;

    // Menu（☰）按鍵狀態
    bool menuWasPressed = false;
    bool menuLongPressFired = false;
    LARGE_INTEGER menuPressStart;
    menuPressStart.QuadPart = 0;

    // 前景程式偵測
    std::wstring lastFgProcess;

    // 常駐主迴圈
    while (true) {
        Sleep(sleepMs);

        // XInput 輪詢（支援最多 4 個控制器）
        bool viewPressed = false;
        bool menuPressed = false;
        for (DWORD i = 0; i < 4; i++) {
            XINPUT_STATE state = {};
            if (XInputGetState(i, &state) == ERROR_SUCCESS) {
                if (state.Gamepad.wButtons & XINPUT_GAMEPAD_BACK)
                    viewPressed = true;
                if (state.Gamepad.wButtons & XINPUT_GAMEPAD_START)
                    menuPressed = true;
                if (viewPressed || menuPressed) break;
            }
        }

        // 自適應輪詢頻率
        if (viewPressed || viewWasPressed || menuPressed || menuWasPressed) {
            sleepMs = 8;        // 輸入偵測中 ~125Hz
            idleTicks = 0;
        } else {
            idleTicks++;
            if (idleTicks > 30) sleepMs = 100;  // 恢復閒置 ~10Hz
        }

        // 前景程式變化偵測 → 重新讀取設定
        std::wstring currentFg = GetForegroundProcessName();
        if (currentFg != lastFgProcess) {
            lastFgProcess = currentFg;
            config = ReadConfig();
            steamCfg = ReadSteamOverlayConfig();
        }

        // ── View（⧉）狀態機：短按 / 長按 ──
        if (viewPressed && !viewWasPressed) {
            QueryPerformanceCounter(&pressStart);
            viewLongPressFired = false;
        } else if (viewPressed && viewWasPressed && !viewLongPressFired) {
            QueryPerformanceCounter(&now);
            double holdMs = (double)(now.QuadPart - pressStart.QuadPart) / freq.QuadPart * 1000.0;

            if (holdMs > 500.0) {
                const InputRule* rule = FindRuleForForeground();
                if (rule && rule->longCombo[0] != L'\0') {
                    Log(L"View long press (%dms). FG matched [%s]. Sending: %s",
                        (int)holdMs, rule->processName, rule->longCombo);
                    SendKeyCombo(rule->longCombo);
                }
                viewLongPressFired = true;
            }
        } else if (!viewPressed && viewWasPressed) {
            if (!viewLongPressFired) {
                const InputRule* rule = FindRuleForForeground();
                if (rule) {
                    Log(L"View short press. FG matched [%s]. Sending: %s",
                        rule->processName, rule->shortCombo);
                    SendKeyCombo(rule->shortCombo);
                }
            }
            viewLongPressFired = false;
        }
        viewWasPressed = viewPressed;

        // ── Menu（☰）狀態機：長按 → Steam In-Game Overlay ──
        if (menuPressed && !menuWasPressed) {
            QueryPerformanceCounter(&menuPressStart);
            menuLongPressFired = false;
        } else if (menuPressed && menuWasPressed && !menuLongPressFired) {
            QueryPerformanceCounter(&now);
            double holdMs = (double)(now.QuadPart - menuPressStart.QuadPart) / freq.QuadPart * 1000.0;

            if (holdMs > 500.0) {
                bool shouldFire =
                    _wcsicmp(config.defaultPlatform.c_str(), L"SteamBigPicture") == 0 &&
                    config.steamOverlayEnabled &&
                    steamCfg.overlayEnabled &&
                    _wcsicmp(currentFg.c_str(), L"steamwebhelper") != 0 &&
                    _wcsicmp(currentFg.c_str(), L"explorer") != 0;

                if (shouldFire) {
                    Log(L"Menu long press (%dms). FG=[%s]. Sending overlay: %s",
                        (int)holdMs, currentFg.c_str(), steamCfg.overlayShortcut.c_str());
                    SendKeyCombo(steamCfg.overlayShortcut);
                }
                menuLongPressFired = true;
            }
        }
        menuWasPressed = menuPressed;
    }

    // 以下程式碼正常情況下不會到達（常駐由外部終止）
    ReleaseMutex(hMutex);
    CloseHandle(hMutex);
    Log(L"PhantomKey ended.");
    return 0;
}
