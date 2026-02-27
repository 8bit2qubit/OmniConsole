using Microsoft.UI.Xaml;
using OmniConsole.Services;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        private bool _hasLaunched = false;

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在首次啟動時自動執行，避免視窗重新取得焦點時重複啟動
            if (_hasLaunched) return;
            _hasLaunched = true;

            StatusText.Text = "正在啟動 Steam...";

            bool success = await ProcessLauncherService.LaunchSteamBigPictureAsync();

            StatusText.Text = success
                ? "Steam 已啟動"
                : "啟動失敗，請確認 Steam 已安裝";
        }
    }
}
