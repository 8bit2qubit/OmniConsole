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

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _dispatcherQueue = _window.DispatcherQueue;
            _window.Activate();
        }

        /// <summary>
        /// 從 Program.cs 的重導啟動事件呼叫，在 UI 執行緒上顯示設定介面。
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
    }
}
