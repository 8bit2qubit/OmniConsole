using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 設定介面 UserControl。
    /// 負責平台卡片管理、NavigationView 頁面切換、自訂平台對話方塊及設定手把輪詢。
    /// </summary>
    public sealed partial class SettingsPage : UserControl
    {
        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>手把 B 鍵（導覽未展開時）或「退出」按鈕點選時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        /// <summary>手把 Menu 鍵觸發，通知 MainWindow 直接啟動目前選取的平台（跳過手動 FSE 切換流程）。</summary>
        public event EventHandler? LaunchPlatformDirectlyRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 ShowWindow (退出隱藏) 使用。</summary>
        public IntPtr Hwnd { get; set; }

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private readonly ResourceLoader _resourceLoader = new();
        private GamepadNavigationService? _gamepadNavigationService;

        // 設定介面的平台卡片清單與目前選取的平台 Id
        private List<PlatformCardItem> _cardItems = [];
        private string _selectedPlatformId = "";

        // 目前顯示的平台分類索引標籤（System / User）
        private string _currentCategoryTag = "System";

        // 目前顯示的設定導覽頁面（General / Advanced / Troubleshoot）
        private string _currentNavTag = "General";

        // 匯出成功提示的自動關閉計時器（2 秒後關閉 TeachingTip）
        private readonly DispatcherTimer _exportTipTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // 關於頁「已複製」InfoBar 的自動關閉計時器（2 秒後關閉）
        private readonly DispatcherTimer _aboutCopyConfirmTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // ContentDialog 重入防護：平板互動模式下 Dialog 關閉動畫較慢，
        // 手把快速按 A 可能在前一個 Dialog 尚未完全移除時觸發第二次 ShowAsync() 導致崩潰
        private bool _isDialogOpen;

        // 防止檢查更新重複觸發
        private bool _isCheckingUpdate;

        // 防止關於頁重新整理重複觸發
        private bool _isRefreshingAbout;

        // 下載更新的取消 token
        private CancellationTokenSource? _downloadCts;

        // ── Win32 API ───────────────────────────────────────────────────────────

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterApplicationRestart(string commandLine, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        public SettingsPage()
        {
            InitializeComponent();
            _exportTipTimer.Tick += (_, _) =>
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;
            };
            _aboutCopyConfirmTimer.Tick += (_, _) =>
            {
                _aboutCopyConfirmTimer.Stop();
                AboutCopyConfirmTeachingTip.IsOpen = false;
            };
        }

        // ── 設定介面初始化 ────────────────────────────────────────────────────

        /// <summary>
        /// 初始化設定介面各控制項狀態，並啟動手把輪詢與平台可用性查詢。
        /// 可見性切換由 <see cref="OmniConsole.MainWindow.ShowSettings"/> 負責，本方法於其後呼叫。
        /// </summary>
        public void ShowSettings()
        {
            // PhantomLink 可能已直接改動 Shared.ini，先從共用儲存同步回 LocalSettings
            SettingsService.ReloadFromSharedStore();

            // 先設好狀態，再賦值 SelectedItem（賦值會觸發 SelectionChanged → UpdateGamepadHints）
            _currentNavTag = "General";
            VisualStateManager.GoToState(this, "General", false);

            // 若目前選取的平台是使用者自訂的，自動切換到「使用者」索引標籤
            var currentPlatform = SettingsService.GetDefaultPlatform();
            bool isUserPlatform = PlatformCatalog.FindById(currentPlatform.Id) == null
                && UserPlatformStore.FindById(currentPlatform.Id) != null;
            _currentCategoryTag = isUserPlatform ? "User" : "System";

            // 初始化 NavigationView，預設選取第一個「一般」項目
            // 賦值觸發 SettingsNav_SelectionChanged → UpdateGamepadHints()，此時狀態已正確
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            PlatformCategoryNav.SelectedItem = isUserPlatform
                ? PlatformCategoryNav.MenuItems[1]
                : PlatformCategoryNav.MenuItems[0];
            LoadPlatformCards();

            // 顯示版本號
            VersionText.Text = $"v{SettingsService.GetAppVersion()}";

            // FSE 不可用時反灰按鈕而非隱藏
            ResetGameBarButton.IsEnabled = FseService.CanActivate();

            // 還原上次儲存的選取狀態
            var current = SettingsService.GetDefaultPlatform();
            _selectedPlatformId = current.Id;

            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }

            UpdateSettingsDescription();

            // PhantomKey 已改為 FSE 常駐，不再有獨立開關。
            // UI 開關雖然註解保留，但這裡直接判斷 FSE → 啟動，確保更新重啟後恢復。
            //UsePhantomKeySwitch.IsOn = SettingsService.GetUsePhantomKey();
            //if (FseService.IsActive() && UsePhantomKeySwitch.IsOn)
            //    PhantomKeyService.Start();
            if (FseService.IsActive())
                PhantomKeyService.Start();

            // 還原 Steam In-Game Overlay 開關狀態（PhantomKey 恆為啟用，此開關恆可用）
            UsePhantomKeySteamInGameOverlaySwitch.IsOn = SettingsService.GetUsePhantomKeySteamInGameOverlay();
            UsePhantomKeySteamInGameOverlaySwitch.IsEnabled = true;

            // 還原 Mouse Mode（Off/Auto/ForceOn）/ 版面配置 / 游標速度，並依內建廠商映射偵測強制停用
            bool builtInMapping = SettingsService.HasBuiltInGamepadMapping();
            string currentMode = builtInMapping ? SettingsService.MouseModeOff : SettingsService.GetMouseMode();
            MouseModeCombo.SelectedIndex = currentMode switch
            {
                SettingsService.MouseModeOff => 0,
                SettingsService.MouseModeForceOn => 2,
                _ => 1,
            };
            MouseModeLayoutSwitch.IsOn = SettingsService.GetMouseModeLayout() == SettingsService.LayoutClassic;

            // 填充游標速度下拉選單並還原選取
            CursorSpeedCombo.Items.Clear();
            foreach (var p in SettingsService.ValidCursorSpeedPercents)
                CursorSpeedCombo.Items.Add($"{p}%");
            int pct = SettingsService.GetCursorSpeedPercent();
            CursorSpeedCombo.SelectedIndex = Array.IndexOf(SettingsService.ValidCursorSpeedPercents, pct);

            ApplyMouseModeEnabledState(builtInMapping);

            // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），強制走 SettingsService 預設值。
            //
            // // 還原 Game Bar 媒體櫃的開關狀態
            // UseGameBarLibrarySwitch.IsOn = SettingsService.GetUseGameBarLibraryForSettings();
            //
            // // 還原 Passthrough 開關狀態
            // EnablePassthroughSwitch.IsOn = SettingsService.GetEnablePassthrough();

            // 還原自動檢查更新開關狀態，並顯示進階區版本號
            AutoUpdateCheckSwitch.IsOn = SettingsService.GetAutoUpdateCheckEnabled();
            AdvancedVersionText.Text = SettingsService.GetAppVersion();

            // 讀取快取的更新資訊
            ShowSettingsUpdateInfoBar();
            ShowCachedUpdateStatus();
            CheckDeveloperMode(); // 未啟用開發人員模式時顯示警告並停用下載按鈕

            // 自動檢查更新（跨日 + 開關啟用時）
            if (UpdateCheckService.ShouldAutoCheck())
                _ = AutoCheckForUpdatesAsync();

            StartGamepadPolling();
        }

        // ── VSM 狀態輔助方法 ─────────────────────────────────────────────────────

        /// <summary>
        /// 依目前導覽頁面、分類索引標籤及免責聲明同意狀態，更新底部手把提示列的按鍵圖示。
        /// 應於 <see cref="_currentNavTag"/> 或 <see cref="_currentCategoryTag"/> 變更後呼叫。
        /// </summary>
        private void UpdateGamepadHints()
        {
            if (_currentNavTag != "General")
            {
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                GamepadHintMenu.Visibility = Visibility.Collapsed;
                return;
            }
            bool showYX = _currentCategoryTag == "User" && SettingsService.GetCustomPlatformConsentAccepted();
            string state = showYX ? "UserTabWithConsent"
                : _currentCategoryTag == "User" ? "UserTabNoConsent"
                : "SystemTab";
            VisualStateManager.GoToState(this, state, false);

            // Menu 提示不依賴 VSM 結果，直接根據條件計算：非 UserTabNoConsent 且在 FSE 中才顯示
            GamepadHintMenu.Visibility = (state != "UserTabNoConsent" && FseService.IsActive())
                ? Visibility.Visible : Visibility.Collapsed;
        }


        // ── NavigationView 事件 ───────────────────────────────────────────────

        /// <summary>
        /// 處理 NavigationView 選項變更，切換內容頁面。
        /// </summary>
        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem selectedItem)
            {
                if (selectedItem.Tag?.ToString() is not string tag) return;

                // 切換頁面並更新提示列
                _currentNavTag = tag;
                VisualStateManager.GoToState(this, tag, false);
                UpdateGamepadHints();

                // 切到關於頁時，每次都重新擷取一次環境快照（PhantomKey 狀態在工作階段中變動）
                if (tag == "About")
                {
                    LoadAboutPageContent();
                }
            }
        }

        // ── 關於頁 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 擷取環境快照並更新關於頁各文字區塊。
        /// 在背景執行緒取資料、再回 UI 執行緒設值。
        /// </summary>
        private async void LoadAboutPageContent()
        {
            if (_isRefreshingAbout) return;
            _isRefreshingAbout = true;

            // 進度環用 Opacity 而非 Visibility 切換：保持佔位（20×20），避免顯隱時推動按鈕 row 寬度。
            RefreshAboutProgressRing.Opacity = 1;
            RefreshAboutProgressRing.IsActive = true;

            var delayTask = Task.Delay(500);
            var snapshot = await Task.Run(() => AboutInfoService.GetEnvironmentSnapshot());
            await delayTask;

            ApplyAboutSnapshot(snapshot);
            RefreshAboutProgressRing.IsActive = false;
            RefreshAboutProgressRing.Opacity = 0;
            _isRefreshingAbout = false;
        }

        /// <summary>
        /// 將 <see cref="AboutInfoService.EnvironmentSnapshot"/> 套用到「關於」分頁的各 UI 欄位。
        /// </summary>
        private void ApplyAboutSnapshot(AboutInfoService.EnvironmentSnapshot s)
        {
            AboutOmniConsoleVersion.Text = LocalizeForUI(s.Versions.OmniConsole);
            AboutPhantomBridgeVersion.Text = LocalizeForUI(s.Versions.PhantomBridge);
            // PhantomKey 同時顯示套件內版本與已部署副本版本，便於診斷複製失敗或舊副本殘留。
            AboutPhantomKeyVersion.Text = s.Versions.PhantomKey == s.Versions.PhantomKeyDeployed
                ? LocalizeForUI(s.Versions.PhantomKey)
                : $"{LocalizeForUI(s.Versions.PhantomKey)} → {LocalizeForUI(s.Versions.PhantomKeyDeployed)}";
            AboutPhantomLinkVersion.Text = LocalizeForUI(s.Versions.PhantomLink);

            // PhantomKey 健康狀況
            ApplyPhantomKeyHealth(s.PhantomKey);

            AboutXfsetToolStatus.Text = FormatXfsetToolForUI(s.Xfset);
            AboutXfsetPhysPanelStatus.Text = FormatPhysPanelForUI(s.Xfset);

            AboutSystemText.Text = $"{LocalizeForUI(s.Hardware.SystemManufacturer)} / {LocalizeForUI(s.Hardware.SystemProductName)}";
            AboutBaseboardText.Text = $"{LocalizeForUI(s.Hardware.BaseboardManufacturer)} / {LocalizeForUI(s.Hardware.BaseboardProduct)}";
            AboutCpuText.Text = FormatCpuForUI(s.Hardware);
            AboutRamText.Text = FormatBytesForUI(s.Hardware.RamTotalBytes);
            AboutGpuText.Text = FormatGpuForUI(s.Hardware);

            AboutWindowsBuildText.Text = LocalizeForUI(s.WindowsBuild);
            AboutFseStateText.Text = s.FseState;

            AboutMaxTouchPointsText.Text = s.MaxTouchPoints == 0
                ? $"{s.MaxTouchPoints} ({_resourceLoader.GetString("MaxTouchPoints_NoTouch")})"
                : s.MaxTouchPoints.ToString(CultureInfo.InvariantCulture);
            AboutLocaleText.Text = LocalizeForUI(s.Locale);
            AboutCapturedAtText.Text = s.CapturedAt.ToString(
                "yyyy-MM-dd HH:mm:ss zzz",
                CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把資料層的固定英文回退字串（"(unknown)" / "(not installed)"）替換為在地化字串供 UI 顯示。
        /// 資料層保持英文常數有助於 Markdown 輸出的可讀性（貼到 GitHub Issue 不會帶非 ASCII 字串）。
        /// </summary>
        private string LocalizeForUI(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw == "(unknown)") return _resourceLoader.GetString("Common_Unknown");
            if (raw == "(not installed)") return _resourceLoader.GetString("Common_NotInstalled");
            return raw;
        }

        /// <summary>
        /// 把 XFSET 主程式安裝狀態格式化為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatXfsetToolForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.ToolInstalled) return _resourceLoader.GetString("XfsetStatus_NotInstalled");
            return $"{_resourceLoader.GetString("XfsetStatus_Installed")} ({x.ToolVersion})";
        }

        /// <summary>
        /// 把 PhysPanelCS 安裝狀態與 touchservice 執行狀態組合為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatPhysPanelForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.PhysPanelInstalled) return _resourceLoader.GetString("XfsetStatus_NotInstalled");

            string touchKey = x.TouchService switch
            {
                AboutInfoService.TouchServiceState.Running => "XfsetStatus_TouchServiceRunning",
                AboutInfoService.TouchServiceState.NotConfigured => "XfsetStatus_TouchServiceNotRunning",
                _ => "XfsetStatus_TouchServiceUnknown",
            };

            return $"{_resourceLoader.GetString("XfsetStatus_Installed")} ({x.PhysPanelVersion}), {_resourceLoader.GetString(touchKey)}";
        }

        /// <summary>
        /// 把位元組數格式化為設定頁顯示用的可讀字串（≥1 GiB 用 GB，否則用 MB）。
        /// </summary>
        private string FormatBytesForUI(ulong bytes)
        {
            if (bytes == 0) return _resourceLoader.GetString("Common_Unknown");
            const double GiB = 1024.0 * 1024.0 * 1024.0;
            double gib = bytes / GiB;
            return gib >= 1.0
                ? gib.ToString("0.# GB", CultureInfo.InvariantCulture)
                : (bytes / (1024.0 * 1024.0)).ToString("0 MB", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把 CPU 頻率（MHz）格式化為設定頁顯示用字串：≥1000 顯示 GHz，否則顯示 MHz。
        /// </summary>
        private string FormatMhzForUI(int mhz)
        {
            if (mhz <= 0) return _resourceLoader.GetString("Common_Unknown");
            return mhz >= 1000
                ? (mhz / 1000.0).ToString("0.00 GHz", CultureInfo.InvariantCulture)
                : $"{mhz} MHz";
        }

        /// <summary>
        /// 把 CPU 名稱、頻率、實體/邏輯核心數組合為設定頁顯示用的單行字串。
        /// </summary>
        private string FormatCpuForUI(AboutInfoService.HardwareInfo h)
        {
            // 顯示為「<名稱> (<時脈>, <實體>C/<邏輯>T)」
            return $"{LocalizeForUI(h.CpuName)} ({FormatMhzForUI(h.CpuMhz)}, {h.CpuPhysicalCores}C/{h.CpuLogicalCores}T)";
        }

        /// <summary>
        /// 把 GPU 清單（名稱、VRAM、驅動程式版本與日期）格式化為設定頁顯示用的多行字串。
        /// </summary>
        private string FormatGpuForUI(AboutInfoService.HardwareInfo h)
        {
            // 多張顯示卡，每張一行；驅動版本/日期各自顯示
            if (h.Gpus.Count == 0) return _resourceLoader.GetString("Common_Unknown");
            return string.Join(Environment.NewLine,
                h.Gpus.Select(g => $"{LocalizeForUI(g.Name)} ({FormatBytesForUI(g.VramBytes)} VRAM, {LocalizeForUI(g.DriverVersion)} / {LocalizeForUI(g.DriverDate)})"));
        }

        /// <summary>
        /// 把 PhantomKeyHealth 紀錄投到對應文字區塊。未在跑時將細節欄為 dash。
        /// </summary>
        private void ApplyPhantomKeyHealth(AboutInfoService.PhantomKeyHealth h)
        {
            if (!h.ProcessRunning)
            {
                AboutPhantomKeyProcessText.Text = _resourceLoader.GetString("PhantomKeyHealth_NotRunning");
                AboutPhantomKeyUptimeText.Text = "—";
                AboutPhantomKeyIntegrityText.Text = "—";
                AboutPhantomKeyResponsivenessText.Text = "—";
                return;
            }

            AboutPhantomKeyProcessText.Text = _resourceLoader.GetString("PhantomKeyHealth_Running");

            AboutPhantomKeyUptimeText.Text = FormatUptimeForUI(h.Uptime);
            AboutPhantomKeyIntegrityText.Text = h.IntegrityLevel == AboutInfoService.IntegrityLevel.Unknown
                ? _resourceLoader.GetString("Common_Unknown")
                : h.IntegrityLevel.ToString();
            AboutPhantomKeyResponsivenessText.Text = FormatResponsivenessForUI(h);
        }

        /// <summary>
        /// 把 PhantomKey Uptime 格式化為設定頁顯示用字串，依量級裁切顯示精度；非正值回 dash。
        /// </summary>
        private static string FormatUptimeForUI(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero) return "—";
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        /// <summary>
        /// 把 PhantomKey 健康分級轉成設定頁顯示用的在地化描述（含延遲毫秒）。
        /// </summary>
        private string FormatResponsivenessForUI(AboutInfoService.PhantomKeyHealth h)
        {
            return h.Responsiveness switch
            {
                AboutInfoService.PhantomKeyResponsiveness.Responsive
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Responsive"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Busy
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Busy"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Stuck
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.GetString("PhantomKeyResp_Stuck"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Hung
                    => _resourceLoader.GetString("PhantomKeyResp_Hung"),
                AboutInfoService.PhantomKeyResponsiveness.NoPingWindow
                    => _resourceLoader.GetString("PhantomKeyResp_NoPingWindow"),
                AboutInfoService.PhantomKeyResponsiveness.NotRunning
                    => _resourceLoader.GetString("PhantomKeyHealth_NotRunning"),
                _ => "—",
            };
        }

        /// <summary>
        /// 複製關於頁的環境快照到剪貼簿，供使用者貼到 GitHub Issue 協助回報問題。
        /// </summary>
        private void CopyAboutButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var snapshot = AboutInfoService.GetEnvironmentSnapshot();
                var markdown = AboutInfoService.FormatAsMarkdown(snapshot);

                var dataPackage = new DataPackage();
                dataPackage.SetText(markdown);
                Clipboard.SetContent(dataPackage);

                // 同步把畫面上的快照重新整理為這次複製的版本，使顯示與剪貼簿一致
                ApplyAboutSnapshot(snapshot);

                AboutCopyConfirmTeachingTip.IsOpen = true;
                _aboutCopyConfirmTimer.Stop();
                _aboutCopyConfirmTimer.Start();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[SettingsPage] CopyAboutButton_Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 重新整理關於頁所有欄位。
        /// 走 LoadAboutPageContent 路徑。
        /// </summary>
        private void RefreshAboutButton_Click(object sender, RoutedEventArgs e)
        {
            LoadAboutPageContent();
        }

        /// <summary>
        /// 依關於頁實際可用寬度切換雙欄/單欄版型。閾值 1416 = 兩欄各 700 + ColumnSpacing 16。
        /// </summary>
        private void AboutPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 用 ViewportWidth（ScrollViewer 實際給內容的可用寬度）而非 e.NewSize.Width。
            const double DualColumnThreshold = 1416;
            double newSize = e.NewSize.Width;
            double viewport = AboutPage.ViewportWidth;
            double actualWidth = AboutPage.ActualWidth;
            double available = viewport > 0 ? viewport : (actualWidth > 0 ? actualWidth : newSize);
            string targetState = available >= DualColumnThreshold ? "WideAboutState" : "NarrowAboutState";
            // VSG 掛在 SettingsPage 根 Grid 上，與 SettingsNavPage / GeneralContent / GamepadHints 並列。
            VisualStateManager.GoToState(this, targetState, false);
        }

        // ── 平台可用性 ────────────────────────────────────────────────────────

        /// <summary>
        /// 非同步查詢所有平台的安裝狀態，更新 IsAvailable 後重新指定 ItemsSource 重新整理 OneTime 繫結。
        /// 若目前選取的平台不可用，自動切換至第一個可用的平台。
        /// </summary>
        private async Task LoadPlatformAvailabilityAsync()
        {
            bool[] available = await Task.WhenAll(
                _cardItems.Select(c => ProcessLauncherService.CheckPlatformAvailableAsync(c.Platform)));

            for (int i = 0; i < _cardItems.Count; i++)
            {
                _cardItems[i].IsAvailable = available[i];
            }

            // 若目前選取的平台已停用，先調整選取的 Id
            var currentSelected = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (currentSelected is { IsAvailable: false })
            {
                var firstAvailable = _cardItems.FirstOrDefault(c => c.IsAvailable);
                if (firstAvailable != null)
                {
                    _selectedPlatformId = firstAvailable.Id;
                }
                else
                {
                    // 所有平台都不可用，清除選取 Id
                    _selectedPlatformId = "";
                }
            }

            // 重新指定 ItemsSource 讓 OneTime 繫結重新求值（CardOpacity 依最新 IsAvailable 更新）
            PlatformGridView.ItemsSource = null;
            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }
        }

        // ── 平台卡片事件 ──────────────────────────────────────────────────────

        /// <summary>
        /// 處理 GridView 選取狀態變更。
        /// 若選取的平台不可用，則還原至上一個有效選取。
        /// </summary>
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is PlatformCardItem selected)
            {
                if (!selected.IsAvailable)
                {
                    if (_currentCategoryTag == "User")
                    {
                        // 使用者索引標籤：允許選取不可用的平台（以便透過 X 編輯修正路徑），但不儲存為預設
                        return;
                    }

                    // 系統索引標籤：若有其他可用平台，還原為上一個有效選取
                    if (_cardItems.Any(c => c.IsAvailable))
                    {
                        var previous = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
                        PlatformGridView.SelectedItem = previous;
                        return;
                    }
                    // 所有系統平台都不可用：允許選取（啟動時會顯示錯誤訊息）
                }

                _selectedPlatformId = selected.Id;

                // 選取即儲存：先查系統平台，再查使用者平台
                var platform = PlatformCatalog.FindById(_selectedPlatformId)
                    ?? UserPlatformStore.FindById(_selectedPlatformId)
                    ?? PlatformCatalog.All[0];
                SettingsService.SetDefaultPlatform(platform);
                SettingsService.SaveCurrentVersion();
                UpdateSettingsDescription();
            }
        }

        /// <summary>
        /// 更新標題下方的描述文字，顯示目前預設平台名稱。
        /// </summary>
        private void UpdateSettingsDescription()
        {
            var platform = SettingsService.GetDefaultPlatform();
            var name = ProcessLauncherService.GetPlatformDisplayName(platform);
            SettingsDescription.Text = string.Format(_resourceLoader.GetString("SettingsDescription"), name);
        }

        /// <summary>
        /// GridView 大小變更時，依可用寬度計算每張卡片的尺寸，使卡片填滿整列。
        /// </summary>
        private void PlatformGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PlatformGridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                double availableWidth = e.NewSize.Width;
                // 根據可用寬度決定欄數
                // ≥1100px → 4 欄, ≥700px → 3 欄, <700px → 2 欄
                int columns = availableWidth >= 1100 ? 4 : availableWidth >= 700 ? 3 : 2;
                double itemWidth = Math.Floor(availableWidth / columns);
                double remainder = availableWidth - itemWidth * columns;
                // 非整除且餘數極小時 ItemsWrapGrid 因精度問題換行，減 1 迴避
                if (remainder > 0 && remainder < 1)
                    itemWidth -= 1;
                wrapGrid.ItemWidth = itemWidth;
                wrapGrid.ItemHeight = Math.Floor(itemWidth * 0.7); // 維持約 7:10 的高寬比
            }
        }

        // ── 設定控制項事件 ────────────────────────────────────────────────────

        /// <summary>
        /// 重設 Game Bar 並觸發 FSE。先透過 <see cref="FseService.EnsureGameBarReadyAsync"/>
        /// 確保 Game Bar 完全就緒，再以「殺死後重發」機制繞過可能卡住的 FSE 進入對話方塊。
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

            // 2. 再次殺掉以繞過 FSE 進入對話方塊（「殺死後重發」機制），稍待讓系統狀態穩定
            FseService.KillGameBar();
            await Task.Delay(500);

            // [Windows Bug] 從桌面進入 FSE 時，部分應用程式會被最大化並搶走前景焦點
            if (!FseService.IsActive())
                FseService.KillIgnoredBackgroundServices();

            if (FseService.TryActivate())
            {
                // 此應用程式會被重新啟動在 FSE 環境
                ShowWindow(Hwnd, SW_HIDE);
                App.ExitApp();
            }

            ResetGameBarProgressRing.IsActive = false;
            ResetGameBarProgressRing.Visibility = Visibility.Collapsed;
            ResetGameBarButton.IsEnabled = true;
        }

        /// <summary>
        /// PhantomKey 手把輸入開關切換時立即儲存。
        /// 開啟時若在 FSE 模式下立即啟動服務，關閉時終止服務。
        /// 同時連動 Steam In-Game Overlay 開關的啟用狀態。
        /// </summary>
        // PhantomKey 已改為 FSE 常駐；XAML 開關已註解，此 handler 保留但不再被觸發。
        private void UsePhantomKeySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            //SettingsService.SetUsePhantomKey(UsePhantomKeySwitch.IsOn);
            //UsePhantomKeySteamInGameOverlaySwitch.IsEnabled = UsePhantomKeySwitch.IsOn;
            //ApplyMouseModeEnabledState();
            //if (UsePhantomKeySwitch.IsOn && FseService.IsActive())
            //    PhantomKeyService.Start();
            //else if (!UsePhantomKeySwitch.IsOn)
            //    PhantomKeyService.Kill();
        }

        /// <summary>
        /// Steam In-Game Overlay 開關切換時立即儲存（同步寫入 INI）。
        /// </summary>
        private void UsePhantomKeySteamInGameOverlaySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetUsePhantomKeySteamInGameOverlay(UsePhantomKeySteamInGameOverlaySwitch.IsOn);
        }

        /// <summary>
        /// Mouse Mode 下拉選單（Off/Auto/ForceOn）變更時立即儲存，並更新子控制項反灰狀態。
        /// </summary>
        private void MouseModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MouseModeCombo.SelectedItem is not ComboBoxItem item) return;
            string mode = item.Tag as string ?? SettingsService.MouseModeAuto;
            SettingsService.SetMouseMode(mode);
            ApplyMouseModeEnabledState();
        }

        /// <summary>
        /// Mouse Mode 版面配置 ToggleSwitch 切換時立即儲存。Off=OmniNav、On=Classic。
        /// </summary>
        private void MouseModeLayoutSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetMouseModeLayout(
                MouseModeLayoutSwitch.IsOn ? SettingsService.LayoutClassic : SettingsService.LayoutOmniNav);
        }

        /// <summary>
        /// Cursor Speed 下拉選單選取變更時儲存百分比。
        /// </summary>
        private void CursorSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CursorSpeedCombo.SelectedIndex < 0) return;
            int pct = SettingsService.ValidCursorSpeedPercents[CursorSpeedCombo.SelectedIndex];
            SettingsService.SetCursorSpeedPercent(pct);
        }

        /// <summary>
        /// 套用 Mouse Mode 子控制項的反灰串聯：
        /// PhantomKey 主開關 + 內建廠商映射偵測 → Mouse Mode 主開關
        /// → Layout / Cursor Speed。
        /// </summary>
        private void ApplyMouseModeEnabledState(bool? builtInMappingOverride = null)
        {
            bool builtIn = builtInMappingOverride ?? SettingsService.HasBuiltInGamepadMapping();
            // PhantomKey 改為 FSE 常駐，不再依開關；保留變數以利未來復原。
            //bool phantomOn = UsePhantomKeySwitch.IsOn;
            bool phantomOn = true;
            bool mouseModeAvailable = phantomOn && !builtIn;
            string mode = (MouseModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? SettingsService.MouseModeAuto;
            bool mouseModeOn = mouseModeAvailable && mode != SettingsService.MouseModeOff;

            MouseModeCombo.IsEnabled = mouseModeAvailable;
            MouseModeBuiltInMappingNoteText.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;

            MouseModeLayoutSwitch.IsEnabled = mouseModeOn;
            CursorSpeedCombo.IsEnabled = mouseModeOn;
        }

        // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），Toggled handler 一併停用。
        //
        // /// <summary>
        // /// Game Bar 媒體櫃開關切換時立即儲存。
        // /// 開啟時 Game Bar 的「媒體櫃」按鈕將開啟 OmniConsole 設定介面；關閉時開啟預設遊戲平台。
        // /// </summary>
        // private void UseGameBarLibrarySwitch_Toggled(object sender, RoutedEventArgs e)
        // {
        //     SettingsService.SetUseGameBarLibraryForSettings(UseGameBarLibrarySwitch.IsOn);
        // }
        //
        // /// <summary>
        // /// Passthrough 開關切換時立即儲存。
        // /// 開啟時 Game Bar 的「首頁」與「媒體櫃」按鈕將直接導向預設平台，跳過 OmniConsole。
        // /// </summary>
        // private void EnablePassthroughSwitch_Toggled(object sender, RoutedEventArgs e)
        // {
        //     SettingsService.SetEnablePassthrough(EnablePassthroughSwitch.IsOn);
        // }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 使用者接受自訂平台免責聲明後，儲存同意狀態並載入自訂平台卡片。
        /// </summary>
        private void CustomConsentAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.SetCustomPlatformConsentAccepted(true);
            LoadPlatformCards();
            UpdateGamepadHints();
        }

        // ── 平台分類索引標籤切換 ──────────────────────────────────────────────

        /// <summary>
        /// 處理分類 NavigationView（系統/使用者）的選項變更。
        /// </summary>
        private void PlatformCategoryNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                SwitchCategoryTab(tag);
            }
        }

        /// <summary>
        /// 切換至指定的分類索引標籤並重新載入卡片。
        /// </summary>
        private void SwitchCategoryTab(string tag)
        {
            if (_currentCategoryTag == tag) return;
            _currentCategoryTag = tag;

            // 同步 NavigationView 選取狀態（LB/RB 肩鍵觸發時需要）
            foreach (NavigationViewItem navItem in PlatformCategoryNav.MenuItems.Cast<NavigationViewItem>())
            {
                if (navItem.Tag is string t && t == tag)
                {
                    PlatformCategoryNav.SelectedItem = navItem;
                    break;
                }
            }

            LoadPlatformCards();
            UpdateGamepadHints();
        }

        /// <summary>
        /// 根據目前分類索引標籤載入對應的平台卡片清單。
        /// 使用者索引標籤需先通過免責聲明同意檢查。
        /// </summary>
        private void LoadPlatformCards()
        {
            bool isUserTab = _currentCategoryTag == "User";
            bool isConsented = SettingsService.GetCustomPlatformConsentAccepted();

            // 使用者索引標籤未同意時：顯示免責聲明，隱藏卡片和手把提示
            VisualStateManager.GoToState(this, (isUserTab && !isConsented) ? "ConsentVisible" : "GridViewVisible", false);

            if (isUserTab)
            {
                // 使用者自訂平台
                var userDefinitions = UserPlatformStore.GetAllDefinitions();
                _cardItems = userDefinitions
                    .Select(p => new PlatformCardItem
                    {
                        Platform = p,
                        DisplayName = UserPlatformStore.FindEntryById(p.Id)?.DisplayName ?? p.Id,
                    })
                    .ToList();
            }
            else
            {
                // 系統內建平台
                _cardItems = PlatformCatalog.All
                    .Select(p => new PlatformCardItem
                    {
                        Platform = p,
                        DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                    })
                    .ToList();
            }

            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }

            // 非同步查詢可用性
            _ = LoadPlatformAvailabilityAsync();
        }

        // ── 平台匯出 / 匯入 ───────────────────────────────────────────────────

        /// <summary>
        /// 卡片右鍵選單開啟前呼叫：非使用者索引標籤時直接關閉 flyout，不顯示選單。
        /// </summary>
        private void CardContextMenu_Opening(object sender, object e)
        {
            if (_currentCategoryTag != "User")
                (sender as MenuFlyout)?.Hide();
        }

        /// <summary>
        /// 卡片右鍵選單「匯出」點選時，將平台設定序列化為 JSON 並複製到剪貼簿。
        /// </summary>
        private void CardExport_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PlatformCardItem card) return;

            var entry = UserPlatformStore.FindEntryById(card.Id);
            if (entry is null) return;

            var dp = new DataPackage();
            dp.SetText(UserPlatformShareService.Export(entry));
            Clipboard.SetContent(dp);

            ExportSuccessTeachingTip.IsOpen = true;
            _exportTipTimer.Stop();
            _exportTipTimer.Start();
        }

        /// <summary>
        /// 使用者索引標籤右側「匯入」按鈕點選時，顯示 ImportPlatformDialog。
        /// 驗證通過後寫入 UserPlatformStore 並重新載入卡片。
        /// </summary>
        private async void ImportPlatformButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                // 若提示仍開著，先強制關閉再顯示 Dialog，
                // 避免 TeachingTip 與 ContentDialog.ShowAsync() 同時存在導致崩潰。
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                var dialog = new ImportPlatformDialog(this.XamlRoot, _resourceLoader);
                StopGamepadPolling();
                var result = await dialog.ShowAsync();
                StartGamepadPolling();
                if (result != ContentDialogResult.Primary || dialog.ResultEntry is null) return;

                UserPlatformStore.Add(dialog.ResultEntry);
                LoadPlatformCards();
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 平台編輯對話方塊 ──────────────────────────────────────────────────

        /// <summary>
        /// 底部提示列「Y 新增」按鈕的滑鼠點選處理。
        /// </summary>
        private void AddPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 底部提示列「X 編輯」按鈕的滑鼠點選處理。
        /// 編輯目前 GridView 中選取的使用者平台。
        /// </summary>
        private void EditPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategoryTag != "User") return;
            if (PlatformGridView.SelectedItem is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>開啟系統 FileOpenPicker 作為舊式後備，回傳選取的檔案路徑或 null。</summary>
        private async Task<string?> ShowLegacyFilePickerAsync(FilePickerOptions options)
        {
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = options.ShowImagePreview
                    ? PickerLocationId.PicturesLibrary
                    : PickerLocationId.ComputerFolder;
                foreach (var filter in options.FileTypeFilters)
                    picker.FileTypeFilter.Add(filter);

                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 顯示新增/編輯使用者平台的 PlatformEditDialog。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        private async Task ShowPlatformEditDialogAsync(UserPlatformEntry? existingEntry)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                bool isEdit = existingEntry != null;
                var dialog = new PlatformEditDialog(
                    this.XamlRoot, _resourceLoader, existingEntry);

                ContentDialogResult result;

                // Hide/reopen 迴圈：PlatformEditDialog 的瀏覽按鈕會 Hide() 自己，
                // 由此處協調顯示 FilePickerDialog 後重新開啟 PlatformEditDialog。
                while (true)
                {
                    StopGamepadPolling();
                    result = await dialog.ShowAsync();
                    StartGamepadPolling();

                    if (!dialog.RequestFilePicker) break;

                    // 顯示自製檔案選擇器
                    var pickerDialog = new FilePickerDialog(
                        this.XamlRoot, _resourceLoader, dialog.FilePickerRequest!);
                    StopGamepadPolling();
                    var pickerResult = await pickerDialog.ShowAsync();
                    StartGamepadPolling();

                    string? selectedPath = null;
                    if (pickerResult == ContentDialogResult.Primary)
                    {
                        selectedPath = pickerDialog.SelectedFilePath;
                    }
                    else if (pickerDialog.RequestLegacyPicker)
                    {
                        // 使用者要求系統 FileOpenPicker
                        selectedPath = await ShowLegacyFilePickerAsync(dialog.FilePickerRequest!);
                    }
                    dialog.ApplyFilePickerResult(selectedPath);
                    // 迴圈回去重新開啟 PlatformEditDialog
                }

                if (result == ContentDialogResult.Primary && dialog.ResultEntry != null)
                {
                    var entry = dialog.ResultEntry;

                    // 匯入卡片背景圖（縮放至 800x560）
                    if (dialog.PendingIconPath != null)
                    {
                        if (!string.IsNullOrEmpty(entry.IconFileName))
                            UserPlatformStore.DeleteIconFile(entry.IconFileName);
                        var storageFile = await StorageFile.GetFileFromPathAsync(dialog.PendingIconPath);
                        entry.IconFileName = await UserPlatformStore.ImportIconAsync(storageFile);
                    }

                    if (isEdit)
                        UserPlatformStore.Update(entry);
                    else
                        UserPlatformStore.Add(entry);

                    LoadPlatformCards();
                }
                else if (result == ContentDialogResult.Secondary && isEdit && existingEntry != null)
                {
                    // 刪除平台：從 Store 移除後，視剩餘數量決定留在使用者索引標籤或切回系統索引標籤
                    UserPlatformStore.Delete(existingEntry.Id);

                    var remainingUser = UserPlatformStore.GetAllDefinitions();
                    if (remainingUser.Count > 0)
                    {
                        // 使用者索引標籤仍有其他平台，留在使用者索引標籤並選取第一個
                        _selectedPlatformId = remainingUser[0].Id;
                        LoadPlatformCards();
                    }
                    else
                    {
                        // 使用者索引標籤已無平台，切換至系統索引標籤
                        _selectedPlatformId = PlatformCatalog.All[0].Id;
                        _currentCategoryTag = "";
                        SwitchCategoryTab("System");
                    }
                }
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 手把輸入處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 Xbox 手把的輸入輪詢機制。
        /// 若尚未初始化 <see cref="GamepadNavigationService"/>，則會在此建立其實體，
        /// 以 <see cref="SettingsNav"/> 為 XY 焦點根容器，並傳遞各按鍵回呼函式。
        /// </summary>
        public void StartGamepadPolling()
        {
            if (_gamepadNavigationService == null)
            {
                _gamepadNavigationService = new GamepadNavigationService(
                    this.SettingsNav,
                    this.DispatcherQueue,
                    OnGamepadAButtonPressed,
                    OnGamepadBButtonPressed,
                    OnGamepadLBPressed,
                    OnGamepadRBPressed,
                    OnGamepadXButtonPressed,
                    OnGamepadYButtonPressed,
                    OnGamepadMenuButtonPressed
                );
            }
            _gamepadNavigationService.Start();
        }

        /// <summary>
        /// 停止 Xbox 手把的輸入輪詢機制。
        /// 於結束應用程式或離開設定介面時呼叫。
        /// </summary>
        public void StopGamepadPolling()
        {
            _gamepadNavigationService?.Stop();
        }

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式（設定介面）。
        /// 依焦點所在元素分派：GridViewItem 選取平台、NavigationViewItem 切換頁面、各控制項觸發對應操作。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);

            switch (focused)
            {
                // 平台卡片：確認選取（不可用卡片不響應，避免意外切換預設平台）
                case GridViewItem { Content: PlatformCardItem { IsAvailable: true } card }:
                    PlatformGridView.SelectedItem = card;
                    _selectedPlatformId = card.Id;
                    break;

                // 分類索引標籤（系統 / 使用者）：透過 SwitchCategoryTab 統一切換
                case NavigationViewItem navItem when PlatformCategoryNav.MenuItems.Contains(navItem):
                    if (navItem.Tag is string categoryTag)
                        SwitchCategoryTab(categoryTag);
                    break;

                // 設定導覽項目（一般 / 進階 / 疑難排解）：選取頁面並收合側邊欄
                case NavigationViewItem navItem:
                    SettingsNav.SelectedItem = navItem;
                    SettingsNav.IsPaneOpen = false;
                    break;

                // NavigationView 內建返回按鈕：無操作（避免誤觸觸發系統行為）
                case Button { Name: "NavigationViewBackButton" }:
                    break;

                // 漢堡選單按鈕：切換側邊欄展開 / 收合狀態
                case FrameworkElement { Name: "TogglePaneButton" }:
                    SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
                    break;

                // 重設 Game Bar 按鈕：觸發殺行程並重新啟動 FSE 的備援流程
                case Button btn when ReferenceEquals(btn, ResetGameBarButton):
                    ResetGameBarButton_Click(this, new RoutedEventArgs());
                    break;

                // 自訂平台免責聲明接受按鈕：同意後解鎖使用者平台索引標籤
                case Button btn when ReferenceEquals(btn, CustomConsentAcceptButton):
                    CustomConsentAcceptButton_Click(this, new RoutedEventArgs());
                    break;

                // 匯入按鈕（使用者索引標籤可見時）：開啟匯入對話方塊
                case Button btn when ReferenceEquals(btn, ImportPlatformButton):
                    ImportPlatformButton_Click(this, new RoutedEventArgs());
                    break;

                // PhantomKey 手把輸入開關 — 已移除（FSE 常駐），保留註解以利復原
                //case ToggleSwitch sw when ReferenceEquals(sw, UsePhantomKeySwitch):
                //    UsePhantomKeySwitch.IsOn = !sw.IsOn;
                //    break;

                // Steam In-Game Overlay 開關
                case ToggleSwitch sw when ReferenceEquals(sw, UsePhantomKeySteamInGameOverlaySwitch):
                    UsePhantomKeySteamInGameOverlaySwitch.IsOn = !sw.IsOn;
                    break;

                // Mouse Mode 下拉選單：A 鍵展開由 GamepadNavigationService 統一處理，此處無需動作

                // Mouse Mode 版面配置切換 (OmniNav / Classic)
                case ToggleSwitch sw when ReferenceEquals(sw, MouseModeLayoutSwitch):
                    if (sw.IsEnabled) MouseModeLayoutSwitch.IsOn = !sw.IsOn;
                    break;

                // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 SettingsPage.xaml 註解），手把 A 鍵切換 case 一併停用。
                //
                // // Game Bar 媒體櫃開關：On = 媒體櫃按鈕開啟 OmniConsole 設定；Off = 開啟預設平台
                // case ToggleSwitch sw when ReferenceEquals(sw, UseGameBarLibrarySwitch):
                //     UseGameBarLibrarySwitch.IsOn = !sw.IsOn;
                //     break;
                //
                // // Passthrough 開關：切換「首頁 / 媒體櫃按鈕直接導向預設平台，跳過 OmniConsole」
                // case ToggleSwitch sw when ReferenceEquals(sw, EnablePassthroughSwitch):
                //     EnablePassthroughSwitch.IsOn = !sw.IsOn;
                //     break;

                // 自動檢查更新開關
                case ToggleSwitch sw when ReferenceEquals(sw, AutoUpdateCheckSwitch):
                    AutoUpdateCheckSwitch.IsOn = !sw.IsOn;
                    break;

                // 檢查更新按鈕
                case Button btn when ReferenceEquals(btn, CheckForUpdatesButton):
                    CheckForUpdatesButton_Click(this, new RoutedEventArgs());
                    break;

                // 下載並安裝按鈕
                case Button btn when ReferenceEquals(btn, DownloadInstallButton):
                    DownloadInstallButton_Click(this, new RoutedEventArgs());
                    break;

                // 開發人員模式設定按鈕
                case HyperlinkButton btn when ReferenceEquals(btn, DeveloperModeOpenSettingsButton):
                    DeveloperModeOpenSettings_Click(this, new RoutedEventArgs());
                    break;

                // 關於頁「複製到剪貼簿」按鈕
                case Button btn when ReferenceEquals(btn, CopyAboutButton):
                    CopyAboutButton_Click(this, new RoutedEventArgs());
                    break;

                // 關於頁「重新整理」按鈕
                case Button btn when ReferenceEquals(btn, RefreshAboutButton):
                    RefreshAboutButton_Click(this, new RoutedEventArgs());
                    break;
            }
        }

        /// <summary>
        /// 處理手把 'B' 鍵被按下的回呼函式。
        /// 導覽選單展開時先收合，否則觸發全域退出。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            if (SettingsNav.IsPaneOpen)
            {
                SettingsNav.IsPaneOpen = false;
                return;
            }

            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 手把 LB 肩鍵：切換到上一個分類索引標籤。
        /// </summary>
        private void OnGamepadLBPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "User")
                SwitchCategoryTab("System");
        }

        /// <summary>
        /// 手把 RB 肩鍵：切換到下一個分類索引標籤。
        /// </summary>
        private void OnGamepadRBPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "System")
                SwitchCategoryTab("User");
        }

        /// <summary>
        /// 手把 Y 鍵：使用者索引標籤時觸發新增平台。
        /// </summary>
        private void OnGamepadYButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "User" && SettingsService.GetCustomPlatformConsentAccepted())
                _ = ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 手把 X 鍵：使用者索引標籤時觸發編輯目前聚焦的平台。
        /// </summary>
        private void OnGamepadXButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag != "User") return;
            if (!SettingsService.GetCustomPlatformConsentAccepted()) return;

            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem gridViewItem &&
                gridViewItem.Content is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>
        /// 底部提示列「Menu 啟動」按鈕的滑鼠點選處理。
        /// </summary>
        private void LaunchPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            OnGamepadMenuButtonPressed();
        }

        /// <summary>
        /// 手把 Menu（☰）鍵：直接啟動目前聚焦（或已選取）的平台，跳過手動 FSE 切換流程。
        /// 僅在 FSE 模式中有效；自訂平台索引標籤需已接受同意聲明。
        /// 若焦點在可用的平台卡片上，先將其設為選取（同 A 鍵），再通知 MainWindow 啟動。
        /// </summary>
        private void OnGamepadMenuButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (!FseService.IsActive()) return;
            if (_currentCategoryTag == "User" && !SettingsService.GetCustomPlatformConsentAccepted()) return;

            // 若焦點在可用卡片上，先確認選取（更新預設平台）
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem { Content: PlatformCardItem { IsAvailable: true } card })
            {
                PlatformGridView.SelectedItem = card;
                _selectedPlatformId = card.Id;
            }

            if (string.IsNullOrEmpty(_selectedPlatformId)) return;

            LaunchPlatformDirectlyRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 更新檢查 ───────────────────────────────────────────────────────────

        /// <summary>自動更新檢查開關切換。</summary>
        private void AutoUpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetAutoUpdateCheckEnabled(AutoUpdateCheckSwitch.IsOn);
            ShowSettingsUpdateInfoBar();
        }

        /// <summary>手動檢查更新按鈕，強制抓取 GitHub API 並更新快取。</summary>
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate) return;
            _isCheckingUpdate = true;

            CheckDeveloperMode(); // 使用者可能從設定頁回來後狀態已變更
            UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateCheck_Checking");
            UpdateCheckStatusText.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.IsActive = true;

            var delayTask = Task.Delay(500);
            var (kind, version) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();
            await delayTask;

            CheckUpdateProgressRing.IsActive = false;
            CheckUpdateProgressRing.Visibility = Visibility.Collapsed;
            _isCheckingUpdate = false;

            switch (kind)
            {
                case UpdateCheckService.UpdateKind.MissingPhantomLink:
                    UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    ShowSettingsUpdateInfoBar();
                    break;

                case UpdateCheckService.UpdateKind.MainAppUpdate:
                    UpdateCheckStatusText.Text = string.Format(
                        _resourceLoader.GetString("UpdateCheck_NewVersion_Subtitle"), version);
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    ShowSettingsUpdateInfoBar();
                    break;

                case UpdateCheckService.UpdateKind.None:
                    UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateCheck_UpToDate_Subtitle");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Collapsed;
                    SettingsUpdateInfoBar.IsOpen = false;
                    break;
            }
        }

        /// <summary>下載並安裝更新按鈕（PhantomLink 先裝，OmniConsole 後裝）。</summary>
        private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_downloadCts != null) return; // 下載中，防止重複觸發

            // 點選時再次確認開發人員模式，防止使用者中途關閉
            CheckDeveloperMode();
            if (!UpdateCheckService.IsDeveloperModeEnabled()) return;

            // 重新檢查最新版本，確保下載的是最新的而非過期快取
            var (kind, _) = await UpdateCheckService.CheckForUpdateAsync();

            var mainUrl = SettingsService.GetCachedDownloadUrl();
            var phantomLinkUrl = SettingsService.GetCachedPhantomLinkUrl();
            var targetVersion = SettingsService.GetCachedNewVersion();

            if (string.IsNullOrEmpty(mainUrl) && string.IsNullOrEmpty(phantomLinkUrl))
            {
                // 無快取下載連結時回退開瀏覽器
                await Windows.System.Launcher.LaunchUriAsync(
                    new Uri(UpdateCheckService.ReleaseNotesUrl));
                return;
            }

            bool mainSkippable = kind == UpdateCheckService.UpdateKind.MissingPhantomLink
                && targetVersion == SettingsService.GetAppVersion();

            await RunInstallBundleWithDialogAsync(phantomLinkUrl, mainUrl, targetVersion,
                mainSkippable, resumeFromPhase2: false);
        }

        /// <summary>
        /// 將 InstallBundleAsync 包進 UpdateProgressDialog，由對話方塊以模態方式擋住手把 B 鍵與 Esc，
        /// 並在 MainWindow 端攔截視窗關閉。失敗後解除鎖定並顯示失敗訊息於原 InfoBar。
        /// </summary>
        internal async Task RunInstallBundleWithDialogAsync(
            string phantomLinkUrl, string mainUrl, string targetVersion,
            bool mainSkippable, bool resumeFromPhase2)
        {
            var dialog = new UpdateProgressDialog(this.XamlRoot, _resourceLoader);

            try
            {
                _downloadCts = new CancellationTokenSource();
                var progress = new Progress<double>(pct => dialog.ReportProgress(pct));
                var status = new Progress<string>(key =>
                {
                    dialog.ReportStatus(key);
                    // Phase 2 末端兩條路徑（實際安裝 / mainSkippable 重啟）會由 OS 送 graceful close
                    // 請求給本視窗，此時鬆開 AppWindow.Closing 鎖讓請求通過
                    if (key == "Phase2Install" || key == "Phase2RequestingRestart")
                        MainWindow.IsUpdateInstallInProgress = false;
                });

                // 終止 PhantomKey，避免 MSIX 更新時因 .exe 佔用而拖慢進度
                PhantomKeyService.Kill();

                // MSIX 更新前取消 FSE 狀態通知
                FseService.StopListening();

                // 註冊自動重啟，ForceApplicationShutdown 結束 OmniConsole 後 Windows 會自動重新啟動 OmniConsole
                RegisterApplicationRestart("", 0);

                // 設定安裝鎖定旗標供 MainWindow 讀取
                MainWindow.IsUpdateInstallInProgress = true;

                // 對話方塊期間停掉設定頁的手把輪詢
                StopGamepadPolling();

                // 不 await ShowAsync，與 InstallBundleAsync 並行執行
                var showTask = dialog.ShowAsync().AsTask();

                await UpdateCheckService.InstallBundleAsync(
                    phantomLinkUrl, mainUrl, targetVersion,
                    mainSkippable, resumeFromPhase2,
                    progress, status, _downloadCts.Token);

                // ForceApplicationShutdown / RequestRestartAsync 路徑會結束本行程，此後程式碼為 fallback
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DebugLogger.Log($"[SettingsPage] Download/install failed: {ex.Message}");
                dialog.RequestClose();
                MainWindow.IsUpdateInstallInProgress = false;
                StartGamepadPolling();
                UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateDownload_Failed");
                UpdateCheckStatusText.Visibility = Visibility.Visible;
            }
            finally
            {
                _downloadCts?.Dispose();
                _downloadCts = null;
            }
        }

        /// <summary>
        /// 自動檢查更新（靜默，有動作時顯示 InfoBar）。
        /// </summary>
        private async Task AutoCheckForUpdatesAsync()
        {
            var (kind, _) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();

            if (kind != UpdateCheckService.UpdateKind.None)
            {
                ShowSettingsUpdateInfoBar();
                ShowCachedUpdateStatus();
            }
        }

        /// <summary>
        /// 依快取的 UpdateKind 顯示或隱藏設定頁 InfoBar。
        /// </summary>
        private void ShowSettingsUpdateInfoBar()
        {
            if (!SettingsService.GetAutoUpdateCheckEnabled())
            {
                SettingsUpdateInfoBar.IsOpen = false;
                return;
            }

            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                SettingsUpdateInfoBar.Title = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                SettingsUpdateInfoBar.Message = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Message");
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                SettingsUpdateInfoBar.Title = "";
                SettingsUpdateInfoBar.Message = string.Format(
                    _resourceLoader.GetString("UpdateAvailable_InfoBar_Settings"), cached);
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else
            {
                SettingsUpdateInfoBar.IsOpen = false;
            }
        }

        /// <summary>
        /// 依快取的 UpdateKind，在版本號下方顯示狀態文字與「下載並安裝」按鈕。
        /// </summary>
        private void ShowCachedUpdateStatus()
        {
            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = _resourceLoader.GetString("UpdateInfoBar_MissingPhantomLink_Title");
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = string.Format(
                    _resourceLoader.GetString("UpdateCheck_NewVersion_Subtitle"), cached);
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateCheckStatusText.Visibility = Visibility.Collapsed;
                DownloadInstallButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// 檢查開發人員模式是否啟用，未啟用時顯示黃色警告並停用下載按鈕。
        /// </summary>
        private void CheckDeveloperMode()
        {
            bool enabled = UpdateCheckService.IsDeveloperModeEnabled();
            DeveloperModeWarningPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            if (!enabled)
                DeveloperModeWarningText.Text = _resourceLoader.GetString("DeveloperMode_Warning");
            DownloadInstallButton.IsEnabled = enabled;
        }

        /// <summary>開啟 Windows 開發人員模式設定頁面。</summary>
        private async void DeveloperModeOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
        }
    }
}
