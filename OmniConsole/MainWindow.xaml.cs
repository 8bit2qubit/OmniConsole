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
        // Win32 API: 視窗顯示控制
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // Win32 API: 視窗樣式控制（從工作檢視隱藏）
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private readonly ResourceLoader _resourceLoader = new();

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
        }

        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在視窗取得前景焦點時啟動，且防止重入
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_isLaunching) return;

            // 已經成功啟動過一次，不再透過 Activated 事件重複啟動
            if (_hasLaunchedOnce) return;

            // 若設定面板正在顯示，不自動啟動
            if (SettingsPanel.Visibility == Visibility.Visible) return;

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 自動啟動已設定的預設平台並隱藏視窗。
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

                // 無論成功或失敗，OmniConsole 都保持運行。
                // FSE Shell 要求首頁應用程式持續存在：
                // 若首頁退出且目標平台尚未到前景，FSE 會不斷重啟首頁。
                _hasLaunchedOnce = true;

                if (success)
                {
                    // 先顯示已啟動狀態，讓使用者確認
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 等待目標平台進入前景後再隱藏
                    await System.Threading.Tasks.Task.Delay(3000);

                    // 從工作檢視和工作列隱藏，但保持運行
                    var hwnd = WindowNative.GetWindowHandle(this);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // 移至螢幕外
                    LaunchPanel.Visibility = Visibility.Collapsed;
                    this.AppWindow.Move(new Windows.Graphics.PointInt32(-9999, -9999));
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
        /// 從 FSE/Game Bar 重導時呼叫，顯示視窗並啟動平台。
        /// </summary>
        public async void Reactivate()
        {
            // 重設旗標，允許再次啟動
            _hasLaunchedOnce = false;

            // 先顯示視窗
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);

            // 啟動平台
            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 從設定入口呼叫，顯示設定介面並將視窗帶到前景。
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

            // 顯示並帶到前景
            var hwnd = WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_SHOW);
            SetForegroundWindow(hwnd);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            GamePlatform selected = GamePlatform.SteamBigPicture;

            if (RadioXbox.IsChecked == true)
                selected = GamePlatform.XboxApp;
            else if (RadioEpic.IsChecked == true)
                selected = GamePlatform.EpicGames;

            SettingsService.SetDefaultPlatform(selected);

            string name = ProcessLauncherService.GetPlatformDisplayName(selected);
            SaveStatusText.Text = string.Format(_resourceLoader.GetString("SavedStatus"), name);
        }
    }
}
