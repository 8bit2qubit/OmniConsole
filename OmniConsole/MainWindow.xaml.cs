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

        // 頂層 Frame 導覽的兩個畫面：各為 Page、設 NavigationCacheMode=Enabled 由框架快取重用。
        // LaunchView 持有可達數分鐘的啟動輪詢、SettingsHostView 持有跨進出常駐狀態與安裝流程，故留著重用而非每次切換重建。
        // 這兩個欄位是目前 Frame 內容的快取參照：每次 NavigateRoot 後從 Frame.Content 取得並重訂事件、注入 HWND。
        private OmniConsole.Pages.LaunchView? _launchView;
        private OmniConsole.Pages.Settings.SettingsHostView? _settingsHostView;

        /// <summary>
        /// 全域唯一的手把導航服務（Pull 模型）。整個 App 生命週期單一實例、計時器全程運轉，
        /// 每 Tick 自動解析目前最頂層 modal dialog 或目前 page scope。
        /// </summary>
        private GamepadNavigationService? _gamepad;

        // Content.Loaded 觸發時 SetResult，標記 XamlRoot 此後可用於 ContentDialog
        private readonly TaskCompletionSource _visualTreeReady = new();

        // 提權工作版本檢查每次啟動只做一次；Activated 會在視窗每次取得焦點時重複觸發。
        private bool _elevatedServiceChecked;
        private bool _gamingCapabilityChecked;

        // 啟動前的兩道詢問閘門是否正在進行中。
        private bool _startupGateRunning;

        // 啟動頁詢問的次數上限。到了上限就放行去啟動平台，設定頁不受此限。
        private const int StartupGateMaxAttempts = 3;

        /// <summary>
        /// 更新安裝期間設為 true，AppWindow.Closing 與 ESC/B 鍵退出路徑均拒絕關閉。
        /// 由 SettingsHostView.RunInstallBundleWithDialogAsync 在開始/結束時切換。
        /// </summary>
        public static bool IsUpdateInstallInProgress { get; set; }

        /// <summary>
        /// 整段安裝流程（含前置 AppsUsingPhantomPawDialog + 下載 + 安裝）期間設為 true。
        /// 由 SettingsHostView.RunInstallBundleWithDialogAsync 包整段 try/finally；
        /// 較 IsUpdateInstallInProgress 更早 true、更晚 false：開頭涵蓋下載前的前置對話方塊等待，
        /// 結尾在 Phase2Install 把 IsUpdateInstallInProgress 設 false（讓 OS graceful close 通過）後仍維持 true。
        /// 供外部入口（開始功能表 / Game Bar 首頁 / 媒體櫃）的 redirect handler 提前 return。
        /// </summary>
        public static bool IsInstallFlowInProgress { get; set; }

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();

            // 套用持久化的背景材質（視窗背景：設根 Grid 背景與自繪桌布玻璃層）。
            ApplyBackgroundMaterialToWindow(SettingsService.GetBackgroundMaterial());

            // MSIX 更新後 LocalSettings 保留，若快取的新版本不再大於目前版本則清除，
            // 避免 InfoBar 誤顯示「有新版可下載」
            UpdateCheckService.InvalidateCacheIfCurrentVersion();

            // 設定工作檢視與工作列圖示（使用套件內 Assets 的圖示）
            var iconPath = System.IO.Path.Combine(
                Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                "Assets", "AppIcon.ico");
            this.AppWindow.SetIcon(iconPath);

            // 開場導覽到啟動頁；NavigateRoot 後 _launchView 即就緒，供下方建立手把服務。
            // settings 啟動路徑會在隨後由 ShowSettings 切到 SettingsHostView。
            NavigateRoot(typeof(OmniConsole.Pages.LaunchView));

            this.Activated += MainWindow_Activated;

            // 監聽 Content.Loaded 作為 XamlRoot 可用的訊號
            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.Loaded += (_, _) =>
                {
                    _visualTreeReady.TrySetResult();
                    // 深連結直開手把映射編輯器時，編輯器已自行於頁首卡片點亮白框，略過通用開場點亮。
                    if (_settingsHostView?.IsGamepadMappingEditorVisible == true)
                        return;
                    // FSE 開場點亮焦點白框
                    var scope = RootContentFrame.Content is OmniConsole.Pages.LaunchView
                        ? (DependencyObject)_launchView!
                        : _settingsHostView!;
                    FocusStateHelper.PrimeFirstFocusable(scope, this.DispatcherQueue);
                };
            }

            // AppWindow 層級的關閉請求（X 按鈕、Task View 關閉、Alt+F4 等）。
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

            // 建立全域唯一手把導航服務並注入給 GamepadDialogBase，須早於任何對話方塊開啟。
            // 初始 page scope 為 LaunchView，計時器全程運轉：之後切頁只換 scope、對話方塊開關只進出 _openDialogs，都不需重啟計時器。
            _gamepad = new GamepadNavigationService(this.DispatcherQueue, _launchView!);
            GamepadDialogBase.AttachService(_gamepad);
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
        /// 處理視窗啟動事件：注入 HWND，並在符合條件時自動啟動預設平台。
        /// </summary>
        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在視窗取得前景焦點時啟動，且防止重入
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;

            // 把主視窗 HWND 注入給 LaunchView 與 SettingsHostView，供其前景與視窗操作使用。
            // 已建立的 Page 立即注入；尚未導覽過的 Page 由 NavigateRoot 在其首次導覽時補注入。
            _hwnd = WindowNative.GetWindowHandle(this);
            if (_launchView != null) _launchView.Hwnd = _hwnd;
            if (_settingsHostView != null) _settingsHostView.Hwnd = _hwnd;

            // 系統的權限要求會讓視窗先失焦，關閉後再取得焦點，於是這個處理常式被重新叫進來一次。
            // 下面兩道閘門都是「檢查過就直接返回」，重入的那一次會穿過去把平台啟動起來，
            // 而第一次那條路的對話方塊還開著。閘門進行中就整個不處理，等第一次走完再啟動平台。
            if (_startupGateRunning) return;
            _startupGateRunning = true;
            try
            {
                // 提權工作版本與套件不符時先擋下來更新。放在設定模式判斷之前：啟動平台與進設定頁兩條分支都會經過這裡，不論停在哪一頁都問得到。
                await TryUpdateElevatedServiceAsync();

                // 缺完整體驗所需設定的機器在這裡補上。同樣放在設定模式判斷之前，兩條分支都問得到。
                await TryEnsureGamingCapabilityAsync();
            }
            finally
            {
                _startupGateRunning = false;
            }

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (_isShowingSettings) return;

            // 確保視覺樹已經載入完畢（由 Loaded 觸發完成）
            if (!_visualTreeReady.Task.IsCompleted)
            {
                await _visualTreeReady.Task;
            }

            // 已成功完成一次啟動嘗試，不因視窗重新取得焦點而再次啟動
            if (_launchView!.HasLaunchedOnce) return;

            await _launchView!.LaunchDefaultPlatformAsync();
        }

        // ── 頂層 Frame 導覽（NavigationCacheMode=Enabled 框架快取重用、不用完即丟）─────────

        /// <summary>目前 Frame 內容是啟動頁時取其參照（非該頁時為 null）。</summary>
        private OmniConsole.Pages.LaunchView? CurrentLaunch => RootContentFrame.Content as OmniConsole.Pages.LaunchView;

        /// <summary>目前 Frame 內容是設定頁時取其參照（非該頁時為 null）。</summary>
        private OmniConsole.Pages.Settings.SettingsHostView? CurrentSettings => RootContentFrame.Content as OmniConsole.Pages.Settings.SettingsHostView;

        /// <summary>
        /// 頂層 Frame 導覽到指定 Page，套用頁面切換動畫。
        /// 兩 Page 設 NavigationCacheMode=Enabled，Navigate 重用框架快取的同一實例、不 new 新的。
        /// 導覽後另給頁元素掛元素自身進場動畫、把 Frame.Content 存進快取參照欄位、
        /// 以具名 handler 重訂事件（-= 再 +=，重用實例避免重複訂閱）、注入 HWND。
        /// </summary>
        private void NavigateRoot(Type pageType)
        {
            FocusNavHelper.NavigatePage(RootContentFrame, pageType);

            FocusNavHelper.ApplyEntranceTransitions(RootContentFrame.Content as UIElement);

            if (RootContentFrame.Content is OmniConsole.Pages.LaunchView launchView)
            {
                _launchView = launchView;
                launchView.NavigateToSettingsRequested -= LaunchView_NavigateToSettingsRequested;
                launchView.NavigateToSettingsRequested += LaunchView_NavigateToSettingsRequested;
                launchView.ExitApplicationRequested -= View_ExitApplicationRequested;
                launchView.ExitApplicationRequested += View_ExitApplicationRequested;
                if (_hwnd != IntPtr.Zero) launchView.Hwnd = _hwnd;
            }
            else if (RootContentFrame.Content is OmniConsole.Pages.Settings.SettingsHostView settingsHostView)
            {
                _settingsHostView = settingsHostView;
                settingsHostView.ExitApplicationRequested -= View_ExitApplicationRequested;
                settingsHostView.ExitApplicationRequested += View_ExitApplicationRequested;
                settingsHostView.LaunchPlatformDirectlyRequested -= SettingsHostView_LaunchPlatformDirectlyRequested;
                settingsHostView.LaunchPlatformDirectlyRequested += SettingsHostView_LaunchPlatformDirectlyRequested;
                settingsHostView.BackgroundMaterialChangeRequested -= SettingsHostView_BackgroundMaterialChangeRequested;
                settingsHostView.BackgroundMaterialChangeRequested += SettingsHostView_BackgroundMaterialChangeRequested;
                if (_hwnd != IntPtr.Zero) settingsHostView.Hwnd = _hwnd;
            }
        }

        /// <summary>具名事件 handler（供 -= 重訂，重用實例避免重複訂閱）。</summary>
        private void LaunchView_NavigateToSettingsRequested(object? sender, EventArgs e) => ShowSettings();
        private void View_ExitApplicationRequested(object? sender, EventArgs e) => RequestExitApplication();
        private void SettingsHostView_LaunchPlatformDirectlyRequested(object? sender, EventArgs e) => LaunchPlatformDirectly();
        private void SettingsHostView_BackgroundMaterialChangeRequested(object? sender, string material) => ApplyBackgroundMaterialToWindow(material);

        // ── 頁面切換 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 切換至設定介面：頂層 Frame 切到 SettingsHostView 並啟動手把輪詢。
        /// </summary>
        public void ShowSettings()
        {
            // 頂層 Frame 切到設定頁；NavigateRoot 後 CurrentSettings 即就緒並已注入 HWND/訂妥事件。
            NavigateRoot(typeof(OmniConsole.Pages.Settings.SettingsHostView));
            var settingsHostView = CurrentSettings!;

            // 切 page scope 到設定頁；全域服務計時器全程運轉，故只需切 scope（即使對話方塊開著時從外部入
            // 口重入也只是重設同一 scope，不會再像舊架構重啟出第二套輪詢）。
            _gamepad?.SetPageScope(settingsHostView);
            _gamepad?.Start();
            _isShowingSettings = true;

            // 設定模式也要全螢幕；用 ActivateFullScreen 雙保險（FSE Activate 前生效、桌面 ready 後補救）
            ActivateFullScreen();

            settingsHostView.ShowSettings();
        }

        /// <summary>
        /// 背景材質的視窗背景：依材質字串設根 Grid 背景與自繪桌布玻璃層。
        /// 啟動套用持久化值與使用者即時切換共用此入口。背景為錦上添花，全程不外拋以免危及啟動。
        /// </summary>
        private void ApplyBackgroundMaterialToWindow(string material)
        {
            try
            {
                RootGrid.Background = BackgroundMaterialService.GetRootBackground(material);
                ApplyWallpaperBackdrop(material);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[MainWindow] ApplyBackgroundMaterialToWindow failed: {ex.Message}");
            }
        }

        /// <summary>自繪桌布玻璃層：依材質把桌布底圖與遮罩兩層元素鋪設就位。</summary>
        private void ApplyWallpaperBackdrop(string material)
        {
            BackgroundMaterialService.ApplyWallpaperLayers(
                material, WallpaperBackdrop, WallpaperScrim, typeof(MainWindow).Assembly);
        }

        /// <summary>
        /// 偵測待續更新狀態，有未完成的階段時彈出確認對話方塊；使用者選擇續做時呼叫
        /// SettingsHostView.RunInstallBundleWithDialogAsync 從中斷的階段接續。
        /// </summary>
        public async Task TryHandlePendingUpdateAsync()
        {
            var (phase, plUrl, mainUrl, targetVersion) = UpdateCheckService.GetPendingUpdateState();
            if (string.IsNullOrEmpty(phase)) return;

            DebugLogger.Log($"[MainWindow] Pending update detected: phase={phase}, target={targetVersion}");

            // 等待 Content.Loaded 取得有效 XamlRoot，再排到 UI 執行緒顯示對話方塊
            await _visualTreeReady.Task;

            // 待續更新路徑必經 App 啟動時的 ShowSettings（已導覽設定頁），故 CurrentSettings 已就緒。
            var settingsHostView = CurrentSettings!;
            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();
                    // 用 GamepadDialogBase 基底類別（A=觸發焦點元素、B=關閉皆自動）；Pull 模型自動讓背景設定頁輪詢避讓。
                    var dialog = new GamepadDialogBase
                    {
                        XamlRoot = this.Content.XamlRoot,
                        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                        RequestedTheme = ElementTheme.Dark,
                        Title = resourceLoader.Loc("ResumeUpdateDialog_Title"),
                        Content = string.Format(
                            resourceLoader.Loc("ResumeUpdateDialog_Content"),
                            targetVersion),
                        PrimaryButtonText = resourceLoader.Loc("ResumeUpdateDialog_Resume"),
                        CloseButtonText = resourceLoader.Loc("ResumeUpdateDialog_Later"),
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
                    await settingsHostView.RunInstallBundleWithDialogAsync(
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
        /// 提權工作的版本與套件不符時引導使用者更新（重跑 install，一次 UAC）。
        /// 沒裝提權工作的人不付任何額外成本。
        /// </summary>
        public async Task TryUpdateElevatedServiceAsync()
        {
            if (_elevatedServiceChecked) return;
            _elevatedServiceChecked = true;

            if (!PhantomSigilService.NeedsUpdate()) return;

            // 等 Content.Loaded 取得有效 XamlRoot，再排到 UI 執行緒顯示對話方塊
            await _visualTreeReady.Task;

            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();

                    // 版本對上之前一直問，關掉對話方塊或在系統的權限要求按取消都會再來一輪。
                    // 元件版本必須一致才能運作正常，故不提供略過的出路；使用者要離開仍可直接關閉視窗。
                    // 唯獨啟動頁有次數上限：問到上限就放行去啟動平台，讓使用者進得了平台，還能接鍵盤滑鼠排查，比被擋在門外進不去好。
                    int maxAttempts = (_isSettingsMode || _isShowingSettings) ? int.MaxValue : StartupGateMaxAttempts;

                    for (int attempt = 1; ; attempt++)
                    {
                        if (!PhantomSigilService.NeedsUpdate()) break;

                        if (attempt > maxAttempts)
                        {
                            DebugLogger.Log($"[MainWindow] Elevated service update: giving up after {maxAttempts} attempts, letting startup continue");
                            break;
                        }

                        // 同一時間只能顯示一個 ContentDialog，且前一個的關閉需要時間。
                        if (attempt > 1) await Task.Delay(500);

                        // ContentDialog 不能重複顯示，每一輪都重建。
                        // 不設 CloseButtonText：更新是繼續使用的前提，不列出與它並排的略過選項。
                        // BlockDismiss 一併擋掉 Esc 與手把 B，只有按下「更新」才關得掉。
                        var dialog = new GamepadDialogBase
                        {
                            XamlRoot = this.Content.XamlRoot,
                            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                            RequestedTheme = ElementTheme.Dark,
                            Title = resourceLoader.Loc("ElevatedServiceUpdateDialog_Title"),
                            Content = resourceLoader.Loc("ElevatedServiceUpdateDialog_Content"),
                            PrimaryButtonText = resourceLoader.Loc("ElevatedServiceUpdateDialog_Update"),
                            DefaultButton = ContentDialogButton.Primary,
                            DismissPolicy = DialogDismissPolicy.RequireChoice
                        };

                        ContentDialogResult result;
                        try
                        {
                            result = await dialog.ShowAsync();
                        }
                        catch (Exception ex)
                        {
                            // 顯示失敗只放棄這一輪，不能讓整個強制更新流程失效
                            DebugLogger.Log($"[MainWindow] Elevated service update attempt {attempt}: ShowAsync failed: {ex.Message}");
                            continue;
                        }

                        if (result == ContentDialogResult.Primary)
                        {
                            bool started = await Task.Run(() => PhantomSigilService.Install());

                            if (started)
                                for (int i = 0; i < 20 && PhantomSigilService.NeedsUpdate(); i++) await Task.Delay(100);
                            bool stillNeedsUpdate = PhantomSigilService.NeedsUpdate();
                            DebugLogger.Log($"[MainWindow] Elevated service update attempt {attempt}: started={started}, stillNeedsUpdate={stillNeedsUpdate}");
                        }
                        else
                        {
                            DebugLogger.Log($"[MainWindow] Elevated service update attempt {attempt}: dismissed, asking again.");
                        }
                    }

                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[MainWindow] Elevated service update failed: {ex.Message}");
                    tcs.SetResult(false);
                }
            });
            await tcs.Task;
        }

        /// <summary>
        /// 這台主機還缺讓 Xbox 模式 (FSE) 以完整體驗運作的設定時，引導使用者補上；已具備則直接返回。
        /// </summary>
        public async Task TryEnsureGamingCapabilityAsync()
        {
            if (_gamingCapabilityChecked) return;
            _gamingCapabilityChecked = true;

            // 上一輪套用到一半被中斷過的話，先把暫時借用的設定還回去。
            GamingCapabilityService.RecoverInterruptedApply();

            if (GamingCapabilityService.Evaluate() != GamingCapabilityState.NeedsSetup) return;

            // 等 Content.Loaded 取得有效 XamlRoot，再排到 UI 執行緒顯示對話方塊
            await _visualTreeReady.Task;

            var tcs = new TaskCompletionSource<bool>();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var resourceLoader = new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();

                    // 套用成功之前一直問，關掉對話方塊或在系統的權限要求按取消都會再來一輪。
                    // 這項設定是完整體驗的前提，故不提供略過的出路；使用者要離開仍可直接關閉視窗。
                    // 唯獨啟動頁有次數上限：問到上限就放行去啟動平台。
                    int maxAttempts = (_isSettingsMode || _isShowingSettings) ? int.MaxValue : StartupGateMaxAttempts;

                    bool applied = false;
                    for (int attempt = 1; ; attempt++)
                    {
                        if (GamingCapabilityService.Evaluate() != GamingCapabilityState.NeedsSetup) break;

                        if (attempt > maxAttempts)
                        {
                            DebugLogger.Log($"[MainWindow] Gaming capability: giving up after {maxAttempts} attempts, letting startup continue");
                            break;
                        }

                        // 同一時間只能顯示一個 ContentDialog，且前一個的關閉需要時間。
                        if (attempt > 1) await Task.Delay(500);

                        // ContentDialog 不能重複顯示，每一輪都重建。
                        // 不設 CloseButtonText：套用是繼續使用的前提，不列出與它並排的略過選項。
                        // BlockDismiss 一併擋掉 Esc 與手把 B，只有按下「套用」才關得掉。
                        var dialog = new GamepadDialogBase
                        {
                            XamlRoot = this.Content.XamlRoot,
                            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                            RequestedTheme = ElementTheme.Dark,
                            Title = resourceLoader.Loc("GamingCapabilityDialog_Title"),
                            Content = resourceLoader.Loc("GamingCapabilityDialog_Content"),
                            PrimaryButtonText = resourceLoader.Loc("GamingCapabilityDialog_Apply"),
                            DefaultButton = ContentDialogButton.Primary,
                            DismissPolicy = DialogDismissPolicy.RequireChoice
                        };

                        ContentDialogResult result;
                        try
                        {
                            result = await dialog.ShowAsync();
                        }
                        catch (Exception ex)
                        {
                            // 顯示失敗只放棄這一輪，不能讓整個流程失效
                            DebugLogger.Log($"[MainWindow] Gaming capability attempt {attempt}: ShowAsync failed: {ex.Message}");
                            continue;
                        }

                        if (result == ContentDialogResult.Primary)
                        {
                            applied = await GamingCapabilityService.TryApplyAsync();
                            DebugLogger.Log($"[MainWindow] Gaming capability attempt {attempt}: applied={applied}");
                        }
                        else
                        {
                            DebugLogger.Log($"[MainWindow] Gaming capability attempt {attempt}: dismissed, asking again.");
                        }
                    }

                    // 只有真的套用成功才提示重新開機。
                    if (applied)
                    {
                        await Task.Delay(500);
                        var doneDialog = new GamepadDialogBase
                        {
                            XamlRoot = this.Content.XamlRoot,
                            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
                            RequestedTheme = ElementTheme.Dark,
                            Title = resourceLoader.Loc("GamingCapabilityRestart_Title"),
                            Content = resourceLoader.Loc("GamingCapabilityRestart_Content"),
                            CloseButtonText = resourceLoader.Loc("GamingCapabilityRestart_Close"),
                        };
                        await doneDialog.ShowAsync();
                    }

                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[MainWindow] Gaming capability flow failed: {ex.Message}");
                    tcs.SetResult(false);
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
        /// 切換回 LaunchView 並重新執行啟動流程。
        /// </summary>
        private void LaunchPlatformDirectly()
        {
            // 頂層 Frame 切回啟動頁；NavigateRoot 後 CurrentLaunch 即就緒。
            NavigateRoot(typeof(OmniConsole.Pages.LaunchView));
            var launchView = CurrentLaunch!;
            _gamepad?.SetPageScope(launchView);
            _isShowingSettings = false;
            launchView.Reactivate();
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
                    ShowFseNotAvailable();
                    return;

                case StartupRoute.GuidanceFseHandheldRequired:
                    ShowFseNotAvailable(handheldRequired: true);
                    return;

                case StartupRoute.GuidanceFseHomeAppNotSet:
                    ShowFseHomeAppNotSet();
                    return;

                case StartupRoute.TryActivateFse:
                    FseService.TryActivate();
                    App.ExitApp();
                    return;

                case StartupRoute.StartWithMainWindow:
                    // 已在 FSE 中：直接重設畫面回 LaunchView 並重啟平台。
                    break;
            }

            _isShowingSettings = false;
            // 頂層 Frame 切回啟動頁；NavigateRoot 後 CurrentLaunch 即就緒。
            var launchView = NavigateToLaunch();
            EnsureFullScreen(); // 維持全螢幕（presenter 通常已是全螢幕，這裡是保險）
            launchView.Reactivate();
        }

        /// <summary>頂層 Frame 切到啟動頁並回傳其參照。FSE 引導/重導等切回啟動頁的路徑共用。</summary>
        private OmniConsole.Pages.LaunchView NavigateToLaunch()
        {
            NavigateRoot(typeof(OmniConsole.Pages.LaunchView));
            return CurrentLaunch!;
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示畫面。
        /// handheldRequired=true 時顯示「偵測到 PC 限制版、需要掌機完整版」訊息。
        /// </summary>
        public void ShowFseNotAvailable(bool handheldRequired = false)
        {
            NavigateToLaunch().ShowFseNotAvailable(handheldRequired);
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示畫面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            NavigateToLaunch().ShowFseHomeAppNotSet();
        }
    }
}
