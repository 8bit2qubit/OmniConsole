using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OmniConsole.Services;
using OmniConsole.Startup;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace OmniConsole
{
    /// <summary>
    /// 提供應用程式層級的行為與重導啟動的橋接。
    /// </summary>
    public partial class App : Application
    {
        private static Window? _window;
        private static DispatcherQueue? _dispatcherQueue;
        private readonly bool _startWithSettings;

        /// <summary>
        /// 建立 App 實例；showSettings=true 表示由設定入口啟動，跳過 FSE 引導直接顯示設定介面。
        /// </summary>
        public App(bool showSettings = false)
        {
            var meta = typeof(App).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .GroupBy(a => a.Key)
                .ToDictionary(g => g.Key, g => g.First().Value ?? string.Empty);
            BuildIdentity.MetadataResolver = key => meta.GetValueOrDefault(key, string.Empty);

            _startWithSettings = showSettings;
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            UpdateCheckService.OnSelfTerminating = DisposeGamepadServices;

            DebugLogger.Log($"[DIAG] OnLaunched pid={Environment.ProcessId} tick={Environment.TickCount64} startWithSettings={_startWithSettings}");

            // 在建立任何 UI 前套用語言（官方語言需 UI 建立前設定、外掛語言須在控制項報到前載入好）。
            InitializeLocalization();

            var decision = await StartupOrchestrator.RunStartupAsync(_startWithSettings);

            switch (decision.Route)
            {
                case StartupRoute.GuidanceFseNotAvailable:
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable());
                    return;

                case StartupRoute.GuidanceFseHandheldRequired:
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable(handheldRequired: true));
                    return;

                case StartupRoute.GuidanceFseHomeAppNotSet:
                    ShowGuidanceWindow(w => w.ShowFseHomeAppNotSet());
                    return;

                case StartupRoute.TryActivateFse:
                    // 觸發 FSE 進入流程後退出，由 Windows 以 FSE 環境重啟。
                    // Build 26220.8165+ 上 TryActivate 同步阻塞至使用者選擇，「Stay on desktop」回
                    // 0x80004004 (E_ABORT)；成功/失敗都 ExitApp：
                    //   成功 → Windows 重啟本程式進 FSE 環境
                    //   失敗 → 不在桌面啟動遊戲平台、直接退出
                    FseService.TryActivate();
                    ExitApp();
                    return;

                case StartupRoute.StartWithMainWindow:
                    SettingsService.ApplyNavigationSoundsSetting();
                    var mainWindow = new MainWindow();
                    _window = mainWindow;
                    _dispatcherQueue = mainWindow.DispatcherQueue;

                    if (_startWithSettings || decision.HasPendingUpdate)
                    {
                        mainWindow.PrepareForSettings();
                        mainWindow.ShowSettings(); // 內含 ActivateFullScreen 雙保險（FSE/桌面皆全螢幕）

                        if (decision.HasPendingUpdate)
                            _ = mainWindow.TryHandlePendingUpdateAsync();

                        WindowForegroundService.BringToForeground(mainWindow);

                        // 語言同步只在「進 Settings」路徑做：同步對話方塊僅限設定情境，不可彈在 LaunchPage 啟動平台時擋住流程。
                        _ = mainWindow.TrySyncLanguagesAsync();
                    }
                    else
                    {
                        // 啟動即全螢幕（雙保險：FSE Activate 前生效、桌面 window ready 後補救）
                        // 此路徑（正常啟動 → 去 LaunchPage 啟動平台）不做語言同步：避免同步對話方塊打斷啟動平台。
                        mainWindow.ActivateFullScreen();
                    }
                    break;
            }
        }

        /// <summary>
        /// 啟動時套用 UI 語言（在建立任何 UI 之前呼叫）。偏好存共用 Shared.ini（空＝跟系統）。
        /// 官方語言的 PRI 已在 Program.Main 處理，此處負責載入並套用外掛翻譯層。
        /// 任一步驟取不到資料夾/語言皆靜默降級回官方，不影響啟動。
        /// </summary>
        private static void InitializeLocalization()
        {
            try
            {
                string lang = SettingsService.GetUiLanguage();   // 空＝跟系統
                // 注意：PrimaryLanguageOverride 已在 Program.Main 最早處設定（須在資源載入前）；此處不再重設，只處理外掛翻譯層。

                // 載入外掛翻譯資料夾（共享 PublisherCacheFolder\OmniConsoleShared\Translations\OmniConsole）。
                // 只載「目前語言」內容（掃目錄記可選清單、不全載），避免把所有語言塞進記憶體浪費。
                try
                {
                    var shared = Windows.Storage.ApplicationData.Current.GetPublisherCacheFolder("OmniConsoleShared");
                    string dir = System.IO.Path.Combine(shared.Path, "Translations", "OmniConsole");
                    PhantomLocalizer.Instance.LoadFolder(dir, lang);
                }
                catch (Exception ex) { DebugLogger.Log("[Localizer] LoadFolder FAIL: " + ex.Message); }

                // 設定目前語言。空（跟隨系統）也必須顯式設空字串「清除」殘留語言（非「不設」），
                // 否則切回跟隨系統後仍走上次殘留語言、與其他文字不一致。
                PhantomLocalizer.Instance.SetLanguage(lang ?? string.Empty);
            }
            catch (Exception ex) { DebugLogger.Log("[Localizer] InitializeLocalization FAIL: " + ex.Message); }
        }

        /// <summary>
        /// 建立引導視窗並顯示指定的引導畫面。
        /// 用於 FSE 不可用、Home App 未設定等需要顯示說明但不執行平台啟動的情境。
        /// </summary>
        private void ShowGuidanceWindow(Action<MainWindow> show)
        {
            SettingsService.ApplyNavigationSoundsSetting();
            var win = new MainWindow();
            _window = win;
            _dispatcherQueue = win.DispatcherQueue;
            win.PrepareForSettings(); // 防止 Activated 觸發平台啟動
            win.Activate();
            show(win);
        }

        /// <summary>
        /// 主實例收到 show-settings / edit-gamepad-profile 重導時的進入點，在 UI 執行緒上
        /// 切到設定介面並呼叫 WindowForegroundService.BringToForeground 把主視窗搶到前景。
        /// </summary>
        public static void ShowSettingsFromRedirect()
        {
            if (MainWindow.IsInstallFlowInProgress)
            {
                DebugLogger.Log("[App] ShowSettingsFromRedirect ignored: install flow in progress");
                return;
            }

            DebugLogger.Log($"[DIAG] ShowSettingsFromRedirect pid={Environment.ProcessId} tick={Environment.TickCount64} hasWindow={_window != null}");
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.ShowSettings();
                    WindowForegroundService.BringToForeground(mainWindow);
                }
            });
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重新啟動平台。
        /// </summary>
        public static void ReactivateFromRedirect()
        {
            if (MainWindow.IsInstallFlowInProgress)
            {
                DebugLogger.Log("[App] ReactivateFromRedirect ignored: install flow in progress");
                return;
            }

            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.Reactivate();
                }
            });
        }

        /// <summary>
        /// 釋放主視窗兩個頁面持有的手把導覽服務。供 ExitApp 及更新自我終止前等
        /// 不經過 ExitApp 的結束路徑共用。
        /// </summary>
        public static void DisposeGamepadServices()
        {
            try { (_window as MainWindow)?.DisposeGamepadServices(); } catch { }
        }

        /// <summary>
        /// 統一退出應用程式。釋放手把導覽服務與 FSE 狀態通知後以 Environment.Exit(0) 終止行程。
        /// </summary>
        public static void ExitApp()
        {
            DebugLogger.Log("[App] ExitApp: disposing gamepad services, stopping FSE listener and exiting");
            DisposeGamepadServices();
            FseService.StopListening();
            Environment.Exit(0);
        }

        /// <summary>
        /// 從 Game Bar 重導時呼叫，直接啟動平台專屬 URI (Passthrough) 後退出應用程式。
        /// </summary>
        public static void PassthroughFromRedirect(string uri)
        {
            // 註：自 v1.9.0.0 起 GetEnablePassthrough 強制 return false，本 method 目前不會被呼叫到。
            // 下方 IsInstallFlowInProgress 防護擋的是更新流程中誤觸 ExitApp()（會終止整個行程）；
            // 待開發人員模式頁恢復 Passthrough，本 method 重新可達後才會實際執行到。
            if (MainWindow.IsInstallFlowInProgress)
            {
                DebugLogger.Log("[App] PassthroughFromRedirect ignored: install flow in progress");
                return;
            }

            _dispatcherQueue?.TryEnqueue(() =>
            {
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri));
                // 先隱藏視窗，避免退出時閃白
                if (_window is not null)
                    WindowForegroundService.Hide(_window);
                ExitApp();
            });
        }
    }
}
