using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Dialogs;
using OmniConsole.Services;
using OmniConsole.Startup;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        private bool _isSettingsMode = false;
        private bool _isShowingSettings = false;
        private IntPtr _hwnd;
        private CancellationTokenSource? _fseExitCts;

        /// <summary>
        /// 全域唯一的手把導航服務（Pull 模型）。整個 App 生命週期單一實例、計時器全程運轉，
        /// 每 Tick 自動解析目前最頂 modal dialog 或目前 page scope。取代過去「每 page/dialog 各自 new」。
        /// </summary>
        private GamepadNavigationService? _gamepad;

        // Content.Loaded 觸發時 SetResult，標記 XamlRoot 此後可用於 ContentDialog
        private readonly TaskCompletionSource _visualTreeReady = new();

        /// <summary>
        /// 更新安裝期間設為 true，AppWindow.Closing 與 ESC/B 鍵退出路徑均拒絕關閉。
        /// 由 SettingsPage.RunInstallBundleWithDialogAsync 在開始/結束時切換。
        /// </summary>
        public static bool IsUpdateInstallInProgress { get; set; }

        /// <summary>
        /// 整段安裝流程（含前置 AppsUsingPhantomPawDialog + 下載 + 安裝）期間設為 true。
        /// 由 SettingsPage.RunInstallBundleWithDialogAsync 包整段 try/finally；
        /// 較 IsUpdateInstallInProgress 更早 true、更晚 false：開頭涵蓋下載前的前置對話方塊等待，
        /// 結尾在 Phase2Install 把 IsUpdateInstallInProgress 設 false（讓 OS graceful close 通過）後仍維持 true。
        /// 供外部入口（開始功能表 / Game Bar 首頁 / 媒體櫃）的 redirect handler 提前 return。
        /// </summary>
        public static bool IsInstallFlowInProgress { get; set; }

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();

            // MSIX 更新後 LocalSettings 保留，若快取的新版本不再大於目前版本則清除，
            // 避免 InfoBar 誤顯示「有新版可下載」
            UpdateCheckService.InvalidateCacheIfCurrentVersion();

            // 設定工作檢視與工作列圖示（使用套件內 Assets 的圖示）
            var iconPath = System.IO.Path.Combine(
                Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                "Assets", "AppIcon.ico");
            this.AppWindow.SetIcon(iconPath);

            // 訂閱兩個 Page 的導覽與退出事件
            LaunchPageControl.NavigateToSettingsRequested += (_, _) => ShowSettings();
            LaunchPageControl.ExitApplicationRequested += (_, _) => RequestExitApplication();
            SettingsPageControl.ExitApplicationRequested += (_, _) => RequestExitApplication();
            SettingsPageControl.LaunchPlatformDirectlyRequested += (_, _) => LaunchPlatformDirectly();

            this.Activated += MainWindow_Activated;

            // 監聽 Content.Loaded 作為 XamlRoot 可用的訊號
            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.Loaded += (_, _) =>
                {
                    _visualTreeReady.TrySetResult();
                    // FSE 開場點亮焦點框
                    var scope = LaunchPageControl.Visibility == Visibility.Visible
                        ? (DependencyObject)LaunchPageControl
                        : SettingsPageControl;
                    FocusStateHelper.PrimeFirstFocusable(scope, this.DispatcherQueue);
                };
            }

            // AppWindow 層級的關閉請求（X 鈕、Task View 關閉、Alt+F4 等）。
            // 更新安裝期間一律拒絕；否則在視窗關閉前釋放手把導覽服務的系統級資源，
            // 涵蓋不經過 App.ExitApp 的關閉路徑。
            this.AppWindow.Closing += (s, e) =>
            {
                if (IsUpdateInstallInProgress)
                {
                    DebugLogger.Log("[MainWindow] AppWindow.Closing blocked: update install in progress");
                    e.Cancel = true;
                    return;
                }
                DebugLogger.Log("[MainWindow] AppWindow.Closing: disposing gamepad services");
                DisposeGamepadServices();
            };

            // 建立全域唯一手把導航服務並注入給 GamepadDialog（須早於任何 dialog 開啟）。
            // 初始 page scope = LaunchPage（啟動先顯示啟動頁），計時器全程運轉：之後切頁只換 scope、
            // dialog 開關只進出 _openDialogs，都不需重啟計時器。
            _gamepad = new GamepadNavigationService(this.DispatcherQueue, LaunchPageControl);
            GamepadDialog.AttachService(_gamepad);
            _gamepad.Start();
        }

        /// <summary>
        /// 在 Activate() 之前呼叫，標記為設定模式，防止 Activated 事件觸發平台啟動。
        /// </summary>
        public void PrepareForSettings()
        {
            _isSettingsMode = true;
        }

        /// <summary>
        /// 全螢幕的唯一進入點：切換至全螢幕 Presenter（已是全螢幕則略過）。所有需要全螢幕的
        /// 路徑（啟動、進設定、返回啟動頁）都呼叫這裡。
        /// </summary>
        private void EnsureFullScreen()
        {
            if (this.AppWindow.Presenter?.Kind != AppWindowPresenterKind.FullScreen)
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        /// <summary>
        /// 啟動流程呼叫：讓視窗以全螢幕首次顯示。雙保險涵蓋兩種環境：
        /// - FSE 環境：Activate() 之前 SetPresenter(FullScreen)。
        /// - 桌面環境：window 首次顯示前的 SetPresenter 會被 OS 忽略（新 OS build 26220.8491+ 上冷啟動會帶標題列），
        ///   故 Activate() 後再於 DispatcherQueue 補一次 EnsureFullScreen（window ready 後才可靠生效）。
        /// </summary>
        public void ActivateFullScreen()
        {
            EnsureFullScreen();                                 // FSE：Activate 前即全螢幕
            this.Activate();
            this.DispatcherQueue.TryEnqueue(EnsureFullScreen);  // 桌面：window ready 後補設、救回全螢幕
        }

        /// <summary>
        /// 處理視窗啟動事件，負責初始化全螢幕狀態並在符合條件時自動啟動預設平台。
        /// </summary>
        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在視窗取得前景焦點時啟動，且防止重入
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;

            // 注入 HWND 至兩個 Page（LaunchPage 供 WS_EX_TOOLWINDOW 設定，SettingsPage 供 ShowWindow 退出隱藏使用）
            _hwnd = WindowNative.GetWindowHandle(this);
            LaunchPageControl.Hwnd = _hwnd;
            SettingsPageControl.Hwnd = _hwnd;

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (_isShowingSettings) return;

            // 已成功完成一次啟動嘗試，不因視窗重新取得焦點而再次啟動
            if (LaunchPageControl.HasLaunchedOnce) return;

            await LaunchPageControl.LaunchDefaultPlatformAsync();
        }

        // ── 頁面切換 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 切換至設定介面：隱藏 LaunchPage、顯示 SettingsPage 並啟動手把輪詢。
        /// </summary>
        public void ShowSettings()
        {
            // 切 page scope 到設定頁；全域服務計時器全程運轉，故只需切 scope（即使 dialog 開著時從外部入
            // 口重入也只是重設同一 scope，不會再像舊架構重啟出第二套輪詢）。
            _gamepad?.SetPageScope(SettingsPageControl);
            _gamepad?.Start();
            _isShowingSettings = true;
            LaunchPageControl.Visibility = Visibility.Collapsed;
            SettingsPageControl.Visibility = Visibility.Visible;

            // 設定模式也要全螢幕；用 ActivateFullScreen 雙保險（FSE Activate 前生效、桌面 ready 後補救）
            ActivateFullScreen();

            SettingsPageControl.ShowSettings();
        }

        /// <summary>
        /// 偵測待續更新狀態，有未完成的階段時彈出確認對話方塊；使用者選擇續做時呼叫
        /// SettingsPage.RunInstallBundleWithDialogAsync 從中斷的階段接續。
        /// </summary>
        public async Task TryHandlePendingUpdateAsync()
        {
            var (phase, plUrl, mainUrl, targetVersion) = UpdateCheckService.GetPendingUpdateState();
            if (string.IsNullOrEmpty(phase)) return;

            DebugLogger.Log($"[MainWindow] Pending update detected: phase={phase}, target={targetVersion}");

            // 等待 Content.Loaded 取得有效 XamlRoot，再排到 UI 執行緒顯示對話方塊
            await _visualTreeReady.Task;

            var settingsPage = SettingsPageControl;
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var loader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                    // 用 GamepadDialog 基底類別（A=觸發焦點元素、B=關閉皆自動）；Pull 模型自動讓背景設定頁輪詢避讓。
                    var dialog = new GamepadDialog
                    {
                        XamlRoot = this.Content.XamlRoot,
                        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                        RequestedTheme = ElementTheme.Dark,
                        Title = loader.Loc("ResumeUpdateDialog_Title"),
                        Content = string.Format(
                            loader.Loc("ResumeUpdateDialog_Content"),
                            targetVersion),
                        PrimaryButtonText = loader.Loc("ResumeUpdateDialog_Resume"),
                        CloseButtonText = loader.Loc("ResumeUpdateDialog_Later"),
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var result = await dialog.ShowAsync();
                    DebugLogger.Log($"[MainWindow] Resume dialog: result={result}");

                    if (result != ContentDialogResult.Primary)
                    {
                        tcs.SetResult(false);
                        return;
                    }

                    bool resumeFromPhase2 = phase == "Phase2";
                    // 待續恢復路徑一律走完整 Phase 2 安裝；mainSkippable 僅用於同版本快速重啟
                    await settingsPage.RunInstallBundleWithDialogAsync(
                        plUrl, mainUrl, targetVersion,
                        mainSkippable: false,
                        resumeFromPhase2: resumeFromPhase2);
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[MainWindow] Resume dialog failed: {ex.Message}");
                    tcs.SetException(ex);
                }
            });
            await tcs.Task;
        }

        /// <summary>
        /// app 更新後首啟：把已裝社群語言檔同步到「目前 app 版本」（語言檔按 app 版本分 repo 資料夾）。
        /// 重用 UpdateProgressDialog 顯示逐語言進度（一口氣做完、不重啟）。
        /// </summary>
        public async Task TrySyncLanguagesAsync()
        {
            // 看 ShouldSync 決定是否彈進度 UI。
            var decision = await TranslationRepoService.EvaluateSyncDecisionAsync();
            if (!decision.ShouldSync) return;

            await _visualTreeReady.Task;

            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                UpdateProgressDialog? dialog = null;
                try
                {
                    dialog = new UpdateProgressDialog(this.Content.XamlRoot);
                    dialog.ReportStatus("LanguageSync");
                    dialog.ReportProgress(0);
                    _ = dialog.ShowAsync();

                    var progress = new Progress<(int done, int total)>(p =>
                    {
                        double pct = p.total > 0 ? (double)p.done / p.total * 100 : 100;
                        dialog.ReportProgress(pct);
                    });

                    var result = await TranslationRepoService.SyncAllInstalledLanguagesAsync(progress, default);

                    // 記錄同步結果；versionChanged/today 沿用決策時基準（非現在重算）。
                    TranslationRepoService.RecordSyncOutcome(result.AllSucceeded, decision.VersionChanged, decision.Today);
                    DebugLogger.Log($"[MainWindow] Language sync: {result.Succeeded}/{result.Total}, allOk={result.AllSucceeded}");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[MainWindow] Language sync failed: {ex.Message}");
                }
                finally
                {
                    dialog?.RequestClose();
                    tcs.SetResult(true);
                }
            });
            await tcs.Task;
        }

        // ── 平台啟動 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 手把 Menu 鍵觸發：直接啟動設定頁中已選取的平台，跳過手動 FSE 切換流程。
        /// 切換回 LaunchPage 並重新執行啟動流程。
        /// </summary>
        private void LaunchPlatformDirectly()
        {
            _gamepad?.SetPageScope(LaunchPageControl);
            _isShowingSettings = false;
            SettingsPageControl.Visibility = Visibility.Collapsed;
            LaunchPageControl.Visibility = Visibility.Visible;
            LaunchPageControl.Reactivate();
        }

        // ── 全域退出 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 釋放全域手把導覽服務的系統級資源。應用程式結束前由 App.ExitApp / AppWindow.Closing 呼叫。
        /// </summary>
        public void DisposeGamepadServices()
        {
            _gamepad?.Dispose();
            _gamepad = null;
        }

        /// <summary>
        /// 全域退出邏輯。
        /// 若在設定介面中，直接退出應用程式（返回 FSE）。
        /// 若在其他介面且在 FSE 中，觸發退回桌面對話方塊。若不在則直接退出。
        /// </summary>
        private async void RequestExitApplication()
        {
            // 更新安裝期間忽略所有退出請求（手把 B 鍵 / 設定頁退出按鈕等）
            if (IsUpdateInstallInProgress)
            {
                DebugLogger.Log("[MainWindow] RequestExitApplication ignored: update install in progress");
                return;
            }

            bool fseActive = FseService.IsActive();
            DebugLogger.Log($"[MainWindow] RequestExitApplication: _isShowingSettings={_isShowingSettings}, fseActive={fseActive}");

            // 在設定介面時，不需要詢問退回桌面，直接結束回到原本呼叫的介面（如 FSE）即可
            if (_isShowingSettings)
            {
                _gamepad?.Stop();
                WindowForegroundService.Hide(_hwnd); // 先隱藏視窗，避免 FullScreen presenter 卸載時閃白
                App.ExitApp();
                return;
            }

            // FSE 模式下透過 API 觸發「切換到 Windows 桌面」確認對話方塊
            // 對話方塊期間使用者無法點選 OmniConsole 的按鈕，無需停用
            //   - 確認退出 → StateChanged 回呼觸發，IsActive() 變 false → Exit()
            //   - 取消 → FSE 退出對話方塊消失，OmniConsole 按鈕可正常點選
            //   - 再次點選「返回桌面」按鈕 → 取消前一輪等待，重新觸發
            if (fseActive)
            {
                _fseExitCts?.Cancel();
                _fseExitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var token = _fseExitCts.Token;
                var tcs = new TaskCompletionSource();

                void OnStateChanged()
                {
                    if (!FseService.IsActive())
                        tcs.TrySetResult();
                }

                FseService.StateChanged += OnStateChanged;
                token.Register(() => tcs.TrySetCanceled());

                FseService.TryDeactivate();

                try
                {
                    await tcs.Task;
                    FseService.StateChanged -= OnStateChanged;
                    _gamepad?.Stop();
                    WindowForegroundService.Hide(_hwnd);
                    App.ExitApp();
                    return;
                }
                catch (OperationCanceledException)
                {
                    FseService.StateChanged -= OnStateChanged;
                }
            }
            // 若為一般視窗模式、或是尚未進入 FSE 環境時，一律直接退出應用程式
            else
            {
                _gamepad?.Stop();
                WindowForegroundService.Hide(_hwnd);
                App.ExitApp();
            }
        }

        // ── FSE 引導與重啟入口 ───────────────────────────────────────────────

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重設啟動狀態並重新啟動平台。
        /// 若目前不在 FSE 環境，重新檢查 FSE 條件，避免略過引導畫面直接啟動平台。
        /// </summary>
        public async void Reactivate()
        {
            var route = await StartupOrchestrator.EvaluateFseGuidanceAsync();
            switch (route)
            {
                case StartupRoute.GuidanceFseNotAvailable:
                    LaunchPageControl.ShowFseNotAvailable();
                    return;

                case StartupRoute.GuidanceFseHandheldRequired:
                    LaunchPageControl.ShowFseNotAvailable(handheldRequired: true);
                    return;

                case StartupRoute.GuidanceFseHomeAppNotSet:
                    LaunchPageControl.ShowFseHomeAppNotSet();
                    return;

                case StartupRoute.TryActivateFse:
                    FseService.TryActivate();
                    App.ExitApp();
                    return;

                case StartupRoute.StartWithMainWindow:
                    // 已在 FSE 中：直接重設畫面回 LaunchPage 並重啟平台。
                    break;
            }

            _isShowingSettings = false;
            SettingsPageControl.Visibility = Visibility.Collapsed;
            LaunchPageControl.Visibility = Visibility.Visible;
            EnsureFullScreen(); // 維持全螢幕（presenter 通常已是全螢幕，這裡是保險）
            LaunchPageControl.Reactivate();
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示畫面。
        /// handheldRequired=true 時顯示「偵測到 PC 限制版、需要掌機完整版」訊息。
        /// </summary>
        public void ShowFseNotAvailable(bool handheldRequired = false)
        {
            LaunchPageControl.ShowFseNotAvailable(handheldRequired);
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示畫面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            LaunchPageControl.ShowFseHomeAppNotSet();
        }
    }
}
