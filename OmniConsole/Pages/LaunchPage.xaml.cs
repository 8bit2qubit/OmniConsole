using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using System.Threading.Tasks;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 啟動畫面 UserControl。
    /// 負責平台自動啟動、FSE 引導畫面及啟動失敗時的操作按鈕。
    /// </summary>
    public sealed partial class LaunchPage : UserControl, IGamepadInputScope
    {
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

                // 讀取快取的更新資訊，有新版時顯示 InfoBar
                ShowUpdateInfoBarIfNeeded();

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                // 預檢平台可用性，不可用則直接顯示訊息，避免無謂的啟動嘗試與逾時等待
                if (!await ProcessLauncherService.CheckPlatformAvailableAsync(platform))
                {
                    StatusText.Text = string.Format(_resourceLoader.Loc("PlatformNotAvailable"), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusStateHelper.Preferred);
                    return;
                }

                // 顯示平台圖示與進度指示
                VisualStateManager.GoToState(this, "Launching", false);
                LaunchIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(platform.IconAsset));

                StatusText.Text = string.Format(_resourceLoader.Loc("Launching"), platformName);

                bool isTimeout = false;
                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 給予足夠的逾時時間來確保平台順利到前景，避免 FSE 重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.Loc("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    int exStyle = WindowForegroundService.GetExStyle(Hwnd);
                    WindowForegroundService.SetExStyle(Hwnd, exStyle | WindowForegroundService.WS_EX_TOOLWINDOW);

                    // [Windows Bug] 部分應用程式在 FSE 中會被最大化並搶走前景焦點，
                    // 在輪詢前先終止，避免干擾前景判定。
                    // 從 OmniConsole 進入 FSE 時已在 App.xaml.cs 預先清理，
                    // 但 Win+F11、工作檢視、開機自動進入等路徑不經過該清理，仍需此防禦。
                    FseService.KillIgnoredBackgroundServices();

                    // 輪詢前景視窗：一旦前景確實是目標平台（非過渡窗）即可安全退出。
                    const int pollIntervalMs = 500;
                    const int slowWarningSeconds = 20;
                    const int timeoutSeconds = 60;            // 「平台沒起來」的逾時（前景一直是殼層／過渡窗）
                    const int extendedTimeoutSeconds = 300;   // 「平台啟動中但慢」的寬限硬上限（全新 Steam 跨更新可達數分鐘）

                    bool platformToForeground = false;
                    int elapsed = 0;

                    IntPtr lastFg = IntPtr.Zero;
                    DebugLogger.Log($"[LP-DIAG] poll start self=0x{Hwnd.ToInt64():X} platform={platform.Id}");

                    bool slowWarningShown = false;
                    bool sawBootstrap = false;   // 本次啟動「曾見過該平台 bootstrap 在前景：證明平台確實在啟動、給寬限
                    while (elapsed < extendedTimeoutSeconds * 1000)
                    {
                        await Task.Delay(pollIntervalMs);
                        elapsed += pollIntervalMs;
                        IntPtr fg = WindowForegroundService.GetForeground();

                        if (!slowWarningShown && elapsed >= slowWarningSeconds * 1000)
                        {
                            slowWarningShown = true;
                            VisualStateManager.GoToState(this, "LaunchingSlow", false);
                        }

                        var ev = WindowForegroundService.EvaluatePlatformForeground(
                            Hwnd,
                            ProcessLauncherService.GetEffectiveForegroundProcessNames(platform),
                            ProcessLauncherService.GetEffectiveForegroundAumidSubstrings(platform));
                        if (ProcessLauncherService.IsForegroundLaunchingPlatform(ev.fgProc, ev.fgPid, platform))
                            sawBootstrap = true;

                        if (fg != lastFg)
                        {
                            lastFg = fg;
                            bool ig = (fg != Hwnd) && FseService.IsIgnoredForegroundWindow(fg);
                            DebugLogger.Log($"[LP-DIAG] t={elapsed}ms ignored={ig} sawBootstrap={sawBootstrap} {WindowForegroundService.ForegroundFocusSnapshot(Hwnd)}");
                        }

                        if (elapsed >= timeoutSeconds * 1000 && !sawBootstrap)
                        {
                            DebugLogger.Log($"[LP-DIAG] t={elapsed}ms timeout (fgProc={ev.fgProc} not platform bootstrap), give up");
                            break;
                        }

                        if (fg != Hwnd)
                        {
                            if (FseService.IsIgnoredForegroundWindow(fg))
                                continue;

                            bool ready = ev.hasExpected
                                ? (ev.procMatch || ev.aumidMatch)
                                : ev.focusOnFg;

                            if (!ready)
                            {
                                DebugLogger.Log($"[LP-DIAG] t={elapsed}ms NOT ready (fgProc={ev.fgProc} hasExp={ev.hasExpected} procMatch={ev.procMatch} aumidMatch={ev.aumidMatch} focusOnFg={ev.focusOnFg} sawBootstrap={sawBootstrap}), keep waiting");
                                continue;
                            }
                            platformToForeground = true;
                            break;
                        }
                    }

                    if (platformToForeground)
                    {
                        DebugLogger.Log($"[LP-DIAG] >>> EXIT DECISION (platform to fg). FINAL: {WindowForegroundService.ForegroundFocusSnapshot(Hwnd)}");
                        // FSE 環境下啟動 PhantomKey 手把輸入服務（常駐，不再檢查使用者開關）
                        //if (FseService.IsActive() && SettingsService.GetUsePhantomKey())
                        if (FseService.IsActive())
                            PhantomKeyService.Start();

                        WindowForegroundService.Hide(Hwnd);
                        App.ExitApp();
                        return;
                    }

                    // 若逾時仍未取得前景，還原視窗狀態並進入失敗流程
                    WindowForegroundService.SetExStyle(Hwnd, exStyle);
                    success = false;
                    isTimeout = true;
                }

                if (!success)
                {
                    // 啟動失敗：切換至 LaunchError 狀態（VSM 負責隱藏圖示/進度圈，顯示操作按鈕）
                    string errorStringKey = isTimeout ? "LaunchTimeout" : "LaunchFailed";
                    StatusText.Text = string.Format(_resourceLoader.Loc(errorStringKey), platformName);
                    VisualStateManager.GoToState(this, "LaunchError", false);
                    OpenSettingsButton.Focus(FocusStateHelper.Preferred);
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
        /// handheldRequired=true 時顯示「偵測到 PC 限制版、需要掌機完整版」訊息（IsSupported=true 但 DeviceForm≠46）。
        /// </summary>
        public void ShowFseNotAvailable(bool handheldRequired = false)
        {
            string resourceKey = handheldRequired ? "FseHandheldRequired" : "FseNotAvailable";
            DebugLogger.Log($"ShowFseNotAvailable: handheldRequired={handheldRequired}");
            StatusText.Text = _resourceLoader.Loc(resourceKey);
            VisualStateManager.GoToState(this, "FseNotAvailable", false);
            EnableFseButton.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示，只引導使用者至設定頁面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            DebugLogger.Log("ShowFseHomeAppNotSet: FSE Home App not set to OmniConsole.");
            StatusText.Text = _resourceLoader.Loc("FseHomeAppNotSet");
            // 含品牌名：由 code-behind 設 Content（注入品牌）。
            OpenFseSettingsButton.Content = _resourceLoader.Loc("OpenFseSettingsButton");
            VisualStateManager.GoToState(this, "FseHomeAppNotSet", false);
            OpenFseSettingsButton.Focus(FocusStateHelper.Preferred);
        }

        // ── 按鈕事件處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// LaunchPanel 的「開啟設定」按鈕點選處理，切換至設定介面。
        /// 啟動失敗等情境均會顯示此按鈕。
        /// </summary>
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
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

        // ── IGamepadInputScope ──
        // 輪詢計時器由 MainWindow 集中管理；本頁只宣告「焦點搜尋根 + A/B 語意」。

        /// <summary>焦點搜尋根：D-pad 在啟動面板內找下一個焦點元素。</summary>
        UIElement IGamepadInputScope.SearchRoot => this.LaunchPanel;

        /// <summary>A 鍵：焦點在按鈕時觸發點選。</summary>
        void IGamepadInputScope.OnA() => OnLaunchPanelGamepadAButtonPressed();

        /// <summary>B 鍵：觸發退出流程。回 true 表示已處理。</summary>
        bool IGamepadInputScope.OnB() { OnGamepadBButtonPressed(); return true; }

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

        // ── 更新通知 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 依快取的 UpdateKind 顯示 InfoBar（唯讀通知）。
        /// </summary>
        public void ShowUpdateInfoBarIfNeeded()
        {
            if (!SettingsService.GetAutoUpdateCheckEnabled())
            {
                UpdateInfoBar.IsOpen = false;
                return;
            }

            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                var plKey = SettingsService.GetUseGameBarLibraryForSettings()
                    ? "UpdateInfoBar_MissingPhantomLink_Launch_GameBar"
                    : "UpdateInfoBar_MissingPhantomLink_Launch_StartMenu";
                UpdateInfoBar.Message = _resourceLoader.Loc(plKey);
                UpdateInfoBar.IsOpen = true;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                var key = SettingsService.GetUseGameBarLibraryForSettings()
                    ? "UpdateAvailable_InfoBar_Launch_GameBar"
                    : "UpdateAvailable_InfoBar_Launch_StartMenu";
                UpdateInfoBar.Message = string.Format(
                    _resourceLoader.Loc(key), cached);
                UpdateInfoBar.IsOpen = true;
            }
            else
            {
                UpdateInfoBar.IsOpen = false;
            }
        }
    }
}
