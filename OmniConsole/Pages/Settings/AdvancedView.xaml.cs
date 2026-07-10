using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings
{
    /// <summary>
    /// 設定的進階分頁內容：更新檢查/下載安裝、Steam Overlay、手把滑鼠模式、背景材質、顯示語言、社群語言管理、導覽音效。
    /// 由 SettingsHostView 透過 Frame.Navigate 載入（切走即銷毀）；本頁不實作手把 scope。
    /// 跨頁互動透過事件回報殼層：重新整理頂部更新 InfoBar、請殼層執行安裝流程。
    /// 入頁重新整理（Initialize）由殼層在導覽後、事件訂妥才呼叫（OnNavigatedTo 早於殼層訂閱、不可在此 invoke 事件）。
    /// </summary>
    public sealed partial class AdvancedView : Page
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 語言下拉選單初始化期間抑制 SelectionChanged（還原選取不應觸發儲存/重啟提示）
        private bool _suppressLanguageChange;

        // 防止檢查更新重複觸發
        private bool _isCheckingUpdate;

        // ── 殼層事件契約 ──────────────────────────────────────────────────────

        /// <summary>請殼層依快取的 UpdateKind 重新整理頂部 SettingsUpdateInfoBar（跨頁固定區塊留殼層）。</summary>
        internal event EventHandler? UpdateInfoBarRefreshRequested;

        /// <summary>請殼層執行安裝流程（殼層持有與 MainWindow 安裝續做機制的契約 RunInstallBundleWithDialogAsync）。</summary>
        internal event EventHandler<InstallRequestEventArgs>? InstallRequested;

        /// <summary>背景材質變更：請殼層轉知 MainWindow 即時套用新材質並切換分層 brush。攜帶材質字串。</summary>
        internal event EventHandler<string>? BackgroundMaterialChanged;

        /// <summary>填入背景材質下拉選單並還原選取期間，抑制 SelectionChanged 觸發重套材質。</summary>
        private bool _suppressBackgroundMaterialChange;

        /// <summary>建立進階分頁。</summary>
        public AdvancedView()
        {
            InitializeComponent();
        }

        // ── 初始化（由 SettingsHostView 在組裝/進頁時呼叫）────────────────────────

        /// <summary>填入各設定控制項的目前值（取代原 ShowSettings 裡的 Advanced 初始化區段）。</summary>
        internal void Initialize()
        {
            // 還原 Steam In-Game Overlay 開關狀態（PhantomKey 恆為啟用，此開關恆可用）
            UsePhantomKeySteamInGameOverlaySwitch.IsOn = SettingsService.GetUsePhantomKeySteamInGameOverlay();
            UsePhantomKeySteamInGameOverlaySwitch.IsEnabled = true;

            // 填充版面配置下拉選單（index 0=OmniNav、1=Classic）
            MouseModeLayoutCombo.Items.Clear();
            MouseModeLayoutCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("ControllerLayoutPresetItem_OmniNav"), Tag = SettingsService.LayoutOmniNav });
            MouseModeLayoutCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("ControllerLayoutPresetItem_Classic"), Tag = SettingsService.LayoutClassic });

            // 還原 Mouse Mode（Off/On）/ 版面配置 / 游標速度，並依內建廠商映射偵測強制停用
            bool builtInMapping = SettingsService.HasBuiltInGamepadMapping();
            string currentMode = builtInMapping ? SettingsService.MouseModeOff : SettingsService.GetMouseMode();
            MouseModeSwitch.IsOn = currentMode != SettingsService.MouseModeOff;
            MouseModeLayoutCombo.SelectedIndex = SettingsService.GetMouseModeLayout() == SettingsService.LayoutClassic ? 1 : 0;

            // 填充游標速度下拉選單並還原選取
            CursorSpeedCombo.Items.Clear();
            foreach (var p in SettingsService.ValidCursorSpeedPercents)
                CursorSpeedCombo.Items.Add($"{p}%");
            int pct = SettingsService.GetCursorSpeedPercent();
            CursorSpeedCombo.SelectedIndex = Array.IndexOf(SettingsService.ValidCursorSpeedPercents, pct);

            // 填充背景材質下拉選單並還原選取（還原期間抑制觸發，避免無謂重套材質）。
            _suppressBackgroundMaterialChange = true;
            BackgroundMaterialCombo.Items.Clear();
            BackgroundMaterialCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("BackgroundMaterialItem_PhantomClassic"), Tag = SettingsService.BackgroundMaterialPhantomClassic });
            BackgroundMaterialCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("BackgroundMaterialItem_PhantomGlass"), Tag = SettingsService.BackgroundMaterialPhantomGlass });
            BackgroundMaterialCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("BackgroundMaterialItem_PhantomGlassDeep"), Tag = SettingsService.BackgroundMaterialPhantomGlassDeep });
            BackgroundMaterialCombo.SelectedIndex = SettingsService.GetBackgroundMaterial() switch
            {
                SettingsService.BackgroundMaterialPhantomClassic => 0,
                SettingsService.BackgroundMaterialPhantomGlassDeep => 2,
                _ => 1,
            };
            _suppressBackgroundMaterialChange = false;

            ApplyMouseModeEnabledState(builtInMapping);

            // 填充顯示語言下拉選單（官方 + 社群動態補）並還原選取
            PopulateLanguageCombo();

            // 還原導覽音效開關狀態
            NavigationSoundsSwitch.IsOn = SettingsService.GetEnableNavigationSounds();

            // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 AdvancedView.xaml 註解），強制走 SettingsService 預設值。
            //
            // // 還原 Game Bar 媒體櫃的開關狀態
            // UseGameBarLibrarySwitch.IsOn = SettingsService.GetUseGameBarLibraryForSettings();
            //
            // // 還原 Passthrough 開關狀態
            // EnablePassthroughSwitch.IsOn = SettingsService.GetEnablePassthrough();

            // 還原自動檢查更新開關狀態，並顯示進階區版本號
            AutoUpdateCheckSwitch.IsOn = SettingsService.GetAutoUpdateCheckEnabled();
            AdvancedVersionText.Text = SettingsService.GetAppVersion();

            // 讀取快取的更新資訊（InfoBar 由殼層重新整理，狀態文字與開發者模式在本控制項內）
            UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
            ShowCachedUpdateStatus();
            CheckDeveloperMode(); // 未啟用開發人員模式時顯示警告並停用下載按鈕
        }

        /// <summary>若應自動檢查更新（跨日 + 開關啟用）則非同步檢查；由殼層在進頁時呼叫。</summary>
        internal Task MaybeAutoCheckForUpdatesAsync()
        {
            if (UpdateCheckService.ShouldAutoCheck())
                return AutoCheckForUpdatesAsync();
            return Task.CompletedTask;
        }

        // ── 設定控制項事件 ────────────────────────────────────────────────────

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

        /// <summary>Steam In-Game Overlay 開關切換時立即儲存（同步寫入 INI）。</summary>
        private void UsePhantomKeySteamInGameOverlaySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetUsePhantomKeySteamInGameOverlay(UsePhantomKeySteamInGameOverlaySwitch.IsOn);
        }

        /// <summary>Mouse Mode 開關切換時立即儲存，並更新子控制項反灰狀態。On=啟用、Off=停用。</summary>
        private void MouseModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetMouseMode(
                MouseModeSwitch.IsOn ? SettingsService.MouseModeOn : SettingsService.MouseModeOff);
            ApplyMouseModeEnabledState();
        }

        /// <summary>背景材質下拉選單變更時立即儲存並請殼層即時套用（無需重啟）；還原選取期間由旗標抑制。</summary>
        private void BackgroundMaterialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressBackgroundMaterialChange) return;
            if (BackgroundMaterialCombo.SelectedItem is not ComboBoxItem item) return;
            string material = item.Tag as string ?? SettingsService.BackgroundMaterialPhantomGlass;
            SettingsService.SetBackgroundMaterial(material);
            BackgroundMaterialChanged?.Invoke(this, material);
        }

        // ─── 顯示語言下拉選單 ──────────────────────────────────────────────────────

        /// <summary>官方語言（編譯進 PRI、切換需重啟）。社群語言另由偵測到的外掛語言動態補。</summary>
        private static readonly string[] OfficialLanguages = { "en-US", "zh-TW", "zh-CN" };

        /// <summary>
        /// 填充顯示語言下拉選單：官方語言 + 社群語言（外掛 .resw 偵測到的）動態補加「(社群)」標記。
        /// 顯示文字＝語言原名（CultureInfo.NativeName）+ 代碼；Tag=BCP47 碼。空偏好＝跟隨系統＝選官方第一項。
        /// 還原選取期間以旗標抑制 SelectionChanged，避免誤觸發儲存/重啟提示。
        /// </summary>
        private void PopulateLanguageCombo()
        {
            _suppressLanguageChange = true;
            try
            {
                LanguageCombo.Items.Clear();

                // 社群語言＝外掛偵測到、且不在官方清單內的（避免官方語言又被當社群重列）。
                var community = PhantomLocalizer.Instance.AvailableLanguages
                    .Where(l => !OfficialLanguages.Contains(l, StringComparer.OrdinalIgnoreCase))
                    .ToArray();

                string communityTag = _resourceLoader.Loc("LanguageItem_CommunitySuffix");             // 「(社群)」
                string communityWithTranslator = _resourceLoader.Loc("LanguageItem_CommunityWithTranslator"); // 「(社群 - {0})」
                string translatorSeparator = _resourceLoader.Loc("ManageLanguages_TranslatorSeparator");      // 「、」

                // 置頂「跟隨系統」項（Tag=空字串）：選它＝寫空偏好、不覆寫 PrimaryLanguageOverride、隨系統語言。
                // 剛裝好（無偏好）即為此狀態，預設選中此項。
                LanguageCombo.Items.Add(new ComboBoxItem
                {
                    Content = _resourceLoader.Loc("LanguageItem_SystemDefault"),
                    Tag = string.Empty,
                });

                foreach (var bcp47 in OfficialLanguages)
                    LanguageCombo.Items.Add(new ComboBoxItem { Content = DescribeLanguage(bcp47), Tag = bcp47 });

                foreach (var bcp47 in community)
                {
                    // 譯者名取自下載時寫入的本機資料，多人以分隔符串接顯示為「(社群 - 譯者A…)」；
                    // 無譯者（手動側載、無 metadata）退回「(社群)」。多人完整串接，超長時由固定 Width 的下拉選單自動截斷（…）。
                    var translators = TranslationProfileStore.GetInstalledTranslators(bcp47);
                    string tag = translators.Count > 0
                        ? SafeFormat(communityWithTranslator, string.Join(translatorSeparator, translators))
                        : communityTag;
                    LanguageCombo.Items.Add(new ComboBoxItem
                    {
                        Content = $"{DescribeLanguage(bcp47)} {tag}",
                        Tag = bcp47,
                    });
                }

                // 還原選取：偏好為空（跟系統）→ 置頂「跟隨系統」項（index 0）；否則比對 Tag。
                // 比不到（外掛已移除）→ 退回 index 0＝跟隨系統（合理回退，比退到任意官方語言好）。
                string current = SettingsService.GetUiLanguage();
                int sel = 0;
                if (!string.IsNullOrEmpty(current))
                {
                    for (int i = 0; i < LanguageCombo.Items.Count; i++)
                    {
                        if (LanguageCombo.Items[i] is ComboBoxItem it &&
                            string.Equals(it.Tag as string, current, StringComparison.OrdinalIgnoreCase))
                        {
                            sel = i;
                            break;
                        }
                    }
                }
                LanguageCombo.SelectedIndex = sel;
            }
            finally
            {
                _suppressLanguageChange = false;
            }
        }

        /// <summary>容錯 string.Format：譯者若在 .resw 刪掉格式字串的 {0} 佔位符會擲出 FormatException，
        /// 故 try/catch，失敗回退原格式字串（不崩）。</summary>
        private static string SafeFormat(string format, string arg)
        {
            try { return string.Format(format, arg); }
            catch { return format; }
        }

        /// <summary>
        /// 取語言顯示名稱＝原名（CultureInfo.NativeName）+ 代碼，如「中文（台灣）(zh-TW)」。
        /// 取不到原名（無效碼等）時 try/catch 回退純 BCP47 碼（graceful degrade 不崩）。
        /// </summary>
        private static string DescribeLanguage(string bcp47)
        {
            try
            {
                string native = CultureInfo.GetCultureInfo(bcp47).NativeName;
                if (!string.IsNullOrWhiteSpace(native))
                    return $"{native} ({bcp47})";
            }
            catch { /* 無效碼等：回退 BCP47 */ }
            return bcp47;
        }

        /// <summary>
        /// 顯示語言下拉選單變更：寫共用偏好（Shared.ini），再提示重啟。
        /// 官方/社群一律走重啟（並非全部字串都能 live 重套）→ 統一重啟最乾淨、零遺漏、行為一致。
        /// </summary>
        private async void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLanguageChange) return;
            if (LanguageCombo.SelectedItem is not ComboBoxItem item) return;
            // Tag 為 BCP47 碼；空字串＝「跟隨系統」項（合法、寫空偏好不覆寫 PrimaryLanguageOverride）。
            if (item.Tag is not string bcp47) return;

            SettingsService.SetUiLanguage(bcp47);   // 空字串＝跟隨系統
            await PromptRestartForLanguageAsync();
        }

        /// <summary>
        /// 官方語言切換後提示重啟（重用 GamepadMessageDialog，手把導航內建）。
        /// primary=「立即重啟」→ AppInstance.Restart；close=「稍後」→ 下次啟動生效。
        /// </summary>
        private async Task PromptRestartForLanguageAsync()
        {
            var dialog = new GamepadMessageDialog(
                this.XamlRoot,
                _resourceLoader.Loc("LanguageRestartDialog_Title"),
                _resourceLoader.Loc("LanguageRestartDialog_Body"),
                _resourceLoader.Loc("LanguageRestartDialog_Restart"),
                _resourceLoader.Loc("LanguageRestartDialog_Later"),
                defaultToPrimary: true); // 預設醒目「立即重啟」方便直接確認
            await dialog.ShowAsync();

            if (dialog.Result)
            {
                // 重啟後導回設定頁，使用者才看得到語言已套用（沿用 PhantomLink 安裝完重啟的旗標機制）。
                SettingsService.SetPendingSettingsRestart(true);

                // 優先用 AppInstance.Restart（同實例重啟，立即終止本行程並由系統重新拉起）；失敗才回退 RequestRestartAsync。
                var result = Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                DebugLogger.Log($"[Language] AppInstance.Restart returned {result}");
                // 走到這裡表示 Restart 未成功終止本行程 → 回退
                await Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync("");
            }
            else
            {
                // 「稍後」：對話方塊關閉後焦點返回語言下拉選單。
                DispatcherQueue.TryEnqueue(() =>
                    LanguageCombo.Focus(FocusStateHelper.Preferred));
            }
        }

        /// <summary>
        /// 「管理社群語言」按鈕：開瀏覽/下載/移除對話方塊。關閉後若有變動 → 重掃語言資料夾 + 重新整理下拉選單
        /// （新裝語言才會立即出現），並把焦點還原至此按鈕。
        /// </summary>
        private async void ManageLanguagesButton_Click(object sender, RoutedEventArgs e)
        {
            bool needRestart = false;
            try
            {
                var dialog = new ManageLanguagesDialog(this.XamlRoot);
                await dialog.ShowAsync();

                if (dialog.Changed)
                {
                    PhantomLocalizer.Instance.RescanFolder(); // 重掃才能偵測到新裝/移除的語言
                    PopulateLanguageCombo();
                }
                needRestart = dialog.NeedsRestartForCurrentLanguage;
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                    ManageLanguagesButton.Focus(FocusStateHelper.Preferred));
            }

            // 更新了目前使用中語言 → 彈重啟提示（必在管理對話方塊關閉後彈：兩個 ContentDialog 不能同時開）。
            if (needRestart)
                await PromptRestartForLanguageAsync();
        }

        /// <summary>Mouse Mode 版面配置下拉選單切換時立即儲存。index 0=OmniNav、1=Classic。</summary>
        private void MouseModeLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SettingsService.SetMouseModeLayout(
                MouseModeLayoutCombo.SelectedIndex == 1 ? SettingsService.LayoutClassic : SettingsService.LayoutOmniNav);
        }

        /// <summary>導覽音效 ToggleSwitch 切換時立即儲存，並即時切換 ElementSoundPlayer 全域狀態。</summary>
        private void NavigationSoundsSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = NavigationSoundsSwitch.IsOn;
            SettingsService.SetEnableNavigationSounds(enabled);
            SettingsService.ApplyNavigationSoundsSetting();
        }

        // Game Bar 媒體櫃 / Passthrough 開關 UI 暫時隱藏（見 AdvancedView.xaml 註解），Toggled handler 一併停用。
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

        /// <summary>Cursor Speed 下拉選單選取變更時儲存百分比。</summary>
        private void CursorSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CursorSpeedCombo.SelectedIndex < 0) return;
            int pct = SettingsService.ValidCursorSpeedPercents[CursorSpeedCombo.SelectedIndex];
            SettingsService.SetCursorSpeedPercent(pct);
        }

        /// <summary>
        /// 套用 Mouse Mode 子控制項的反灰串聯：
        /// PhantomKey 主開關 + 內建廠商映射偵測 → Mouse Mode 主開關 → Layout / Cursor Speed。
        /// </summary>
        private void ApplyMouseModeEnabledState(bool? builtInMappingOverride = null)
        {
            bool builtIn = builtInMappingOverride ?? SettingsService.HasBuiltInGamepadMapping();
            // PhantomKey 改為 FSE 常駐，不再依開關；保留變數以利未來復原。
            bool phantomOn = true;
            bool mouseModeAvailable = phantomOn && !builtIn;
            bool mouseModeOn = mouseModeAvailable && MouseModeSwitch.IsOn;

            MouseModeSwitch.IsEnabled = mouseModeAvailable;
            MouseModeBuiltInMappingNoteText.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;

            // 版面配置與游標速度只在總開關 On 時有意義（Off 時內建與 JSON 皆不介入），與小工具一致
            MouseModeLayoutCombo.IsEnabled = mouseModeOn;
            CursorSpeedCombo.IsEnabled = mouseModeOn;
        }

        // ── 更新檢查 ───────────────────────────────────────────────────────────

        /// <summary>自動更新檢查開關切換。</summary>
        private void AutoUpdateCheckSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetAutoUpdateCheckEnabled(AutoUpdateCheckSwitch.IsOn);
            UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>手動檢查更新按鈕，強制抓取 GitHub API 並更新快取。</summary>
        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate) return;
            _isCheckingUpdate = true;

            CheckDeveloperMode(); // 使用者可能從設定頁回來後狀態已變更
            UpdateCheckStatusText.Text = _resourceLoader.Loc("UpdateCheck_Checking");
            UpdateCheckStatusText.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.Visibility = Visibility.Visible;
            CheckUpdateProgressRing.IsActive = true;

            var delayTask = Task.Delay(500);
            DebugLogger.Log($"[UpdCheck] entry=Manual tick={Environment.TickCount64}");
            var (kind, version) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();
            DebugLogger.Log($"[UpdCheck] entry=Manual result kind={kind} version={version} tick={Environment.TickCount64}");
            await delayTask;

            CheckUpdateProgressRing.IsActive = false;
            CheckUpdateProgressRing.Visibility = Visibility.Collapsed;
            _isCheckingUpdate = false;

            switch (kind)
            {
                case UpdateCheckService.UpdateKind.MissingPhantomLink:
                    UpdateCheckStatusText.Text = _resourceLoader.Loc("UpdateInfoBar_MissingPhantomLink_Title");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case UpdateCheckService.UpdateKind.MainAppUpdate:
                    UpdateCheckStatusText.Text = string.Format(
                        _resourceLoader.Loc("UpdateCheck_NewVersion_Subtitle"), version);
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Visible;
                    UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;

                case UpdateCheckService.UpdateKind.None:
                    UpdateCheckStatusText.Text = _resourceLoader.Loc("UpdateCheck_UpToDate_Subtitle");
                    UpdateCheckStatusText.Visibility = Visibility.Visible;
                    DownloadInstallButton.Visibility = Visibility.Collapsed;
                    UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }

        /// <summary>下載並安裝更新按鈕：解析快取連結後，透過事件請殼層執行安裝流程（殼層持有 MainWindow 安裝續做契約）。</summary>
        private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e)
        {
            // 安裝流程由殼層執行，重複觸發由殼層的 MainWindow.IsInstallFlowInProgress 防護。

            // 點選時再次確認開發人員模式，防止使用者中途關閉
            CheckDeveloperMode();
            if (!UpdateCheckService.IsDeveloperModeEnabled()) return;

            // 重新檢查最新版本，確保下載的是最新的而非過期快取
            DebugLogger.Log($"[UpdCheck] entry=Install tick={Environment.TickCount64}");
            var (kind, _) = await UpdateCheckService.CheckForUpdateAsync();
            DebugLogger.Log($"[UpdCheck] entry=Install result kind={kind} tick={Environment.TickCount64}");

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

            // 安裝流程由殼層執行（與 MainWindow 的安裝續做機制共用 RunInstallBundleWithDialogAsync）。
            InstallRequested?.Invoke(this, new InstallRequestEventArgs(
                phantomLinkUrl, mainUrl, targetVersion, mainSkippable));
        }

        /// <summary>自動檢查更新（跨日邏輯）：有更新時重新整理 InfoBar 與版本下方狀態。</summary>
        private async Task AutoCheckForUpdatesAsync()
        {
            DebugLogger.Log($"[UpdCheck] entry=Advanced-Auto tick={Environment.TickCount64}");
            var (kind, version) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();
            DebugLogger.Log($"[UpdCheck] entry=Advanced-Auto result kind={kind} version={version} tick={Environment.TickCount64}");

            if (kind != UpdateCheckService.UpdateKind.None)
            {
                UpdateInfoBarRefreshRequested?.Invoke(this, EventArgs.Empty);
                ShowCachedUpdateStatus();
            }
        }

        /// <summary>安裝失敗時在版本號下方顯示失敗訊息（由殼層的安裝流程在 catch 時呼叫）。</summary>
        internal void ShowInstallFailed()
        {
            UpdateCheckStatusText.Text = _resourceLoader.Loc("UpdateDownload_Failed");
            UpdateCheckStatusText.Visibility = Visibility.Visible;
        }

        /// <summary>安裝流程中止後把焦點還原至下載並安裝按鈕（由殼層在對話方塊關閉後呼叫）。</summary>
        internal void RestoreInstallButtonFocus()
        {
            DispatcherQueue.TryEnqueue(() =>
                DownloadInstallButton.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>背景材質變更使本頁重建後，把焦點還原至新頁的背景材質下拉選單（由殼層在重建後呼叫）。</summary>
        internal void RestoreBackgroundMaterialFocus() =>
            FocusNavHelper.RestoreFocusAfterRebuild(this, BackgroundMaterialCombo);

        /// <summary>依快取的 UpdateKind，在版本號下方顯示狀態文字與「下載並安裝」按鈕。</summary>
        private void ShowCachedUpdateStatus()
        {
            var kindStr = SettingsService.GetCachedUpdateKind();
            var cached = SettingsService.GetCachedNewVersion();

            if (kindStr == UpdateCheckService.UpdateKind.MissingPhantomLink.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = _resourceLoader.Loc("UpdateInfoBar_MissingPhantomLink_Title");
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                UpdateCheckStatusText.Text = string.Format(
                    _resourceLoader.Loc("UpdateCheck_NewVersion_Subtitle"), cached);
                UpdateCheckStatusText.Visibility = Visibility.Visible;
                DownloadInstallButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateCheckStatusText.Visibility = Visibility.Collapsed;
                DownloadInstallButton.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>檢查開發人員模式是否啟用，未啟用時顯示黃色警告並停用下載按鈕。</summary>
        private void CheckDeveloperMode()
        {
            bool enabled = UpdateCheckService.IsDeveloperModeEnabled();
            DeveloperModeWarningPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            if (!enabled)
                DeveloperModeWarningText.Text = _resourceLoader.Loc("DeveloperMode_Warning");
            DownloadInstallButton.IsEnabled = enabled;
        }

        /// <summary>開啟 Windows 開發人員模式設定頁面。</summary>
        private async void DeveloperModeOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:developers"));
        }
    }

    /// <summary>AdvancedView 請殼層執行安裝流程的事件參數。</summary>
    internal sealed class InstallRequestEventArgs : EventArgs
    {
        /// <summary>建立安裝請求參數。</summary>
        public InstallRequestEventArgs(string phantomLinkUrl, string mainUrl, string targetVersion, bool mainSkippable)
        {
            PhantomLinkUrl = phantomLinkUrl;
            MainUrl = mainUrl;
            TargetVersion = targetVersion;
            MainSkippable = mainSkippable;
        }

        /// <summary>PhantomLink msix 下載連結。</summary>
        public string PhantomLinkUrl { get; }
        /// <summary>主程式 msix 下載連結。</summary>
        public string MainUrl { get; }
        /// <summary>目標版本號。</summary>
        public string TargetVersion { get; }
        /// <summary>是否可略過主程式安裝（僅缺 PhantomLink 且版本相同時）。</summary>
        public bool MainSkippable { get; }
    }
}
