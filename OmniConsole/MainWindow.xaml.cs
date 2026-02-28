using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private bool _isFullScreenSet = false;
        private bool _isSettingsMode = false;
        private readonly ResourceLoader _resourceLoader = new();

        public MainWindow()
        {
            InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.Activated += MainWindow_Activated;
        }

        /// <summary>
        /// 在 Activate() 之前呼叫，標記為設定模式，防止 Activated 事件觸發平台啟動。
        /// </summary>
        public void PrepareForSettings()
        {
            _isSettingsMode = true;
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在視窗取得前景焦點時啟動，且防止重入
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_isLaunching) return;

            // 首次啟動時切換到全螢幕（延遲到 Activated 才執行，避免建構函式中卡住）
            if (!_isFullScreenSet)
            {
                _isFullScreenSet = true;
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }

            // 已經成功啟動過一次，不再透過 Activated 事件重複啟動
            if (_hasLaunchedOnce) return;

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (SettingsPanel.Visibility == Visibility.Visible) return;

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 啟動成功後清空 UI（黑底），平台會覆蓋在上面。
        /// </summary>
        private async System.Threading.Tasks.Task LaunchDefaultPlatformAsync()
        {
            if (_isLaunching) return;
            _isLaunching = true;

            try
            {
                // 確保為啟動模式
                LaunchPanel.Visibility = Visibility.Visible;
                SettingsPanel.Visibility = Visibility.Collapsed;

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                StatusText.Text = string.Format(_resourceLoader.GetString("Launching"), platformName);

                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 3 秒延遲確保平台已到前景，FSE 不會重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    var hwnd = WindowNative.GetWindowHandle(this);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // 等待目標平台進入前景後結束應用程式
                    await System.Threading.Tasks.Task.Delay(5000);
                    Application.Current.Exit();
                    return;
                }
                else
                {
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchFailed"), platformName);
                }
            }
            finally
            {
                _isLaunching = false;
            }
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重新啟動平台。
        /// </summary>
        public async void Reactivate()
        {
            _hasLaunchedOnce = false;
            LaunchPanel.Visibility = Visibility.Visible;

            // 確保全螢幕
            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 從設定入口呼叫，顯示設定介面。
        /// </summary>
        public void ShowSettings()
        {
            // 切換到設定模式
            LaunchPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            SaveStatusText.Text = "";

            // 載入目前設定
            var currentPlatform = SettingsService.GetDefaultPlatform();
            switch (currentPlatform)
            {
                case GamePlatform.SteamBigPicture:
                    RadioSteam.IsChecked = true;
                    break;
                case GamePlatform.XboxApp:
                    RadioXbox.IsChecked = true;
                    break;
                case GamePlatform.EpicGames:
                    RadioEpic.IsChecked = true;
                    break;
            }

            // 確保全螢幕並帶到前景
            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            this.Activate();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            GamePlatform selected = GamePlatform.SteamBigPicture;

            if (RadioXbox.IsChecked == true)
                selected = GamePlatform.XboxApp;
            else if (RadioEpic.IsChecked == true)
                selected = GamePlatform.EpicGames;

            SettingsService.SetDefaultPlatform(selected);

            Application.Current.Exit();
        }
    }
}
