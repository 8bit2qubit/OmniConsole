using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 啟動畫面 UserControl。
    /// 負責平台自動啟動、FSE 引導畫面及啟動失敗時的操作按鈕。
    /// </summary>
    public sealed partial class LaunchPage : UserControl
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>啟動失敗或需要進行設定時，由 MainWindow 切換至設定介面。</summary>
        public event EventHandler? NavigateToSettingsRequested;

        /// <summary>使用者點選「返回桌面」或手把 B 鍵時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 WS_EX_TOOLWINDOW 設定使用。</summary>
        public IntPtr Hwnd { get; set; }

        /// <summary>
        /// 已完成過一次實際啟動嘗試。
        /// MainWindow_Activated 在此為 true 時不再重複觸發啟動，避免視窗重新取得焦點後再次啟動。
        /// </summary>
        public bool HasLaunchedOnce => _hasLaunchedOnce;

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private readonly ResourceLoader _resourceLoader = new();

        private GamepadNavigationService? _launchPanelGamepadService;

        public LaunchPage()
        {
            InitializeComponent();
        }

        // ── 平台啟動 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 先預檢可用性，不可用則顯示錯誤訊息；啟動成功後隱藏視窗，
        /// 輪詢前景視窗確認平台已到前景後結束應用程式。
        /// </summary>
        public async Task LaunchDefaultPlatformAsync()
        {
            if (_isLaunching) return;

            // 首次執行或版本更新時不自動啟動，轉至設定介面讓使用者確認預設平台
            if (SettingsService.IsFirstRunOrUpdate())
            {
                NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            _isLaunching = true;

            try
            {
                // 重設為初始狀態，確保上次失敗殘留的按鈕等元素被收合
                // 注意：SettingsPage 的可見性由呼叫方 MainWindow 在進入此方法前已處理
                VisualStateManager.GoToState(this, "Idle", false);

                StartGamepadPolling();

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                // 預檢平台可用性，不可用則直接顯示訊息，避免無謂的啟動嘗試與逾時等待
                if (!await ProcessLauncherService.CheckPlatformAvailableAsync(platform))
                {
                    StatusText.Text = string.Format(_resourceLoader.GetString("PlatformNotAvailable"), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                    return;
                }

                // 顯示平台圖示與進度指示
                VisualStateManager.GoToState(this, "Launching", false);
                LaunchIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(platform.IconAsset));

                StatusText.Text = string.Format(_resourceLoader.GetString("Launching"), platformName);

                bool isTimeout = false;
                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 給予足夠的逾時時間來確保平台順利到前景，避免 FSE 重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    int exStyle = GetWindowLong(Hwnd, GWL_EXSTYLE);
                    SetWindowLong(Hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // FSE 模式下已知干擾應用程式可能被最大化並搶走焦點，
                    // 在輪詢前先主動終止，避免視覺干擾與焦點搶奪
                    FseService.KillIgnoredBackgroundServices();

                    // 輪詢前景視窗：一旦前景不再是 OmniConsole，代表平台已到前景，可以安全退出
                    // 超過 slowWarningSeconds 顯示緩和提示，超過 timeoutSeconds 進入失敗流程
                    const int pollIntervalMs = 500;
                    const int slowWarningSeconds = 15;
                    const int timeoutSeconds = 60;

                    bool platformToForeground = false;
                    int elapsed = 0;

                    while (elapsed < timeoutSeconds * 1000)
                    {
                        await Task.Delay(pollIntervalMs);
                        elapsed += pollIntervalMs;
                        IntPtr fg = GetForegroundWindow();
                        if (fg != Hwnd)
                        {
                            if (FseService.IsIgnoredForegroundWindow(fg))
                                continue;
                            platformToForeground = true;
                            break;
                        }
                        if (elapsed == slowWarningSeconds * 1000)
                            VisualStateManager.GoToState(this, "LaunchingSlow", false);
                    }

                    if (platformToForeground)
                    {
                        Application.Current.Exit();
                        return;
                    }

                    // 若逾時仍未取得前景，還原視窗狀態並進入失敗流程
                    SetWindowLong(Hwnd, GWL_EXSTYLE, exStyle);
                    success = false;
                    isTimeout = true;
                }

                if (!success)
                {
                    // 啟動失敗：切換至 LaunchError 狀態（VSM 負責隱藏圖示/進度圈，顯示操作按鈕）
                    string errorStringKey = isTimeout ? "LaunchTimeout" : "LaunchFailed";
                    StatusText.Text = string.Format(_resourceLoader.GetString(errorStringKey), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                }
            }
            finally
            {
                _isLaunching = false;
            }
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重設啟動狀態並重新啟動平台。
        /// </summary>
        public async void Reactivate()
        {
            _hasLaunchedOnce = false;
            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示，引導使用者透過 XboxFullScreenExperienceTool 工具啟用。
        /// </summary>
        public void ShowFseNotAvailable()
        {
            Services.DebugLogger.Log("ShowFseNotAvailable: FSE not available.");
            StatusText.Text = _resourceLoader.GetString("FseNotAvailable");
            VisualStateManager.GoToState(this, "FseNotAvailable", false);
            EnableFseButton.Focus(FocusState.Programmatic);
            StartGamepadPolling();
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示，只引導使用者至設定頁面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            Services.DebugLogger.Log("ShowFseHomeAppNotSet: FSE Home App not set to OmniConsole.");
            StatusText.Text = _resourceLoader.GetString("FseHomeAppNotSet");
            VisualStateManager.GoToState(this, "FseHomeAppNotSet", false);
            OpenFseSettingsButton.Focus(FocusState.Programmatic);
            StartGamepadPolling();
        }

        // ── 按鈕事件處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// LaunchPanel 的「開啟設定」按鈕點選處理，切換至設定介面。
        /// 啟動失敗等情境均會顯示此按鈕。
        /// </summary>
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            StopGamepadPolling();
            VisualStateManager.GoToState(this, "Idle", false);
            NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// LaunchPanel 的「返回桌面」按鈕點選處理，觸發全域退出流程。
        /// 啟動失敗等情境均會顯示此按鈕。
        /// </summary>
        private void ReturnToDesktopButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 若 Xbox Full Screen Experience Tool 已安裝則直接啟動，否則開啟 GitHub 下載頁面。OmniConsole 保持開啟。
        /// </summary>
        private async void EnableFseButton_Click(object _, RoutedEventArgs __)
        {
            const string toolExePath = @"C:\Program Files\8bit2qubit\Xbox FullScreen Experience Tool\XboxFullScreenExperienceTool.exe";
            if (System.IO.File.Exists(toolExePath))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(toolExePath) { UseShellExecute = true });
            else
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri("https://github.com/8bit2qubit/XboxFullScreenExperienceTool"));
        }

        /// <summary>
        /// 開啟 Windows 設定中的全螢幕體驗頁面。
        /// </summary>
        private async void OpenFseSettingsButton_Click(object _, RoutedEventArgs __)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:gaming-fullscreen"));
        }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 手把輸入處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 LaunchPanel 的手把輪詢，使 A 鍵可觸發按鈕，B 鍵可退出。
        /// </summary>
        public void StartGamepadPolling()
        {
            _launchPanelGamepadService ??= new GamepadNavigationService(
                this.LaunchPanel,
                this.DispatcherQueue,
                OnLaunchPanelGamepadAButtonPressed,
                OnGamepadBButtonPressed
            );
            _launchPanelGamepadService.Start();
        }

        /// <summary>
        /// 停止 LaunchPanel 的手把輪詢。
        /// </summary>
        public void StopGamepadPolling()
        {
            _launchPanelGamepadService?.Stop();
        }

        /// <summary>
        /// LaunchPanel 中手把 'A' 鍵的處理：焦點在按鈕時觸發點選。
        /// </summary>
        private void OnLaunchPanelGamepadAButtonPressed()
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.XamlRoot);
            if (ReferenceEquals(focused, OpenSettingsButton))
                OpenSettingsButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, ReturnToDesktopButton))
                ReturnToDesktopButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, EnableFseButton))
                EnableFseButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, OpenFseSettingsButton))
                OpenFseSettingsButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 手把 'B' 鍵：觸發退出流程。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
