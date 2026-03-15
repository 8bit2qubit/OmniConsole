using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OmniConsole.Services;

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

        public App(bool showSettings = false)
        {
            _startWithSettings = showSettings;
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 若從桌面環境啟動（非 FSE 模式、非設定模式），自動觸發 FSE
            if (!_startWithSettings && !FseService.IsActive())
            {
                if (!FseService.CanActivate())
                {
                    // 系統未啟用 FSE，顯示提示引導使用者先啟用
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable());
                    return;
                }

                if (!FseService.IsOmniConsoleSetAsHomeApp())
                {
                    // FSE 可用，但 Home App 未設為 OmniConsole，引導使用者至設定頁面
                    ShowGuidanceWindow(w => w.ShowFseHomeAppNotSet());
                    return;
                }

                // [Windows Bug] Game Bar 未執行時（常見於系統從休眠回復後尚未就緒），FSE 觸發雖會回
                // 傳成功，但 FSE 進入對話方塊不會出現，導致靜默退出後無任何視窗。此為 Windows 本身
                // 的缺陷，非 OmniConsole 可控範圍；先確保 Game Bar 就緒再觸發以避免 FSE 啟動失敗。
                if (!FseService.IsGameBarRunning())
                    await FseService.EnsureGameBarRunningAsync();

                if (FseService.TryActivate())
                {
                    // FSE 已觸發，Windows 會重新以 FSE 環境啟動本應用程式
                    Application.Current.Exit();
                    return;
                }
                // TryActivate 失敗（系統支援但觸發失敗），繼續正常啟動
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
        private void ShowGuidanceWindow(System.Action<MainWindow> show)
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
        /// 從 Game Bar 重導時呼叫，直接啟動平台專屬 URI (Passthrough) 後退出應用程式。
        /// </summary>
        public static void PassthroughFromRedirect(string uri)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                _ = Windows.System.Launcher.LaunchUriAsync(new System.Uri(uri));
                Application.Current.Exit();
            });
        }
    }
}
