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

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 若從桌面環境啟動（非 FSE 模式、非設定模式），自動觸發 FSE
            if (!_startWithSettings && !FseService.IsActive())
            {
                if (!FseService.CanActivate())
                {
                    // 系統未啟用 FSE，顯示提示引導使用者先啟用
                    _window = new MainWindow();
                    _dispatcherQueue = _window.DispatcherQueue;
                    var notAvailableWindow = _window as MainWindow;
                    notAvailableWindow?.PrepareForSettings(); // 防止 Activated 觸發平台啟動
                    _window.Activate();
                    notAvailableWindow?.ShowFseNotAvailable();
                    return;
                }

                if (FseService.TryActivate())
                {
                    // FSE 已觸發，Windows 會重新以 FSE 環境啟動本應用程式
                    Application.Current.Exit();
                    return;
                }
                // TryActivate 失敗（系統支援但觸發失敗），繼續正常啟動
            }

            _window = new MainWindow();
            _dispatcherQueue = _window.DispatcherQueue;

            // 檢查是否為設定入口冷啟動
            // 設定模式：在 Activate 前標記，防止 Activated 事件觸發平台啟動
            var mainWindow = _window as MainWindow;
            if (_startWithSettings && mainWindow != null)
            {
                mainWindow.PrepareForSettings();
                mainWindow.ShowSettings();
            }
            else
            {
                _window.Activate();
            }
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
