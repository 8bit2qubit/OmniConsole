using Microsoft.UI.Xaml;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        // Win32 API: 完全隱藏視窗（不出現在工作檢視和工作列）
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        private bool _isLaunching = false;

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
            _isLaunching = true;

            try
            {
                StatusText.Text = "正在啟動 Steam...";

                bool success = await ProcessLauncherService.LaunchSteamBigPictureAsync();

                StatusText.Text = success
                    ? "Steam 已啟動"
                    : "啟動失敗，請確認 Steam 已安裝";

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
    }
}
