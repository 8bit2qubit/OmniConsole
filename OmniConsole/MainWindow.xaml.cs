using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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

        private GamepadNavigationService? _gamepadNavigationService;

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

        /// <summary>
        /// 處理視窗啟動事件，負責初始化全螢幕狀態並在符合條件時自動啟動預設平台。
        /// </summary>
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
        /// 啟動成功後將隱藏視窗並在延遲後結束應用程式。
        /// </summary>
        private async System.Threading.Tasks.Task LaunchDefaultPlatformAsync()
        {
            if (_isLaunching) return;

            // 若目前為設定模式或尚未設定平台/已更新，則不執行自動啟動
            if (_isSettingsMode || SettingsService.IsFirstRunOrUpdate())
            {
                ShowSettings();
                return;
            }

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
                    // 5 秒延遲確保平台已到前景，FSE 不會重啟首頁
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

            // 載入目前設定
            var currentPlatform = SettingsService.GetDefaultPlatform();
            switch (currentPlatform)
            {
                case GamePlatform.SteamBigPicture:
                    RadioSteam.IsChecked = true;
                    RadioSteam.Focus(FocusState.Keyboard);
                    break;
                case GamePlatform.XboxApp:
                    RadioXbox.IsChecked = true;
                    RadioXbox.Focus(FocusState.Keyboard);
                    break;
                case GamePlatform.EpicGames:
                    RadioEpic.IsChecked = true;
                    RadioEpic.Focus(FocusState.Keyboard);
                    break;
                case GamePlatform.ArmouryCrateSE:
                    RadioArmouryCrate.IsChecked = true;
                    RadioArmouryCrate.Focus(FocusState.Keyboard);
                    break;
            }

            // 確保全螢幕並帶到前景
            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            this.Activate();

            StartGamepadPolling();
        }

        /// <summary>
        /// 啟動 Xbox 手把的輸入輪詢機制。
        /// 若尚未初始化 <see cref="GamepadNavigationService"/>，則會在此建立其實體，並傳遞 UI 根容器與 A 鍵的回呼函式。
        /// </summary>
        private void StartGamepadPolling()
        {
            if (_gamepadNavigationService == null)
            {
                _gamepadNavigationService = new GamepadNavigationService(
                    this.SettingsPanel,
                    this.DispatcherQueue,
                    OnGamepadAButtonPressed
                );
            }
            _gamepadNavigationService.Start();
        }

        /// <summary>
        /// 停止 Xbox 手把的輸入輪詢機制。
        /// 於結束應用程式或離開需要手把輸入的畫面時呼叫。
        /// </summary>
        private void StopGamepadPolling()
        {
            _gamepadNavigationService?.Stop();
        }

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式。
        /// 根據目前 UI 焦點所在的位置，觸發對應的平台選擇或儲存動作。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focusedElement = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (ReferenceEquals(focusedElement, RadioSteam)) RadioSteam.IsChecked = true;
            else if (ReferenceEquals(focusedElement, RadioXbox)) RadioXbox.IsChecked = true;
            else if (ReferenceEquals(focusedElement, RadioEpic)) RadioEpic.IsChecked = true;
            else if (ReferenceEquals(focusedElement, RadioArmouryCrate)) RadioArmouryCrate.IsChecked = true;
            else if (ReferenceEquals(focusedElement, SaveButton)) SaveButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 儲存使用者選擇的預設遊戲平台，並結束應用程式。
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            GamePlatform selected = GamePlatform.SteamBigPicture;

            if (RadioXbox.IsChecked == true)
                selected = GamePlatform.XboxApp;
            else if (RadioEpic.IsChecked == true)
                selected = GamePlatform.EpicGames;
            else if (RadioArmouryCrate.IsChecked == true)
                selected = GamePlatform.ArmouryCrateSE;

            SettingsService.SetDefaultPlatform(selected);
            SettingsService.SaveCurrentVersion();

            StopGamepadPolling();
            Application.Current.Exit();
        }
    }
}
