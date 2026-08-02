using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace OmniConsole.Pages.Settings
{
    /// <summary>
    /// 設定的關於分頁內容：OmniConsole Suite 版本、PhantomKey 健康狀況、XFSET 工具鏈狀態、環境快照，並提供複製到剪貼簿。
    /// 由 SettingsHostView 透過 Frame.Navigate 載入（切走即銷毀）；本頁不實作手把 scope。
    /// 雙欄/單欄版型切換的 AboutLayoutStates VSM 整組在本頁內（Setter Target 皆為自家元素）。
    /// </summary>
    public sealed partial class AboutView : Page
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 關於頁重新整理重複觸發防護
        private bool _isRefreshingAbout;

        // 關於頁複製到剪貼簿重複觸發防護
        private bool _isCopyingAbout;

        // 複製確認 TeachingTip 的自動關閉計時器（2 秒後關閉）
        private readonly DispatcherTimer _aboutCopyConfirmTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>記住上次套用的關於頁焦點導航是否為窄版，避免每次 SizeChanged 重複賦值（null=尚未套用）。</summary>
        private bool? _aboutNarrowFocus;

        /// <summary>建立關於分頁：套用品牌標題與發行資訊（commit/時間戳），掛複製提示計時器。</summary>
        public AboutView()
        {
            InitializeComponent();

            _aboutCopyConfirmTimer.Tick += (_, _) =>
            {
                _aboutCopyConfirmTimer.Stop();
                AboutCopyConfirmTeachingTip.IsOpen = false;
            };

            ApplyBrandedTitles();
            ApplyBuildIdentity();
        }

        /// <summary>Frame 導覽進來時擷取環境快照並更新各欄位。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            LoadAboutPageContent();
        }

        /// <summary>
        /// 設定含品牌名的關於頁標題類字串：由 code-behind 設定，品牌名由程式注入（佔位符換成品牌、確保一致正確）。
        /// </summary>
        private void ApplyBrandedTitles()
        {
            AboutTitleText.Text = _resourceLoader.Loc("AboutTitle");
            AboutSuiteSectionText.Text = _resourceLoader.Loc("AboutSection_Suite");
            LabelOmniConsoleText.Text = _resourceLoader.Loc("Label_OmniConsole");
        }

        /// <summary>
        /// 擷取環境快照並更新關於頁各文字區塊。
        /// 在背景執行緒取資料、再回 UI 執行緒設值。
        /// </summary>
        private async void LoadAboutPageContent(bool enforceMinDelay = false)
        {
            if (_isRefreshingAbout) return;
            _isRefreshingAbout = true;

            RefreshAboutProgressRing.Visibility = Visibility.Visible;
            RefreshAboutProgressRing.IsActive = true;

            var delayTask = enforceMinDelay ? Task.Delay(500) : null;
            var snapshot = await Task.Run(() => AboutInfoService.GetEnvironmentSnapshot());
            if (delayTask is not null) await delayTask;

            ApplyAboutSnapshot(snapshot);
            RefreshAboutProgressRing.IsActive = false;
            RefreshAboutProgressRing.Visibility = Visibility.Collapsed;
            _isRefreshingAbout = false;
        }

        /// <summary>
        /// 將 <see cref="AboutInfoService.EnvironmentSnapshot"/> 套用到「關於」分頁的各 UI 欄位。
        /// </summary>
        private void ApplyAboutSnapshot(AboutInfoService.EnvironmentSnapshot s)
        {
            AboutOmniConsoleVersion.Text = LocalizeForUI(s.Versions.OmniConsole);
            AboutPhantomBridgeVersion.Text = LocalizeForUI(s.Versions.PhantomBridge);
            AboutPhantomKeyVersion.Text = FormatPhantomKeyVersionForUI(s.Versions);
            AboutPhantomPawVersion.Text = FormatPhantomPawVersionForUI(s.Versions);
            AboutPhantomLinkVersion.Text = LocalizeForUI(s.Versions.PhantomLink);
            AboutPhantomSigilVersion.Text = FormatPackagedVsDeployed(s.Versions.PhantomSigil, s.Versions.PhantomSigilInstalled);

            // PhantomKey 健康狀況
            ApplyPhantomKeyHealth(s.PhantomKey);

            // PhantomSigil（提權工作層）健康狀況
            AboutElevatedServiceText.Text = _resourceLoader.Loc(
                s.PhantomSigil.ServiceRunning ? "ElevatedServiceHealth_Running" : "ElevatedServiceHealth_NotRunning");

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
                ? $"{s.MaxTouchPoints} ({_resourceLoader.Loc("MaxTouchPoints_NoTouch")})"
                : s.MaxTouchPoints.ToString(CultureInfo.InvariantCulture);
            AboutLocaleText.Text = LocalizeForUI(s.Locale);
            AboutCountryRegionText.Text = LocalizeForUI(s.CountryRegion);
            AboutDeviceRegionText.Text = LocalizeForUI(s.DeviceRegion);
            AboutCapturedAtText.Text = s.CapturedAt.ToString(
                "yyyy-MM-dd HH:mm:ss zzz",
                CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 把 BuildIdentity 的 commit hash 與 build 時間套用到關於頁發行資訊卡片兩列。
        /// 任何欄位取不到值時，整列 Visibility=Collapsed。憑證指紋與 source URL 由詳細資訊按鈕觸發的對話方塊顯示。
        /// </summary>
        private void ApplyBuildIdentity()
        {
            var commit = BuildIdentity.CommitHash;
            if (string.IsNullOrEmpty(commit))
            {
                AboutReleaseInfoCommitRow.Visibility = Visibility.Collapsed;
            }
            else
            {
                AboutReleaseInfoCommitLink.Content = commit;
                // Commit 超連結永遠指向官方 repo 的 commit 頁，讓使用者比對 hash 是否在官方 repo 中
                AboutReleaseInfoCommitLink.NavigateUri = new Uri($"https://github.com/8bit2qubit/OmniConsole/commit/{commit}");
            }

            var timestamp = BuildIdentity.Timestamp;
            if (string.IsNullOrEmpty(timestamp))
            {
                AboutReleaseInfoBuiltRow.Visibility = Visibility.Collapsed;
            }
            else
            {
                AboutReleaseInfoBuiltText.Text = timestamp;
            }
        }

        /// <summary>詳細資訊按鈕按下：開啟憑證詳細對話方塊顯示指紋與來源網址，關閉後焦點還原至此按鈕。</summary>
        private async void CertDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CertificateDetailsDialog(XamlRoot, BuildIdentity.CertificateThumbprint);
            await dialog.ShowAsync();
            DispatcherQueue.TryEnqueue(() =>
                CertDetailsButton.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>
        /// 把資料層的固定英文回退字串（"(unknown)" / "(not installed)"）替換為在地化字串供 UI 顯示。
        /// 資料層保持英文常數有助於 Markdown 輸出的可讀性（貼到 GitHub Issue 不會帶非 ASCII 字串）。
        /// </summary>
        private string LocalizeForUI(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            if (raw == "(unknown)") return _resourceLoader.Loc("Common_Unknown");
            if (raw == "(not installed)") return _resourceLoader.Loc("Common_NotInstalled");
            return raw;
        }

        /// <summary>
        /// 把 PhantomKey 的 exe/dll 兩個版本欄位摺疊為設定頁顯示字串。
        /// 摺疊策略與 <see cref="FormatPhantomPawVersionForUI"/> 同構（由乾淨到詳細）：
        /// (1) exe/dll + packaged/deployed 全四個皆同 → 單一版本 "2.7.0.0"
        /// (2) exe/dll 同步、但 packaged ≠ deployed → "2.7.0.0 → 2.6.0.0"
        /// (3) exe 與 dll 真的不同 → "exe: ..., dll: ..."，每邊各自再套上面 (1)/(2) 規則
        /// </summary>
        private string FormatPhantomKeyVersionForUI(AboutInfoService.ComponentVersions v)
        {
            string pkgExe = v.PhantomKey, depExe = v.PhantomKeyDeployed;
            string pkgDll = v.PhantomKeyDll, depDll = v.PhantomKeyDllDeployed;

            bool componentsAligned = pkgExe == pkgDll && depExe == depDll;
            if (componentsAligned)
                return FormatPackagedVsDeployed(pkgExe, depExe);

            return $"exe: {FormatPackagedVsDeployed(pkgExe, depExe)}, dll: {FormatPackagedVsDeployed(pkgDll, depDll)}";
        }

        /// <summary>
        /// 把 PhantomPaw 四個版本欄位（32/64 × packaged/deployed）摺疊為設定頁顯示字串。
        /// 摺疊策略（由乾淨到詳細）：
        /// (1) 32/64 + packaged/deployed 全四個皆同 → 單一版本 "2.7.0.0"
        /// (2) 32/64 同步、但 packaged ≠ deployed → "2.7.0.0 → 2.6.0.0"
        /// (3) 32 與 64 真的不同 → "32: ..., 64: ..."，每邊各自再套上面 (1)/(2) 規則
        /// </summary>
        private string FormatPhantomPawVersionForUI(AboutInfoService.ComponentVersions v)
        {
            string pkg64 = v.PhantomPaw, dep64 = v.PhantomPawDeployed;
            string pkg32 = v.PhantomPaw32, dep32 = v.PhantomPaw32Deployed;

            bool bitnessAligned = pkg64 == pkg32 && dep64 == dep32;
            if (bitnessAligned)
                return FormatPackagedVsDeployed(pkg64, dep64);

            return $"32: {FormatPackagedVsDeployed(pkg32, dep32)}, 64: {FormatPackagedVsDeployed(pkg64, dep64)}";
        }

        /// <summary>單一 bitness 的「packaged vs deployed」摺疊：相同顯示一次、不同顯示 "A → B"。</summary>
        private string FormatPackagedVsDeployed(string packaged, string deployed)
        {
            return packaged == deployed
                ? LocalizeForUI(packaged)
                : $"{LocalizeForUI(packaged)} → {LocalizeForUI(deployed)}";
        }

        /// <summary>
        /// 把 XFSET 主程式安裝狀態格式化為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatXfsetToolForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.ToolInstalled) return _resourceLoader.Loc("XfsetStatus_NotInstalled");
            return $"{_resourceLoader.Loc("XfsetStatus_Installed")} ({x.ToolVersion})";
        }

        /// <summary>
        /// 把 PhysPanelCS 安裝狀態與 touchservice 執行狀態組合為設定頁顯示用的在地化字串。
        /// </summary>
        private string FormatPhysPanelForUI(AboutInfoService.XfsetInfo x)
        {
            if (!x.PhysPanelInstalled) return _resourceLoader.Loc("XfsetStatus_NotInstalled");

            string touchKey = x.TouchService switch
            {
                AboutInfoService.TouchServiceState.Running => "XfsetStatus_TouchServiceRunning",
                AboutInfoService.TouchServiceState.NotConfigured => "XfsetStatus_TouchServiceNotRunning",
                _ => "XfsetStatus_TouchServiceUnknown",
            };

            return $"{_resourceLoader.Loc("XfsetStatus_Installed")} ({x.PhysPanelVersion}), {_resourceLoader.Loc(touchKey)}";
        }

        /// <summary>
        /// 把位元組數格式化為設定頁顯示用的可讀字串（≥1 GiB 用 GB，否則用 MB）。
        /// </summary>
        private string FormatBytesForUI(ulong bytes)
        {
            if (bytes == 0) return _resourceLoader.Loc("Common_Unknown");
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
            if (mhz <= 0) return _resourceLoader.Loc("Common_Unknown");
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
            if (h.Gpus.Count == 0) return _resourceLoader.Loc("Common_Unknown");
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
                AboutPhantomKeyProcessText.Text = _resourceLoader.Loc("PhantomKeyHealth_NotRunning");
                AboutPhantomKeyUptimeText.Text = "—";
                AboutPhantomKeyIntegrityText.Text = "—";
                AboutPhantomKeyResponsivenessText.Text = "—";
                return;
            }

            AboutPhantomKeyProcessText.Text = _resourceLoader.Loc("PhantomKeyHealth_Running");

            AboutPhantomKeyUptimeText.Text = AboutInfoService.FormatUptime(h.Uptime, "—");
            AboutPhantomKeyIntegrityText.Text = h.IntegrityLevel == AboutInfoService.IntegrityLevel.Unknown
                ? _resourceLoader.Loc("Common_Unknown")
                : h.IntegrityLevel.ToString();
            AboutPhantomKeyResponsivenessText.Text = FormatResponsivenessForUI(h);
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
                        _resourceLoader.Loc("PhantomKeyResp_Responsive"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Busy
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.Loc("PhantomKeyResp_Busy"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Stuck
                    => string.Format(CultureInfo.InvariantCulture,
                        _resourceLoader.Loc("PhantomKeyResp_Stuck"), h.PingLagMs),
                AboutInfoService.PhantomKeyResponsiveness.Hung
                    => _resourceLoader.Loc("PhantomKeyResp_Hung"),
                AboutInfoService.PhantomKeyResponsiveness.NoPingWindow
                    => _resourceLoader.Loc("PhantomKeyResp_NoPingWindow"),
                AboutInfoService.PhantomKeyResponsiveness.NotRunning
                    => _resourceLoader.Loc("PhantomKeyHealth_NotRunning"),
                _ => "—",
            };
        }

        /// <summary>
        /// 複製關於頁的環境快照到剪貼簿，供使用者貼到 GitHub Issue 協助回報問題。
        /// </summary>
        private async void CopyAboutButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCopyingAbout) return;
            _isCopyingAbout = true;

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

                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutView] CopyAboutButton_Click failed: {ex.Message}");
            }
            finally
            {
                _isCopyingAbout = false;
            }
        }

        /// <summary>
        /// 重新整理關於頁所有欄位。
        /// 走 LoadAboutPageContent 路徑。
        /// </summary>
        private void RefreshAboutButton_Click(object sender, RoutedEventArgs e)
        {
            LoadAboutPageContent(enforceMinDelay: true);
        }

        /// <summary>
        /// 依關於頁實際可用寬度切換雙欄/單欄版型：取得版型決策後切 VSM 狀態、設標題區寬、套焦點導航。
        /// </summary>
        private void AboutScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var layout = FocusNavHelper.ResolveTwoColumnLayout(AboutScrollViewer, e.NewSize.Width);
            // AboutLayoutStates VSG 掛在本頁內容根 Grid（AboutViewRoot）上；
            // GoToState 第一參數需 Control，傳本頁（this），框架會在其內容根找對應 VSG。
            VisualStateManager.GoToState(this, layout.Narrow ? "NarrowAboutState" : "WideAboutState", false);
            // 把標題區 MaxWidth 設為實際可用寬減去左右邊距（左右各 24）。
            const double TitleSideMargin = 48;
            if (layout.AvailableWidth > TitleSideMargin) AboutTitleGroup.MaxWidth = layout.AvailableWidth - TitleSideMargin;
            ApplyAboutFocusNavigation(layout.Narrow);
        }

        /// <summary>關於頁手把焦點導航寬窄分流：寬螢幕（雙欄）走框架自動 XY、窄螢幕（單欄）設手動 XYFocus 指向接垂直流。</summary>
        private void ApplyAboutFocusNavigation(bool narrow)
        {
            if (_aboutNarrowFocus == narrow) return; // 狀態未變、免重複賦值
            _aboutNarrowFocus = narrow;
            FocusNavHelper.ApplyTwoColumnFocusNav(
                narrow, AboutReleaseInfoCommitLink, CertDetailsButton, CopyAboutButton, RefreshAboutButton,
                AboutHardwareCard, AboutSuiteCard, AboutSystemCard, AboutPhantomKeyCard);
        }
    }
}
