using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

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
            _window = new MainWindow();
            _dispatcherQueue = _window.DispatcherQueue;

            // 檢查是否為設定入口冷啟動
            // 設定模式：在 Activate 前標記，防止 Activated 事件觸發平台啟動
            var mainWindow = _window as MainWindow;
            if (_startWithSettings && mainWindow != null)
            {
                mainWindow.PrepareForSettings();
            }

            _window.Activate();

            // Activate 後再呼叫 ShowSettings 切換 UI
            if (_startWithSettings && mainWindow != null)
            {
                mainWindow.ShowSettings();
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
    }
}
