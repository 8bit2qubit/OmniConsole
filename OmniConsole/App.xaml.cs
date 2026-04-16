using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;

namespace OmniConsole
{
    /// <summary>
    /// 提供應用程式層級的行為與重導啟動的橋接。
    /// </summary>
    public partial class App : Application
    {
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private static Window? _window;
        private static DispatcherQueue? _dispatcherQueue;
        private readonly bool _startWithSettings;

        public App(bool showSettings = false)
        {
            _startWithSettings = showSettings;
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 註冊 FSE 狀態變化通知（取代輪詢），記錄啟動時的 FSE 狀態供診斷
            FseService.StartListening();
            DebugLogger.Log($"[App] IsSupported={FseService.IsSupported()}, CanActivate={FseService.CanActivate()}, IsActive={FseService.IsActive()}");

            // 首次安裝或版本更新時：先遷移舊版設定（若有殘留 LocalCache INI），再同步共用 INI
            // 確保所有欄位存在、含內建手把映射偵測結果；一般啟動不需重跑這兩個動作
            if (SettingsService.IsFirstRunOrUpdate())
            {
                SettingsService.MigrateLegacyIfNeeded();
                SettingsService.SyncPhantomKeyStore();
            }

            // 若從桌面環境啟動（非 FSE 模式、非設定模式），自動觸發 FSE
            if (!_startWithSettings && !FseService.IsActive())
            {
                if (!FseService.IsSupported())
                {
                    // 系統未啟用 FSE（未使用 Xbox Full Screen Experience Tool 或非原生 FSE 掌機），引導使用者先啟用
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable());
                    return;
                }

                if (!FseService.CanActivate() || !FseService.IsOmniConsoleSetAsHomeApp())
                {
                    // FSE 已啟用但 Home App 未設為 OmniConsole（設為「無」、其他 App、或尚未設定）
                    ShowGuidanceWindow(w => w.ShowFseHomeAppNotSet());
                    return;
                }

                // [Windows Bug] Game Bar 未執行時（常見於系統從休眠回復後尚未就緒），FSE 觸發雖會回
                // 傳成功，但 FSE 進入對話方塊不會出現，導致靜默退出後無任何視窗。此為 Windows 本身
                // 的缺陷，非 OmniConsole 可控範圍；先確保 Game Bar 就緒再觸發以避免 FSE 啟動失敗。
                if (!FseService.IsGameBarReady())
                {
                    await FseService.EnsureGameBarReadyAsync();
                    await System.Threading.Tasks.Task.Delay(500);
                }

                // [Windows Bug] 部分應用程式（音訊面板、Windows 設定等）在進入 FSE 後會被最大化並搶
                // 走前景焦點，在進入前先終止以避免干擾。
                FseService.KillIgnoredBackgroundServices();

                if (FseService.TryActivate())
                {
                    // FSE 已觸發，Windows 會重新以 FSE 環境啟動本應用程式
                    ExitApp();
                    return;
                }

                // TryActivate 失敗：使用者在 FSE 進入對話方塊中選擇了「停留在桌面上」，
                // 或系統支援但觸發失敗。不應在桌面環境啟動遊戲平台，而是直接退出。
                // Windows 11 Build 26220.8165+ 的 SetGamingFullScreenExperience 會同步阻塞至使用者選擇，
                // 選擇「Stay on desktop」時回傳 0x80004004 (E_ABORT)。
                ExitApp();
                return;
            }

            var mainWindow = new MainWindow();
            _window = mainWindow;
            _dispatcherQueue = mainWindow.DispatcherQueue;

            // 設定模式：在 Activate 前標記，防止 Activated 事件觸發平台啟動
            if (_startWithSettings)
            {
                mainWindow.PrepareForSettings();
                mainWindow.ShowSettings();
            }
            else
            {
                mainWindow.Activate();
            }
        }

        /// <summary>
        /// 建立引導視窗並顯示指定的引導畫面。
        /// 用於 FSE 不可用、Home App 未設定等需要顯示說明但不執行平台啟動的情境。
        /// </summary>
        private void ShowGuidanceWindow(Action<MainWindow> show)
        {
            var win = new MainWindow();
            _window = win;
            _dispatcherQueue = win.DispatcherQueue;
            win.PrepareForSettings(); // 防止 Activated 觸發平台啟動
            win.Activate();
            show(win);
        }

        /// <summary>
        /// 從設定入口重導時呼叫，在 UI 執行緒上顯示設定介面。
        /// </summary>
        public static void ShowSettingsFromRedirect()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.ShowSettings();
                }
            });
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重新啟動平台。
        /// </summary>
        public static void ReactivateFromRedirect()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.Reactivate();
                }
            });
        }

        /// <summary>
        /// 統一退出應用程式。取消 FSE 狀態通知後以 Environment.Exit(0) 終止行程。
        /// </summary>
        public static void ExitApp()
        {
            DebugLogger.Log("[App] ExitApp: stopping FSE listener and exiting");
            FseService.StopListening();
            Environment.Exit(0);
        }

        /// <summary>
        /// 從 Game Bar 重導時呼叫，直接啟動平台專屬 URI (Passthrough) 後退出應用程式。
        /// </summary>
        public static void PassthroughFromRedirect(string uri)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri));
                // 先隱藏視窗，避免退出時閃白
                if (_window is not null)
                    ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 0);
                ExitApp();
            });
        }
    }
}
