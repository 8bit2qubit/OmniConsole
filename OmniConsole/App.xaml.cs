using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Storage;

namespace OmniConsole
{
    /// <summary>
    /// 提供應用程式層級的行為與重導啟動的橋接。
    /// </summary>
    public partial class App : Application
    {
        private static Window? _window;
        private static DispatcherQueue? _dispatcherQueue;

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _dispatcherQueue = _window.DispatcherQueue;

            // 檢查是否為設定入口冷啟動
            bool showSettings = false;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.ContainsKey("_ShowSettings"))
                {
                    values.Remove("_ShowSettings");
                    showSettings = true;
                }
            }
            catch { }

            if (showSettings && _window is MainWindow mainWindow)
            {
                mainWindow.ShowSettings();
            }

            _window.Activate();
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
