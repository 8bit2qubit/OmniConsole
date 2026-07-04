using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmniConsole.Services;
using System;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings
{
    /// <summary>
    /// 設定的疑難排解分頁內容。提供「重設 Game Bar 並觸發 FSE」的備援操作。
    /// 由 SettingsHostView 透過 Frame.Navigate 載入（切走即銷毀）；本頁不實作手把 scope，相關鍵分派仍在 SettingsHostView。
    /// </summary>
    public sealed partial class TroubleshootView : Page
    {
        /// <summary>主視窗 HWND，供觸發 FSE 後隱藏視窗使用。由 SettingsHostView 於導覽後以 SetHwnd 注入。</summary>
        private IntPtr Hwnd { get; set; }

        /// <summary>建立疑難排解分頁。</summary>
        public TroubleshootView()
        {
            InitializeComponent();
        }

        /// <summary>Frame 導覽進來時依 FSE 是否可啟動決定重設按鈕的可用狀態（不可用時反灰而非隱藏）。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ResetGameBarButton.IsEnabled = FseService.CanActivate();
        }

        /// <summary>由 SettingsHostView 在導覽後注入主視窗 HWND。</summary>
        internal void SetHwnd(IntPtr hwnd) => Hwnd = hwnd;

        /// <summary>
        /// 重設 Game Bar 並觸發 FSE。先透過 <see cref="FseService.EnsureGameBarReadyAsync"/>
        /// 確保 Game Bar 完全就緒，再以「終止後重發」機制繞過可能卡住的 FSE 進入對話方塊。
        /// </summary>
        private async void ResetGameBarButton_Click(object sender, RoutedEventArgs e)
        {
            ResetGameBarButton.IsEnabled = false;
            ResetGameBarProgressRing.IsActive = true;
            ResetGameBarProgressRing.Visibility = Visibility.Visible;

            // 1. 強制重啟 Game Bar 並輪詢等待 GameBarFTServer 就緒
            //    （內部會先終止 GameBar.exe 再透過 ms-gamebar:// 重啟）
            await FseService.EnsureGameBarReadyAsync();
            await Task.Delay(500);

            // 2. 再次終止以繞過 FSE 進入對話方塊（「終止後重發」機制），稍待讓系統狀態穩定
            FseService.KillGameBar();
            await Task.Delay(500);

            // [Windows Bug] 從桌面進入 FSE 時，部分應用程式會被最大化並搶走前景焦點
            if (!FseService.IsActive())
                FseService.KillIgnoredBackgroundServices();

            if (FseService.TryActivate())
            {
                // 此應用程式會被重新啟動在 FSE 環境
                WindowForegroundService.Hide(Hwnd);
                App.ExitApp();
            }

            ResetGameBarProgressRing.IsActive = false;
            ResetGameBarProgressRing.Visibility = Visibility.Collapsed;
            ResetGameBarButton.IsEnabled = true;
        }
    }
}
