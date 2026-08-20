using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using System.Threading.Tasks;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 啟動畫面 Page。
    /// 負責平台自動啟動、FSE 引導畫面及啟動失敗時的操作按鈕。
    /// </summary>
    public sealed partial class LaunchView : Page, IGamepadInputScope
    {
        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>啟動失敗或需要進行設定時，由 MainWindow 切換至設定介面。</summary>
        public event EventHandler? NavigateToSettingsRequested;

        /// <summary>使用者點選「返回桌面」或手把 B 鍵時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供輪詢前景判定與退場隱藏視窗使用。</summary>
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

        // 啟動流程世代序號：每輪啟動遞增，舊流程在各 await 檢查點發現序號不符即自行退場。
        // 宣告為 static 讓序號跨頁面實例共用，頁面被 Frame 重建時舊實例的殘留流程一併失效。
        private static int s_launchGeneration = 0;

        public LaunchView()
        {
            InitializeComponent();
        }

        // ── 平台啟動 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 先預檢可用性，不可用則顯示錯誤訊息；啟動成功後隱藏視窗，
        /// 輪詢前景視窗確認平台已到前景後結束應用程式。
        /// <paramref name="restart"/>=true 時取代仍在進行中的啟動流程重新開始（首頁按鈕重導等）；
        /// false 時若已有流程進行中則不重複觸發。
        /// 平台 bootstrap 已在前景出現過（平台確實在啟動中）時，restart 也不重跑，維持原輪等待。
        /// </summary>
        public async Task LaunchDefaultPlatformAsync(bool restart = false)
        {
            if (_isLaunching)
            {
                if (!restart) return;

                // 平台 bootstrap 已在前景出現過（如 Steam 啟動前更新）＝平台確實在啟動中：維持原輪等待、不重跑，保留寬限逾時；
                // 重跑後新一輪看不到已過去的 bootstrap，會落回基本逾時提早放棄
                if (WindowForegroundService.HasSeenPlatformBootstrap) return;
            }

            // 首次執行或版本更新時不自動啟動，轉至設定介面讓使用者確認預設平台
            if (SettingsService.IsFirstRunOrUpdate())
            {
                NavigateToSettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            // 遞增世代序號使進行中的舊流程失效；卡在前景輪詢的舊流程由 WaitForPlatformForegroundAsync 在新一輪進入時取消喚醒
            int generation = ++s_launchGeneration;
            _isLaunching = true;

            try
            {
                // 開機時可能在使用者登入前就被啟動：等互動 session 就緒（使用者已登入且解鎖）
                // 再啟動平台，避免 Windows 在登入畫面後面就把平台跑起來。全平台一體適用；
                // 鎖定期間掛在解鎖事件上駐留等待，解鎖後秒醒接續啟動
                await WindowForegroundService.WaitForInteractiveSessionAsync(Hwnd);

                // 已有較新的啟動流程接手：本輪退場、不再動 UI
                if (generation != s_launchGeneration) return;

                // 重設為初始狀態，確保上次失敗殘留的按鈕等元素被收合
                VisualStateManager.GoToState(this, "Idle", false);

                // 讀取快取的更新資訊，有新版時顯示 InfoBar
                ShowUpdateInfoBarIfNeeded();

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                // 預檢平台可用性，不可用則直接顯示訊息，避免無謂的啟動嘗試與逾時等待
                bool available = await ProcessLauncherService.CheckPlatformAvailableAsync(platform);

                // 已有較新的啟動流程接手：本輪退場、不再動 UI
                if (generation != s_launchGeneration) return;

                if (!available)
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

                // 平台已在另一種模式下執行（如 Playnite 桌面版）就不啟動，改等它取得前景後讓開。
                string? deferTarget = ProcessLauncherService.GetRunningDeferTarget(platform);
                var waitTarget = platform;
                bool success;

                if (deferTarget != null)
                {
                    DebugLogger.Log($"[LaunchView] defer to running '{deferTarget}', skip launching {platform.Id}");
                    waitTarget = platform with
                    {
                        ForegroundProcessNames = ProcessLauncherService.GetEffectiveDeferToProcessNames(platform),
                    };
                    success = true;
                }
                else
                {
                    success = await ProcessLauncherService.LaunchPlatformAsync(platform);
                }

                _hasLaunchedOnce = true;

                // 已有較新的啟動流程接手：本輪退場、不再動 UI
                if (generation != s_launchGeneration) return;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 給予足夠的逾時時間來確保平台順利到前景，避免 FSE 重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.Loc("LaunchSuccess"), platformName);

                    // [Windows Bug] 部分應用程式在 FSE 中會被最大化並搶走前景焦點，
                    // 在輪詢前先終止，避免干擾前景判定。
                    // 從 OmniConsole 進入 FSE 時已在 App.xaml.cs 預先清理，
                    // 但 Win+F11、工作檢視、開機自動進入等路徑不經過該清理，仍需此防禦。
                    FseService.KillIgnoredBackgroundServices();

                    // 輪詢前景視窗，等目標平台就位（非過渡窗）後回傳。
                    // 慢啟動達提示時機時回呼，於此切 UI 狀態顯示「啟動較慢」提示。
                    bool platformToForeground = await WindowForegroundService.WaitForPlatformForegroundAsync(
                        Hwnd,
                        waitTarget,
                        () => VisualStateManager.GoToState(this, "LaunchingSlow", false));

                    // 已有較新的啟動流程接手：本輪退場、不再動 UI 也不退出應用程式
                    if (generation != s_launchGeneration) return;

                    if (platformToForeground)
                    {
                        // FSE 環境下啟動 PhantomKey 手把輸入服務（常駐，不再檢查使用者開關）
                        //if (FseService.IsActive() && SettingsService.GetUsePhantomKey())
                        if (FseService.IsActive())
                            PhantomKeyService.Start();

                        WindowForegroundService.Hide(Hwnd);

                        await PlatformWindowService.OnShellHiddenAsync();

                        App.ExitApp();
                        return;
                    }

                    // 逾時仍未取得前景，進入失敗流程
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
            catch (OperationCanceledException)
            {
                // 前景輪詢被較新一輪啟動流程取消：不動 UI，交由新流程接手
            }
            finally
            {
                // 僅當本輪仍是最新世代才歸還旗標，避免被取代的舊流程清掉新流程的進行中狀態
                if (generation == s_launchGeneration)
                    _isLaunching = false;
            }
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重設啟動狀態並重新啟動平台。
        /// 前一輪啟動流程仍在等待平台就位時，取消該輪重跑。
        /// </summary>
        public async void Reactivate()
        {
            _hasLaunchedOnce = false;
            await LaunchDefaultPlatformAsync(restart: true);
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

        /// <summary>A 鍵：等同點選焦點按鈕（AutomationPeer Invoke）。</summary>
        void IGamepadInputScope.OnA() => GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);

        /// <summary>B 鍵：觸發退出流程。回 true 表示已處理。</summary>
        bool IGamepadInputScope.OnB() { OnGamepadBButtonPressed(); return true; }

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
