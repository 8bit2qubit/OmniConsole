#include <windows.h>
#include <xinput.h>
#include <appmodel.h>

#pragma comment(lib, "xinput.lib")

#include "Log.h"
#include "Config.h"
#include "SteamConfig.h"
#include "ForegroundMonitor.h"
#include "InputSender.h"
#include "MouseMode.h"
#include "PingService.h"

// ============================================================================
// FSE 狀態查詢
// ============================================================================

typedef BOOL(WINAPI* PfnIsGamingFseActive)();
static PfnIsGamingFseActive LoadIsGamingFseActive() {
    HMODULE hMod = LoadLibraryW(L"api-ms-win-gaming-experience-l1-1-0.dll");
    if (!hMod) return nullptr;
    return reinterpret_cast<PfnIsGamingFseActive>(
        GetProcAddress(hMod, "IsGamingFullScreenExperienceActive"));
}

// ============================================================================
// 程式進入點
// ============================================================================

int WINAPI wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int) {
    InitLog();
    Log(L"[PhantomKey] started.");

    // 單例 Mutex：同一登入工作階段同時只允許一個 PhantomKey 實例（Local\ 命名空間）
    HANDLE hMutex = CreateMutexW(NULL, TRUE, L"Local\\OmniConsole_PhantomKey");
    if (!hMutex || GetLastError() == ERROR_ALREADY_EXISTS) {
        Log(L"[PhantomKey] Another instance already running, exiting.");
        if (hMutex) CloseHandle(hMutex);
        return 0;
    }

    Log(L"[PhantomKey] Singleton acquired.");

    // 驗證 OmniConsole MSIX 套件是否已安裝
    {
        const wchar_t* familyName = L"b5fbce6b-2d7d-4da0-b419-4beb30e2b808_n7gpkx2kypjte";
        UINT32 count = 0, bufLen = 0;
        (void)FindPackagesByPackageFamily(familyName, PACKAGE_FILTER_HEAD, &count, NULL, &bufLen, NULL, NULL);
        if (count == 0) {
            Log(L"[PhantomKey] OmniConsole package not installed, exiting.");
            CloseHandle(hMutex);
            return 1;
        }
        Log(L"[PhantomKey] OmniConsole package verified (count=%u).", count);
    }

    // 讀取設定
    AppConfig config = ReadConfig();
    SteamOverlayConfig steamCfg = ReadSteamOverlayConfig();
    WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut); // 同步給 Widget 讀
    unsigned long long lastIniMTime = GetSharedIniLastWriteTime();
    unsigned long long lastSteamVdfMTime = GetSteamLocalConfigLastWriteTime();

    // FSE 狀態查詢函式（載入失敗時不阻擋啟動，僅跳過退出檢查）
    auto pfnIsFseActive = LoadIsGamingFseActive();
    if (!pfnIsFseActive)
        Log(L"[PhantomKey] WARNING: Failed to load IsGamingFullScreenExperienceActive.");

    // 啟動 ping 服務（健康檢查回應通道）：建立 message-only window，主程式可透過 SendMessageTimeout 量測主迴圈推進狀況
    PingService::Start();

    Log(L"[PhantomKey] Entering main loop.");

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

        // 心跳：每圈更新一次；ping 執行緒讀此值回報主迴圈推進狀況
        PingService::UpdateHeartbeat();

        // XInput 輪詢：遍歷所有手把，收集 View/Menu 按鍵，取最後一支有顯著輸入的手把狀態
        XINPUT_GAMEPAD activePad = {};
        bool viewPressed = false;
        bool menuPressed = false;
        for (DWORD i = 0; i < 4; i++) {
            XINPUT_STATE state = {};
            if (XInputGetState(i, &state) != ERROR_SUCCESS) continue;
            const auto& g = state.Gamepad;
            if (g.wButtons & XINPUT_GAMEPAD_BACK)  viewPressed = true;
            if (g.wButtons & XINPUT_GAMEPAD_START) menuPressed = true;
            if (g.wButtons || g.bLeftTrigger || g.bRightTrigger ||
                abs(g.sThumbLX) > 8000 || abs(g.sThumbLY) > 8000 ||
                abs(g.sThumbRX) > 8000 || abs(g.sThumbRY) > 8000) {
                activePad = g;
            }
        }

        // 前景程式變化偵測 → 重新讀取設定 + 重設 Mouse Mode 狀態 + FSE 退出檢查
        std::wstring currentFg = GetForegroundProcessName();
        if (currentFg != lastFgProcess) {
            Log(L"[PhantomKey] FG changed: [%s] -> [%s].", lastFgProcess.c_str(), currentFg.c_str());
            LogForegroundWindowDiagnostics();
            lastFgProcess = currentFg;

            // 不在 FSE 中 → 結束 PhantomKey
            if (pfnIsFseActive && !pfnIsFseActive()) {
                Log(L"[PhantomKey] FSE no longer active, exiting.");
                break;
            }

            // 切到 steamwebhelper 時重讀 SteamConfig：涵蓋首次登入（vdf 路徑尚未確立）與帳號切換
            // Overlay 快捷鍵改動的同步走下方 localconfig.vdf mtime 監看，不依賴前景切換
            if (_wcsicmp(currentFg.c_str(), L"steamwebhelper") == 0) {
                steamCfg = ReadSteamOverlayConfig();
                WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut); // 同步給 Widget 讀
                lastSteamVdfMTime = GetSteamLocalConfigLastWriteTime();
            }

            MouseMode::Reset();
        }

        // Shared.ini 被改寫（主程式或 PhantomLink 操作）→ 即時重載 AppConfig
        unsigned long long curIniMTime = GetSharedIniLastWriteTime();
        if (curIniMTime != 0 && curIniMTime != lastIniMTime) {
            Log(L"[PhantomKey] Shared.ini changed, reloading config.");
            lastIniMTime = curIniMTime;
            config = ReadConfig();
            MouseMode::Reset();
        }

        // localconfig.vdf 被改寫（使用者在 SteamBigPicture 調整 overlay 快捷鍵或開關）→ 即時重讀 SteamConfig
        // 涵蓋「SteamBigPicture 改快捷鍵 → 直接啟動遊戲、未回 SteamBigPicture」這條路徑（前景切換偵測點抓不到）
        // Steam 未安裝 / 未登入 / 路徑尚未確立 → GetSteamLocalConfigLastWriteTime() 回 0
        unsigned long long curSteamVdfMTime = GetSteamLocalConfigLastWriteTime();
        if (curSteamVdfMTime != 0 && curSteamVdfMTime != lastSteamVdfMTime) {
            Log(L"[PhantomKey] localconfig.vdf changed, reloading SteamConfig.");
            lastSteamVdfMTime = curSteamVdfMTime;
            steamCfg = ReadSteamOverlayConfig();
            WriteSteamInGameOverlayShortcut(steamCfg.overlayShortcut);
        }

        // Mouse Mode 啟用條件：模式非 Off、無內建廠商映射；Auto 需前景符合白名單，ForceOn 除排除清單外皆生效
        bool mouseModeActive = false;
        if (!config.hasBuiltInGamepadMapping) {
            switch (config.mouseMode) {
                case MouseModeState::Off:     break;
                case MouseModeState::Auto:    mouseModeActive = IsMouseModeTarget(currentFg); break;
                case MouseModeState::ForceOn: mouseModeActive = !IsMouseModeForceExcluded(currentFg); break;
            }
        }

        // 自適應輪詢頻率
        if (viewPressed || viewWasPressed || menuPressed || menuWasPressed || mouseModeActive) {
            sleepMs = 8;        // 輸入偵測中 ~125Hz
            idleTicks = 0;
        } else {
            idleTicks++;
            if (idleTicks > 30) sleepMs = 100;  // 恢復閒置 ~10Hz
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
                    Log(L"[PhantomKey] View long press (%dms). FG matched [%s]. Sending: %s",
                        (int)holdMs, rule->processName, rule->longCombo);
                    SendKeyCombo(rule->longCombo);
                }
                viewLongPressFired = true;
            }
        } else if (!viewPressed && viewWasPressed) {
            if (!viewLongPressFired) {
                const InputRule* rule = FindRuleForForeground();
                if (rule) {
                    Log(L"[PhantomKey] View short press. FG matched [%s]. Sending: %s",
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
                    Log(L"[PhantomKey] Menu long press (%dms). FG=[%s]. Sending overlay: %s",
                        (int)holdMs, currentFg.c_str(), steamCfg.overlayShortcut.c_str());
                    SendKeyCombo(steamCfg.overlayShortcut);
                }
                menuLongPressFired = true;
            }
        }
        menuWasPressed = menuPressed;

        // ── Mouse Mode：前景為目標程式時將手把映射為滑鼠+鍵盤 ──
        if (mouseModeActive) {
            MouseMode::Tick(activePad, config);
        }
    }

    // 清理資源（FSE 退出後 break 到此）
    ReleaseMutex(hMutex);
    CloseHandle(hMutex);
    Log(L"[PhantomKey] ended.");
    return 0;
}
