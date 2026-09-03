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
    /// 設定的進階分頁內容：更新檢查/下載安裝、Steam Overlay、貓又模式（含提權程式支援、控制器版面配置、游標速度）、
    /// 疊加層（Pro 專屬，RTSS 的螢幕顯示設定）、背景材質、顯示語言、社群語言管理、導覽音效。
    /// 由 SettingsHostView 透過 Frame.Navigate 載入（切走即銷毀）；本頁不實作手把 scope。
    /// 跨頁互動透過事件回報殼層：重新整理頂部更新 InfoBar、請殼層執行安裝流程、即時套用背景材質、切到手把映射頁開啟自訂版面編輯器。
    /// 入頁重新整理（Initialize）由殼層在導覽後、事件訂妥才呼叫（OnNavigatedTo 早於殼層訂閱、不可在此 invoke 事件）。
    /// </summary>
    public sealed partial class AdvancedView : Page
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 語言下拉選單初始化期間抑制 SelectionChanged（還原選取不應觸發儲存/重啟提示）
        private bool _suppressLanguageChange;

        // 防止檢查更新重複觸發
        private bool _isCheckingUpdate;

        // 防止安裝更新重複觸發：殼層的 MainWindow.IsInstallFlowInProgress 要等本方法內的
        // 更新檢查 await 之後才生效，該空窗期間的連點會各自打一次更新檢查 API。
        private bool _isRequestingInstall;

        // ── 殼層事件契約 ──────────────────────────────────────────────────────

        /// <summary>請殼層依快取的 UpdateKind 重新整理頂部 SettingsUpdateInfoBar（跨頁固定區塊留殼層）。</summary>
        internal event EventHandler? UpdateInfoBarRefreshRequested;

        /// <summary>
        /// 請殼層執行安裝流程（殼層持有與 MainWindow 安裝續做機制的契約 RunInstallBundleWithDialogAsync）。
        /// 回傳 Task 供本頁 await，使連點防呆旗標在整個安裝流程結束後才還原。
        /// </summary>
        internal Func<Task>? InstallRequested;

        /// <summary>背景材質變更：請殼層轉知 MainWindow 即時套用新材質並切換分層 brush。攜帶材質字串。</summary>
        internal event EventHandler<string>? BackgroundMaterialChanged;

        /// <summary>填入背景材質下拉選單並還原選取期間，抑制 SelectionChanged 觸發重套材質。</summary>
        private bool _suppressBackgroundMaterialChange;

        /// <summary>還原貓又模式開關的位置期間，抑制 Toggled handler 反過來寫回設定。</summary>
        private bool _suppressMouseModeToggled;

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

            // 還原 Mouse Mode（Off/On）/ 版面配置 / 游標速度。
            // 開關一律顯示儲存的真實值，反灰時也是。
            _suppressMouseModeToggled = true;
            MouseModeSwitch.IsOn = SettingsService.GetMouseMode() != SettingsService.MouseModeOff;
            _suppressMouseModeToggled = false;
            FillLayoutCombo();

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

            ApplyMouseModeEnabledState();

            // 提權程式支援（Pro 專屬）：依授權與安裝狀態切顯示與按鈕文字
            RefreshElevatedAppSupport();

            // 疊加層（Pro 專屬）：讀 RTSS 現值填控制項
            RefreshOverlaySection();

            // Xbox 模式 (FSE)：讀系統現值填兩個確認對話方塊的控制項
            RefreshFseDialogSection();

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

        /// <summary>提權服務的狀態變了之後，把相依於它的區塊一起重新整理，不重新載入整頁。</summary>
        private void RefreshElevationDependentSections()
        {
            RefreshElevatedAppSupport();
            RefreshOverlaySection();
        }

        /// <summary>
        /// 依 Pro 授權與提權工作安裝狀態，切換貓又區「提權程式支援」列的顯示與按鈕文字。
        /// 未持 Pro 整列 Collapsed。
        /// </summary>
        private void RefreshElevatedAppSupport()
        {
            bool isPro = LicenseService.HasEntitlement(LicenseService.Entitlement.Pro);
            ElevatedAppSupportSection.Visibility = isPro ? Visibility.Visible : Visibility.Collapsed;
            if (!isPro) return;

            bool installed = PhantomSigilService.IsInstalled();
            ElevatedAppSupportButton.Content = _resourceLoader.Loc(
                installed ? "ElevatedAppSupport_Remove" : "ElevatedAppSupport_Install");
            ElevatedAppSupportButton.Tag = installed;   // 記住目前狀態供 click 分派
        }

        // ── 疊加層（Pro 專屬）──────────────────────────────────────────────

        /// <summary>填控制項期間抑制 Toggled 與 SelectionChanged，避免還原值時反過來寫回去。</summary>
        private bool _suppressOverlayChange;

        /// <summary>提權服務是否已安裝；疊加層設定的寫入需要它。</summary>
        private bool _overlayElevationReady;

        /// <summary>RTSS 是否已安裝。沒安裝時整組控制項反灰並顯示提示。</summary>
        private bool _overlayRtssInstalled;

        /// <summary>RTSS 是否正在執行；與「沒安裝」分開判定，提示不同。</summary>
        private bool _overlayRtssRunning;

        /// <summary>幀率限制下拉選單的常用值；0 為不限。</summary>
        private static readonly int[] _overlayFpsLimits = { 0, 30, 40, 60, 90, 120, 144, 165, 240 };

        /// <summary>依 Pro 授權切整段顯隱，並讀 RTSS 現值填控制項。</summary>
        private void RefreshOverlaySection()
        {
            bool isPro = LicenseService.HasEntitlement(LicenseService.Entitlement.Pro);
            OverlaySection.Visibility = isPro ? Visibility.Visible : Visibility.Collapsed;
            if (!isPro) return;

            _overlayElevationReady = PhantomSigilService.IsInstalled();

            var state = RtssService.ReadState();
            _overlayRtssInstalled = state.Installed;
            _overlayRtssRunning = state.Running;

            _suppressOverlayChange = true;
            try
            {
                OverlayOsdSwitch.IsOn = state.EnableOsd == 1;
                OverlayStatSwitch.IsOn = state.EnableStat == 1;
                OverlayShadowSwitch.IsOn = state.EnableShadow == 1;

                FillOverlayZoomCombo(state.ZoomRatio);
                FillOverlayFpsLimitCombo(state.FramerateLimit);
            }
            finally
            {
                _suppressOverlayChange = false;
            }

            ApplyOverlayEnabledState();
        }

        /// <summary>顯示大小下拉選單：值域 1..8，直接對應 RTSS 的屬性值。</summary>
        private void FillOverlayZoomCombo(int? current)
        {
            OverlayZoomCombo.Items.Clear();
            for (int i = 1; i <= 8; i++)
                OverlayZoomCombo.Items.Add($"{i}x");

            int zoom = current is >= 1 and <= 8 ? current.Value : 1;
            OverlayZoomCombo.SelectedIndex = zoom - 1;
        }

        /// <summary>填幀率限制下拉選單；現值不在常用清單上時補一格顯示它。</summary>
        private void FillOverlayFpsLimitCombo(int? current)
        {
            OverlayFpsLimitCombo.Items.Clear();
            foreach (int limit in _overlayFpsLimits)
                OverlayFpsLimitCombo.Items.Add(FormatFpsLimit(limit));

            int value = current ?? 0;
            int index = Array.IndexOf(_overlayFpsLimits, value);
            if (index < 0)
            {
                OverlayFpsLimitCombo.Items.Add(FormatFpsLimit(value));
                index = OverlayFpsLimitCombo.Items.Count - 1;
            }
            OverlayFpsLimitCombo.SelectedIndex = index;
        }

        /// <summary>0 顯示成「不限」，其餘顯示數字。</summary>
        private string FormatFpsLimit(int value)
            => value == 0 ? _resourceLoader.Loc("OverlayFpsLimit_Unlimited") : value.ToString();

        /// <summary>依提權與 RTSS 狀態決定控制項可不可用，並顯示對應說明。</summary>
        private void ApplyOverlayEnabledState()
        {
            bool usable = _overlayElevationReady && _overlayRtssInstalled && _overlayRtssRunning;

            OverlayOsdSwitch.IsEnabled = usable;
            OverlayStatSwitch.IsEnabled = usable;
            OverlayShadowSwitch.IsEnabled = usable;
            OverlayZoomCombo.IsEnabled = usable;
            OverlayFpsLimitCombo.IsEnabled = usable;

            // 三則說明依先決條件的順序擇一顯示：沒提權 → 沒裝 → 裝了但沒在跑。
            OverlayElevationNoteText.Visibility =
                _overlayElevationReady ? Visibility.Collapsed : Visibility.Visible;
            OverlayRtssNoteText.Visibility =
                (_overlayElevationReady && !_overlayRtssInstalled) ? Visibility.Visible : Visibility.Collapsed;
            OverlayRtssStoppedNoteText.Visibility =
                (_overlayElevationReady && _overlayRtssInstalled && !_overlayRtssRunning)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>套用一項設定；失敗時把控制項退回 RTSS 的實際狀態。</summary>
        private async Task ApplyOverlayAsync(Func<RtssService.ApplyResult> apply)
        {
            var result = await Task.Run(apply);
            if (result == RtssService.ApplyResult.Success) return;

            RefreshOverlaySection();
        }

        /// <summary>螢幕顯示總開關。</summary>
        private async void OverlayOsdSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayChange) return;
            bool on = OverlayOsdSwitch.IsOn;
            await ApplyOverlayAsync(() => RtssService.Apply(enableOsd: on ? 1 : 0));
        }

        /// <summary>統計資訊開關。</summary>
        private async void OverlayStatSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayChange) return;
            bool on = OverlayStatSwitch.IsOn;
            await ApplyOverlayAsync(() => RtssService.Apply(enableStat: on ? 1 : 0));
        }

        /// <summary>文字陰影開關。</summary>
        private async void OverlayShadowSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressOverlayChange) return;
            bool on = OverlayShadowSwitch.IsOn;
            await ApplyOverlayAsync(() => RtssService.Apply(enableShadow: on ? 1 : 0));
        }

        /// <summary>顯示大小；選項索引加一即為 RTSS 的屬性值。</summary>
        private async void OverlayZoomCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressOverlayChange) return;
            if (OverlayZoomCombo.SelectedIndex < 0) return;

            int zoom = OverlayZoomCombo.SelectedIndex + 1;
            await ApplyOverlayAsync(() => RtssService.Apply(zoomRatio: zoom));
        }

        /// <summary>幀率限制；索引超出常用值清單時代表那格是 RTSS 原本的值，不重送。</summary>
        private async void OverlayFpsLimitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressOverlayChange) return;
            int index = OverlayFpsLimitCombo.SelectedIndex;
            if (index < 0 || index >= _overlayFpsLimits.Length) return;

            int limit = _overlayFpsLimits[index];
            await ApplyOverlayAsync(() => RtssService.Apply(framerateLimit: limit));
        }

        /// <summary>
        /// 安裝/移除提權工作。兩者都需一次 UAC（runas）；移除前以手把對話方塊確認。
        /// runas 放背景執行緒避免阻塞 UI（UAC 期間走 secure desktop）。
        /// </summary>
        private async void ElevatedAppSupportButton_Click(object sender, RoutedEventArgs e)
        {
            bool installed = ElevatedAppSupportButton.Tag is true;

            if (installed)
            {
                var confirm = new GamepadMessageDialog(
                    XamlRoot,
                    _resourceLoader.Loc("ElevatedAppSupportRemoveDialog_Title"),
                    _resourceLoader.Loc("ElevatedAppSupportRemoveDialog_Body"),
                    _resourceLoader.Loc("ElevatedAppSupportRemoveDialog_Confirm"),
                    _resourceLoader.Loc("ElevatedAppSupportRemoveDialog_Cancel"));
                await confirm.ShowAsync();
                if (!confirm.Result)
                {
                    DispatcherQueue.TryEnqueue(() => ElevatedAppSupportButton.Focus(FocusStateHelper.Preferred));
                    return;
                }
                bool ok = await Task.Run(() => PhantomSigilService.Uninstall());
                DebugLogger.Log($"[AdvancedView] Elevated service uninstall: {ok}");
                // master 收到 Off 後自盡需短暫時間，輪詢等它停（event 消失）再更新 UI。
                for (int i = 0; i < 20 && PhantomSigilService.IsInstalled(); i++) await Task.Delay(100);
            }
            else
            {
                if (!PhantomSigilService.CanUserElevate())
                {
                    DebugLogger.Log("[AdvancedView] Account cannot elevate; install blocked before UAC.");
                    // closeText 傳 null＝純資訊單按鈕，primaryText 當關閉按鈕字樣
                    var notice = new GamepadMessageDialog(
                        XamlRoot,
                        _resourceLoader.Loc("ElevatedAppSupportUnavailableDialog_Title"),
                        _resourceLoader.Loc("ElevatedAppSupportUnavailableDialog_Body"),
                        _resourceLoader.Loc("ElevatedAppSupportUnavailableDialog_Close"),
                        null);
                    await notice.ShowAsync();
                    DispatcherQueue.TryEnqueue(() => ElevatedAppSupportButton.Focus(FocusStateHelper.Preferred));
                    return;
                }

                bool ok = await Task.Run(() => PhantomSigilService.Install());
                DebugLogger.Log($"[AdvancedView] Elevated service install: {ok}");
                // master 啟動工作、建 event 需短暫時間，輪詢等它就緒再更新 UI。
                for (int i = 0; i < 20 && !PhantomSigilService.IsInstalled(); i++) await Task.Delay(100);
            }

            RefreshElevationDependentSections();
            DispatcherQueue.TryEnqueue(() => ElevatedAppSupportButton.Focus(FocusStateHelper.Preferred));

            // 服務狀態變了就讓 PhantomKey 重新就位：安裝後升到 High IL、移除後回一般權限
            // （master 自盡時會順手收掉它，不重啟手把映射就沒了）。
            await PhantomKeyService.ReconcileElevationAsync();
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

        /// <summary>
        /// Mouse Mode 開關切換時立即儲存，並更新子控制項反灰狀態。On=啟用、Off=停用。
        /// 內建廠商映射的機種要開啟時先徵詢確認。
        /// </summary>
        private async void MouseModeSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressMouseModeToggled) return;

            bool needsConfirm = MouseModeSwitch.IsOn && SettingsService.HasBuiltInGamepadMapping();
            if (needsConfirm && !await ConfirmEnableOnBuiltInMappingAsync())
            {
                // 取消：把開關撥回關閉。賦值會再觸發一次 Toggled，用旗標擋住避免遞迴寫入。
                _suppressMouseModeToggled = true;
                MouseModeSwitch.IsOn = false;
                _suppressMouseModeToggled = false;
                RestoreMouseModeSwitchFocus();
                return;
            }

            SettingsService.SetMouseMode(
                MouseModeSwitch.IsOn ? SettingsService.MouseModeOn : SettingsService.MouseModeOff);
            ApplyMouseModeEnabledState();

            // 對話方塊關掉後焦點留在它身上，確認與取消兩條路都要送回觸發的開關。
            if (needsConfirm) RestoreMouseModeSwitchFocus();
        }

        /// <summary>把焦點送回貓又模式開關。</summary>
        private void RestoreMouseModeSwitchFocus() =>
            DispatcherQueue.TryEnqueue(() => MouseModeSwitch.Focus(FocusStateHelper.Preferred));

        /// <summary>徵詢在內建廠商映射的機種上開啟貓又模式；回傳使用者是否確認。</summary>
        private async Task<bool> ConfirmEnableOnBuiltInMappingAsync()
        {
            var dlg = new GamepadMessageDialog(
                XamlRoot,
                _resourceLoader.Loc("BuiltInMappingEnableDialog_Title"),
                _resourceLoader.Loc("BuiltInMappingEnableDialog_Body"),
                _resourceLoader.Loc("BuiltInMappingEnableDialog_Confirm"),
                _resourceLoader.Loc("BuiltInMappingEnableDialog_Cancel"));
            await dlg.ShowAsync();
            return dlg.Result;
        }

        // ── Xbox 模式 (FSE) 確認對話方塊 ──────────────────────────────────────

        /// <summary>還原選取期間抑制 SelectionChanged，避免撥回選項時又寫一次登錄檔。</summary>
        private bool _suppressFseEnterConfirmChange;

        /// <summary>還原開關狀態期間抑制 Toggled，避免又寫一次登錄檔。</summary>
        private bool _suppressFseExitConfirmToggled;

        /// <summary>依主機是否支援 FSE 切整段顯隱，並讀系統現值填兩個確認對話方塊的控制項。</summary>
        private void RefreshFseDialogSection()
        {
            if (!FseService.IsSupported())
            {
                FseDialogSection.Visibility = Visibility.Collapsed;
                return;
            }
            FseDialogSection.Visibility = Visibility.Visible;

            _suppressFseEnterConfirmChange = true;
            FseEnterConfirmCombo.Items.Clear();
            FseEnterConfirmCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("FseEnterConfirmItem_Ask"), Tag = FseEnterConfirmation.Ask });
            FseEnterConfirmCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("FseEnterConfirmItem_Restart"), Tag = FseEnterConfirmation.RestartForPerformance });
            FseEnterConfirmCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("FseEnterConfirmItem_StartNow"), Tag = FseEnterConfirmation.StartNow });
            var current = FseService.GetEnterConfirmation();
            FseEnterConfirmCombo.SelectedIndex = IndexOfEnterConfirmation(current);
            _suppressFseEnterConfirmChange = false;

            UpdateFseEnterConfirmNote(current);

            _suppressFseExitConfirmToggled = true;
            FseExitConfirmSwitch.IsOn = FseService.GetExitConfirmationVisible();
            _suppressFseExitConfirmToggled = false;
        }

        /// <summary>回傳某個進入方式在下拉選單中的位置，順序與 RefreshFseDialogSection 填入的一致。</summary>
        private static int IndexOfEnterConfirmation(FseEnterConfirmation value) => value switch
        {
            FseEnterConfirmation.RestartForPerformance => 1,
            FseEnterConfirmation.StartNow => 2,
            _ => 0,
        };

        /// <summary>選了重新啟動才顯示補充說明。</summary>
        private void UpdateFseEnterConfirmNote(FseEnterConfirmation value) =>
            FseEnterConfirmNoteText.Visibility = value == FseEnterConfirmation.RestartForPerformance
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>
        /// 進入方式變更時寫入系統設定；選重新啟動要先徵詢確認，取消則把選取撥回登錄檔目前的值。
        /// </summary>
        private async void FseEnterConfirmCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressFseEnterConfirmChange) return;
            if (FseEnterConfirmCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not FseEnterConfirmation picked) return;

            bool needsConfirm = picked == FseEnterConfirmation.RestartForPerformance;
            if (needsConfirm && !await ConfirmRestartEveryTimeAsync())
            {
                // 取消：撥回登錄檔目前的值。賦值會再觸發一次 SelectionChanged，用旗標擋住避免遞迴寫入。
                _suppressFseEnterConfirmChange = true;
                FseEnterConfirmCombo.SelectedIndex = IndexOfEnterConfirmation(FseService.GetEnterConfirmation());
                _suppressFseEnterConfirmChange = false;
                RestoreFseEnterConfirmFocus();
                return;
            }

            FseService.SetEnterConfirmation(picked);
            UpdateFseEnterConfirmNote(picked);

            // 對話方塊關掉後焦點留在它身上，確認與取消兩條路都要送回觸發的下拉選單。
            if (needsConfirm) RestoreFseEnterConfirmFocus();
        }

        /// <summary>把焦點送回進入方式下拉選單。</summary>
        private void RestoreFseEnterConfirmFocus() =>
            DispatcherQueue.TryEnqueue(() =>
                FseEnterConfirmCombo.Focus(FocusStateHelper.Preferred));

        /// <summary>徵詢是否每次進入都直接重新啟動主機；回傳使用者是否確認。</summary>
        private async Task<bool> ConfirmRestartEveryTimeAsync()
        {
            var dlg = new GamepadMessageDialog(
                XamlRoot,
                _resourceLoader.Loc("FseEnterConfirmDialog_Title"),
                _resourceLoader.Loc("FseEnterConfirmDialog_Body"),
                _resourceLoader.Loc("FseEnterConfirmDialog_Confirm"),
                _resourceLoader.Loc("FseEnterConfirmDialog_Cancel"));
            await dlg.ShowAsync();
            return dlg.Result;
        }

        /// <summary>返回桌面確認開關變更時寫入系統設定。</summary>
        private void FseExitConfirmSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressFseExitConfirmToggled) return;
            FseService.SetExitConfirmationVisible(FseExitConfirmSwitch.IsOn);
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

        private bool _suppressLayoutSelection = false;

        /// <summary>使用者按下「編輯…」時通知宿主切到手把映射頁並開啟自訂版面編輯器。</summary>
        internal event EventHandler? EditCustomLayoutRequested;

        /// <summary>
        /// 填入版面配置下拉選單並還原選取。「自訂」只在持有 Pro 時列入，未持有時整項不存在。
        /// 還原期間抑制 SelectionChanged：未持有 Pro 而設定值是「自訂」時會落回索引 0，
        /// 那是「顯示實際生效的版面」而不是使用者的選擇，寫回去就會把他存的設定覆蓋掉。
        /// </summary>
        private void FillLayoutCombo()
        {
            _suppressLayoutSelection = true;

            MouseModeLayoutCombo.Items.Clear();
            MouseModeLayoutCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("ControllerLayoutPresetItem_OmniNav"), Tag = SettingsService.LayoutOmniNav });
            MouseModeLayoutCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("ControllerLayoutPresetItem_Classic"), Tag = SettingsService.LayoutClassic });

            if (LicenseService.HasEntitlement(LicenseService.Entitlement.Pro))
                MouseModeLayoutCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("ControllerLayoutPresetItem_Custom"), Tag = SettingsService.LayoutCustom });

            string current = SettingsService.GetMouseModeLayout();
            int index = 0;
            for (int i = 0; i < MouseModeLayoutCombo.Items.Count; i++)
                if (MouseModeLayoutCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == current) { index = i; break; }
            MouseModeLayoutCombo.SelectedIndex = index;

            _suppressLayoutSelection = false;
            UpdateEditCustomLayoutButtonVisibility();
        }

        /// <summary>
        /// 「編輯…」只在目前選的是自訂版面時出現：選 OmniNav 或經典時它沒有可編輯的對象。
        /// 未持有 Pro 時清單裡沒有那一項，故也不會出現。
        /// </summary>
        private void UpdateEditCustomLayoutButtonVisibility()
        {
            bool isCustom = MouseModeLayoutCombo.SelectedItem is ComboBoxItem item
                            && (item.Tag as string) == SettingsService.LayoutCustom;
            EditCustomLayoutButton.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>版面配置下拉選單切換時立即儲存，值取自選項的 Tag。</summary>
        private void MouseModeLayoutCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressLayoutSelection) return;
            SaveSelectedLayout();
        }

        /// <summary>
        /// 下拉選單關閉時補寫一次：選項索引沒變時 SelectionChanged 不會觸發，但使用者確實選了。
        /// 這條專為「未持有 Pro 而存的值是自訂版面」那個狀態存在：清單裡沒有「自訂」，
        /// 畫面落在 OmniNav，此時再點一次 OmniNav 索引不變，什麼都不會發生，
        /// 使用者會以為自己設好了。小工具那側是按鈕、點什麼寫什麼，兩邊得一致。
        /// </summary>
        private void MouseModeLayoutCombo_DropDownClosed(object sender, object e)
        {
            if (_suppressLayoutSelection) return;
            SaveSelectedLayout();
        }

        /// <summary>把目前選取的版面寫回設定；與已存的值相同時不寫，免得無謂改動觸發 PhantomKey 重新載入。</summary>
        private void SaveSelectedLayout()
        {
            if (MouseModeLayoutCombo.SelectedItem is not ComboBoxItem item) return;
            string picked = item.Tag as string ?? SettingsService.LayoutOmniNav;
            if (picked == SettingsService.GetMouseModeLayout()) return;
            SettingsService.SetMouseModeLayout(picked);
            UpdateEditCustomLayoutButtonVisibility();
        }

        /// <summary>把白框焦點落回「編輯…」按鈕，供自訂版面編輯器返回時還原（本頁是用完即丟、返回時整頁重建）。</summary>
        internal void FocusEditCustomLayoutButton()
        {
            if (EditCustomLayoutButton.Visibility != Visibility.Visible) return;
            FocusNavHelper.RestoreFocusAfterRebuild(this, EditCustomLayoutButton);
        }

        /// <summary>「編輯…」按鈕：編輯器住手把映射頁的內層 Frame，故把請求交給宿主處理。</summary>
        private void EditCustomLayoutButton_Click(object sender, RoutedEventArgs e)
            => EditCustomLayoutRequested?.Invoke(this, EventArgs.Empty);

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
        /// 內建廠商映射的機種持有 Pro 時開關可用，兩則說明依授權擇一顯示。
        /// </summary>
        private void ApplyMouseModeEnabledState()
        {
            bool builtIn = SettingsService.HasBuiltInGamepadMapping();
            bool isPro = LicenseService.HasEntitlement(LicenseService.Entitlement.Pro);
            // PhantomKey 改為 FSE 常駐，不再依開關；保留變數以利未來復原。
            bool phantomOn = true;
            bool mouseModeAvailable = phantomOn && (!builtIn || isPro);
            bool mouseModeOn = mouseModeAvailable && MouseModeSwitch.IsOn;

            MouseModeSwitch.IsEnabled = mouseModeAvailable;
            // 未持 Pro 講停用與如何解鎖，持有 Pro 講怎麼與廠商映射並存；兩則互斥。
            MouseModeBuiltInMappingNoteText.Visibility = (builtIn && !isPro) ? Visibility.Visible : Visibility.Collapsed;
            MouseModeBuiltInMappingProNoteText.Visibility = (builtIn && isPro) ? Visibility.Visible : Visibility.Collapsed;

            // 版面配置與游標速度只在總開關 On 時有意義（Off 時內建與 JSON 皆不介入），與小工具一致
            MouseModeLayoutCombo.IsEnabled = mouseModeOn;
            EditCustomLayoutButton.IsEnabled = mouseModeOn;
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

        /// <summary>
        /// 下載並安裝更新按鈕：確認開發人員模式後，透過事件請殼層執行安裝流程
        /// （殼層依序跑前置檢查、更新檢查與安裝，並持有 MainWindow 安裝續做契約）。
        /// </summary>
        private async void DownloadInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isRequestingInstall) return;
            _isRequestingInstall = true;
            try
            {
                // 點選時再次確認開發人員模式，防止使用者中途關閉
                CheckDeveloperMode();
                if (!UpdateCheckService.IsDeveloperModeEnabled()) return;

                await (InstallRequested?.Invoke() ?? Task.CompletedTask);
            }
            finally
            {
                // 安裝成功會由 ForceApplicationShutdown 結束本行程、不會執行到此；
                // 安裝失敗或使用者於前置對話方塊取消時還原旗標，允許重新觸發。
                _isRequestingInstall = false;
            }
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
}
