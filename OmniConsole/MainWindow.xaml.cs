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
        // Win32 API: 完全隱藏/顯示視窗
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;

        private bool _isLaunching = false;
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

                StatusText.Text = success
                    ? string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName)
                    : string.Format(_resourceLoader.GetString("LaunchFailed"), platformName);

                // 啟動成功後完全隱藏視窗（從工作檢視和工作列消失）
                if (success)
                {
                    var hwnd = WindowNative.GetWindowHandle(this);
                    ShowWindow(hwnd, SW_HIDE);
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
