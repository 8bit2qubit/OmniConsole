using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Services;
using System;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings
{
    /// <summary>
    /// 設定介面的宿主 Page。
    /// 以內層 Frame 切換各設定分頁（一般 / 進階 / 疑難排解 / 關於 / 手把映射），並將手把各鍵語意轉發給目前分頁；
    /// 更新安裝流程的對話方塊亦由本頁驅動。
    /// </summary>
    public sealed partial class SettingsHostView : Page, IGamepadInputScope
    {
        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>手把 B 鍵（導覽未展開時）或「退出」按鈕點選時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        /// <summary>手把 Menu 鍵觸發，通知 MainWindow 直接啟動目前選取的平台（跳過手動 FSE 切換流程）。</summary>
        public event EventHandler? LaunchPlatformDirectlyRequested;

        /// <summary>進階分頁變更背景材質，轉知 MainWindow 即時套用新材質的視窗背景（根 Grid 背景與自繪桌布玻璃層）。攜帶材質字串。</summary>
        public event EventHandler<string>? BackgroundMaterialChangeRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 ShowWindow (退出隱藏) 使用。</summary>
        public IntPtr Hwnd { get; set; }

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private readonly ResourceLoader _resourceLoader = new();

        // 目前顯示的設定導覽頁面（General / Advanced / Troubleshoot）
        private string _currentNavTag = "General";

        // 手把映射編輯器待辦：從 Protocol 進來時暫存 appId / displayName，ShowSettings 取出
        private OmniConsole.Models.AppId? _pendingEditAppId;
        private string _pendingEditDisplayName = string.Empty;

        public SettingsHostView()
        {
            InitializeComponent();
        }

        // ── 子分頁 Frame 取用（用完即丟，Content 隨切頁重建，須每次動態取）──────

        /// <summary>目前內容 Frame 是一般分頁時取其宿主（非該分頁時為 null）。</summary>
        private General.GeneralHostView? CurrentGeneral => SettingsContentFrame.Content as General.GeneralHostView;

        /// <summary>目前內容 Frame 是手把映射分頁時取其內層宿主（非該分頁時為 null）。</summary>
        private GamepadMapping.GamepadMappingHostView? CurrentMappingHost => SettingsContentFrame.Content as GamepadMapping.GamepadMappingHostView;

        /// <summary>
        /// 導覽某 Frame 到目標頁並設為切走即銷毀（NavigationCacheMode=Disabled）。
        /// 無條件重新導覽：每次切入都重建該頁、徹底用完即丟（與手把映射內層一致），不沿用上次實例。
        /// 套用頁面切換動畫；不清 BackStack。
        /// 導覽後排一次非阻塞背景回收：用完即丟每次切頁丟棄整棵舊頁 XAML 樹，需主動提示 GC 否則閾值未到不收、記憶體單調上升。
        /// 回傳新內容（給呼叫端注入 HWND/訂閱事件/呼叫初始化）。
        /// </summary>
        private Page? NavigateFrame(Frame frame, Type pageType)
        {
            FocusNavHelper.NavigatePage(frame, pageType);
            if (frame.Content is Page p)
                p.NavigationCacheMode = NavigationCacheMode.Disabled;
            PageLifecycleHelper.CollectDiscardedPage();
            return frame.Content as Page;
        }

        /// <summary>從 LocalSettings 取 Protocol 進來時暫存的 appId / displayName；取出後立刻刪除。</summary>
        private void ConsumePendingEditProfileRequest()
        {
            PendingEditProfileService.TryConsume(out _pendingEditAppId, out _pendingEditDisplayName);
        }

        /// <summary>進入手把映射分頁的初始化：導覽內層宿主、訂狀態事件，把 Protocol 帶入的待編輯 appId 交給宿主決定開清單或編輯器。</summary>
        private void InitGamepadMappingPage()
        {
            var host = NavigateFrame(SettingsContentFrame, typeof(GamepadMapping.GamepadMappingHostView)) as GamepadMapping.GamepadMappingHostView;
            if (host == null) return;

            host.StateChanged -= MappingHost_StateChanged;
            host.StateChanged += MappingHost_StateChanged;
            host.CustomLayoutEditorClosed -= MappingHost_CustomLayoutEditorClosed;
            host.CustomLayoutEditorClosed += MappingHost_CustomLayoutEditorClosed;

            var id = _pendingEditAppId;
            var name = _pendingEditDisplayName;
            bool custom = _pendingCustomLayoutEdit;
            _pendingEditAppId = null;
            _pendingEditDisplayName = string.Empty;
            _pendingCustomLayoutEdit = false;
            host.Initialize(id, name, custom);
        }

        // 進階頁按下「編輯…」後待處理：切到手把映射分頁時據此直開自訂版面編輯器
        private bool _pendingCustomLayoutEdit;

        /// <summary>進階頁要求編輯自訂版面：編輯器住手把映射分頁，故先立旗標再把導覽切過去。</summary>
        private void AdvancedView_EditCustomLayoutRequested(object? sender, EventArgs e)
        {
            _pendingCustomLayoutEdit = true;
            SelectNavTab("GamepadMapping");
        }

        // 自訂版面編輯器返回進階頁後，把焦點還給使用者按下的那顆「編輯…」
        private bool _focusEditCustomLayoutOnAdvanced;

        /// <summary>自訂版面編輯器關閉：把導覽切回進階頁，使用者才回得到他按「編輯…」的地方。</summary>
        private void MappingHost_CustomLayoutEditorClosed(object? sender, EventArgs e)
        {
            _focusEditCustomLayoutOnAdvanced = true;
            SelectNavTab("Advanced");
        }

        /// <summary>把左側導覽切到指定 Tag 的分頁；找不到時不動作。</summary>
        private void SelectNavTab(string tag)
        {
            foreach (var item in SettingsNav.MenuItems)
            {
                if (item is NavigationViewItem nav && nav.Tag?.ToString() == tag)
                {
                    SettingsNav.SelectedItem = nav;
                    return;
                }
            }
        }

        /// <summary>內層宿主清單/編輯器切換或清單變動後重評底部手把提示列。</summary>
        private void MappingHost_StateChanged(object? sender, EventArgs e) => UpdateGamepadHints();

        /// <summary>目前是否在手把映射編輯器頁。</summary>
        internal bool IsGamepadMappingEditorVisible =>
            _currentNavTag == "GamepadMapping" && CurrentMappingHost?.IsEditorVisible == true;

        /// <summary>目前是否在手把映射清單頁。</summary>
        private bool IsGamepadMappingListVisible =>
            _currentNavTag == "GamepadMapping" && CurrentMappingHost?.IsListVisible == true;

        /// <summary>處理 B 鍵：編輯器頁 = 儲存並返回，清單頁交給一般退出邏輯。</summary>
        private bool TryHandleGamepadMappingBackKey()
        {
            if (_currentNavTag != "GamepadMapping") return false;
            return CurrentMappingHost?.TrySave() == true;
        }

        /// <summary>處理 X 鍵：編輯器頁=刪目前 profile；清單頁=刪選中項。</summary>
        private bool TryHandleGamepadMappingDeleteKey()
        {
            if (_currentNavTag != "GamepadMapping") return false;
            return CurrentMappingHost?.TryDelete() == true;
        }

        /// <summary>X 鍵提示按鈕的滑鼠點選處理（清單頁 / 編輯器頁都共用）。</summary>
        private void DeleteProfileHintButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMappingHost?.TryDelete();
        }

        /// <summary>B 鍵儲存並返回的提示按鈕滑鼠點選處理（編輯器頁專用）。</summary>
        private void SaveProfileHintButton_Click(object sender, RoutedEventArgs e)
        {
            CurrentMappingHost?.TrySave();
        }

        /// <summary>Y 鍵搜尋切換的提示按鈕滑鼠點選處理：在搜尋方塊與清單之間切換焦點。</summary>
        private void SearchToggleHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsGamepadMappingListVisible) CurrentMappingHost?.ToggleSearchFocus();
        }

        // ── 設定介面初始化 ────────────────────────────────────────────────────

        /// <summary>
        /// 初始化設定介面各控制項狀態，並啟動手把輪詢與平台可用性查詢。
        /// 切到設定頁由 <see cref="OmniConsole.MainWindow.ShowSettings"/> 負責，本方法於其後呼叫。
        /// </summary>
        public void ShowSettings()
        {
            DebugLogger.Log($"[DIAG] SettingsHostView.ShowSettings pid={Environment.ProcessId} tick={Environment.TickCount64}");
            // Protocol 帶入的待編輯 appId / displayName 在這裡先取出，下方視情況用來自動跳到手把映射編輯器
            ConsumePendingEditProfileRequest();

            // PhantomLink 可能已直接改動 Shared.ini，先從共用儲存同步回 LocalSettings
            SettingsService.ReloadFromSharedStore();

            // 先設好狀態，再賦值 SelectedItem。
            _currentNavTag = "General";

            // 初始化 NavigationView，預設選取第一個「一般」項目。
            // 暫解 SelectionChanged 再賦值再訂回：賦值是否觸發 SelectionChanged 視框架選取是否已實體化而定，不可靠；
            // 不解的話冷啟動時這次賦值與下方 ActivateTab 會雙雙導覽 General、整頁載入兩遍。改由下方明確呼叫 ActivateTab 單一導覽，與 GeneralHostView.Initialize 一致。
            SettingsNav.SelectionChanged -= SettingsNav_SelectionChanged;
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            SettingsNav.SelectionChanged += SettingsNav_SelectionChanged;

            // 顯示版本號
            VersionText.Text = $"v{SettingsService.GetAppVersion()}";

            // PhantomKey 已改為 FSE 常駐，不再有獨立開關。
            // UI 開關雖然註解保留，但這裡直接判斷 FSE → 啟動，確保更新重啟後恢復。
            //UsePhantomKeySwitch.IsOn = SettingsService.GetUsePhantomKey();
            //if (FseService.IsActive() && UsePhantomKeySwitch.IsOn)
            //    PhantomKeyService.Start();
            if (FseService.IsActive())
                PhantomKeyService.Start();

            // 導覽目前分頁 Frame 並完成初始化（HWND 注入、事件訂閱、各頁重新整理集中於 ActivateTab）。
            ActivateTab(_currentNavTag);

            // 主動重新整理底部手把提示列：上方已暫解 SelectionChanged 賦值，初始提示列不再隨賦值副作用觸發，由殼層進設定頁時明確驅動。
            UpdateGamepadHints();

            // 進設定頁的進場音：上方已暫解 SelectionChanged 賦值，不再隨賦值副作用觸發，由殼層明確播一聲。
            // 下方 Protocol 帶 appId 時會切到手把映射分頁、那次真實 SelectionChanged 自會播音，故僅在無待編輯跳轉時播以免兩聲。
            if (_pendingEditAppId == null)
                GamepadNavigationService.PlaySound(Microsoft.UI.Xaml.ElementSoundKind.Invoke);

            // 跨頁固定區塊：依快取的更新狀態重新整理頂部 InfoBar。由殼層在進設定頁時主動重新整理，
            // 不依賴進階分頁被導覽（進階分頁用完即丟、未進過時不會發出重新整理事件）。
            ShowSettingsUpdateInfoBar();

            // 自動檢查更新亦由殼層在進設定頁時驅動：檢查會寫入快取，若綁在進階分頁被導覽時才跑，
            // 未進過進階分頁時快取一直不更新、上方 InfoBar 只讀得到舊快取。跨日與開關的條件由 ShouldAutoCheck 把關。
            _ = MaybeAutoCheckForUpdatesAsync();

            // 由 Protocol 帶入待編輯 appId 時，把 NavigationView 切到「手把映射」分頁
            //（SelectionChanged → ActivateTab → InitGamepadMappingPage 會處理 _pendingEditAppId 開編輯器）
            if (_pendingEditAppId != null)
            {
                foreach (var item in SettingsNav.MenuItems)
                {
                    if (item is NavigationViewItem nav && nav.Tag?.ToString() == "GamepadMapping")
                    {
                        SettingsNav.SelectedItem = nav;
                        break;
                    }
                }
            }
        }

        // ── VSM 狀態輔助方法 ─────────────────────────────────────────────────────

        /// <summary>
        /// 依目前導覽頁面、分類索引標籤及免責聲明同意狀態，更新底部手把提示列的按鍵圖示。
        /// 應於 <see cref="_currentNavTag"/> 變更、或一般分頁分類索引/同意狀態變更（GeneralHostView 事件回報）後呼叫。
        /// </summary>
        private void UpdateGamepadHints()
        {
            if (_currentNavTag == "GamepadMapping")
            {
                // 先把 General 頁的 Y/X/LBRB 等 setter 清回基礎狀態，再套用編輯器/清單頁專屬手把提示列
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                bool editor = IsGamepadMappingEditorVisible;
                VisualStateManager.GoToState(this, editor ? "GamepadMappingEditorTab" : "GamepadMappingListTab", false);
                GamepadHintMenu.Visibility = Visibility.Collapsed;
                bool canDelete = CurrentMappingHost?.CanDelete == true;
                bool hasItems = CurrentMappingHost?.HasItems == true;
                GamepadHintXDelete.Visibility = (editor ? canDelete : hasItems)
                    ? Visibility.Visible : Visibility.Collapsed;
                GamepadHintLBRBPaging.Visibility = (editor ? false : hasItems)
                    ? Visibility.Visible : Visibility.Collapsed;
                GamepadHintYSearchToggle.Visibility = (editor ? false : hasItems)
                    ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            if (_currentNavTag != "General")
            {
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                GamepadHintMenu.Visibility = Visibility.Collapsed;
                // 還原映射頁可能留下的特殊提示按鈕（離開時要藏回去；Exit 要顯示）
                GamepadHintXDelete.Visibility = Visibility.Collapsed;
                GamepadHintBSaveReturn.Visibility = Visibility.Collapsed;
                GamepadHintLBRBPaging.Visibility = Visibility.Collapsed;
                GamepadHintYSearchToggle.Visibility = Visibility.Collapsed;
                GamepadHintExit.Visibility = Visibility.Visible;
                return;
            }
            // 從手把映射回到 General 時也還原特殊提示按鈕
            GamepadHintXDelete.Visibility = Visibility.Collapsed;
            GamepadHintBSaveReturn.Visibility = Visibility.Collapsed;
            GamepadHintLBRBPaging.Visibility = Visibility.Collapsed;
            GamepadHintYSearchToggle.Visibility = Visibility.Collapsed;
            GamepadHintExit.Visibility = Visibility.Visible;

            bool isUserTab = CurrentGeneral?.IsCurrentCategoryUser == true;
            bool showYX = isUserTab && SettingsService.GetCustomPlatformConsentAccepted();
            string state = showYX ? "UserTabWithConsent"
                : isUserTab ? "UserTabNoConsent"
                : "SystemTab";
            VisualStateManager.GoToState(this, state, false);

            // Menu 提示不依賴 VSM 結果，直接根據條件計算：非 UserTabNoConsent 且在 FSE 中才顯示
            GamepadHintMenu.Visibility = (state != "UserTabNoConsent" && FseService.IsActive())
                ? Visibility.Visible : Visibility.Collapsed;
        }


        // ── NavigationView 事件 ───────────────────────────────────────────────

        /// <summary>
        /// 側邊欄展開/收合時，調整 NavigationView 與底部手把提示列的疊放關係，避免浮層
        /// 最下方項目（如「關於」）被較長的提示列壓住。
        /// </summary>
        private void SettingsNav_PaneOpening(NavigationView sender, object args)
        {
            FocusNavHelper.ApplyPaneOverlayZIndex(SettingsNav, paneOpen: true);
        }

        /// <summary>
        /// 側邊欄收合時，把 NavigationView 還原回底層，讓底部手把提示列浮回上層、維持滑鼠可點。
        /// 與 <see cref="SettingsNav_PaneOpening"/> 對稱。
        /// </summary>
        private void SettingsNav_PaneClosing(NavigationView sender, NavigationViewPaneClosingEventArgs args)
        {
            FocusNavHelper.ApplyPaneOverlayZIndex(SettingsNav, paneOpen: false);
        }

        /// <summary>
        /// 處理 NavigationView 選項變更，切換內容頁面。
        /// </summary>
        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            DebugLogger.Log($"[DIAG] SettingsNav_SelectionChanged tick={Environment.TickCount64} tag={(args.SelectedItemContainer as NavigationViewItem)?.Tag}");
            if (args.SelectedItemContainer is NavigationViewItem selectedItem)
            {
                if (selectedItem.Tag?.ToString() is not string tag) return;

                // 切換頁面並更新提示列；NavigationViewItem 點選預設不播音，於此補 Invoke 音讓滑鼠/觸控路徑也有回饋。
                // 走 GamepadNavigationService.PlaySound 共用同輪去重，避免手把 A 鍵 tick 播音與本事件雙觸發。
                _currentNavTag = tag;
                ActivateTab(tag);
                UpdateGamepadHints();
                GamepadNavigationService.PlaySound(Microsoft.UI.Xaml.ElementSoundKind.Invoke);
            }
        }

        /// <summary>
        /// 導覽目標分頁 Frame 並完成該頁初始化（切走即銷毀，每次都重建並重訂事件/注入 HWND）。
        /// SelectionChanged 與 ShowSettings 共用，後者涵蓋「重入設定頁但 SelectedItem 未變、不觸發 SelectionChanged」的情形。
        /// </summary>
        private void ActivateTab(string tag)
        {
            switch (tag)
            {
                case "General":
                    var g = NavigateFrame(SettingsContentFrame, typeof(General.GeneralHostView)) as General.GeneralHostView;
                    if (g != null)
                    {
                        g.StateChanged -= SubView_HintsDirty;
                        g.StateChanged += SubView_HintsDirty;
                        g.Hwnd = Hwnd;
                        g.Initialize();
                    }
                    break;

                case "Advanced":
                    var a = NavigateFrame(SettingsContentFrame, typeof(AdvancedView)) as AdvancedView;
                    if (a != null)
                    {
                        a.UpdateInfoBarRefreshRequested -= AdvancedView_UpdateInfoBarRefreshRequested;
                        a.UpdateInfoBarRefreshRequested += AdvancedView_UpdateInfoBarRefreshRequested;
                        a.InstallRequested = RunInstallFlowAsync;
                        a.BackgroundMaterialChanged -= AdvancedView_BackgroundMaterialChanged;
                        a.BackgroundMaterialChanged += AdvancedView_BackgroundMaterialChanged;
                        a.EditCustomLayoutRequested -= AdvancedView_EditCustomLayoutRequested;
                        a.EditCustomLayoutRequested += AdvancedView_EditCustomLayoutRequested;
                        a.Initialize();
                        if (_focusEditCustomLayoutOnAdvanced)
                        {
                            _focusEditCustomLayoutOnAdvanced = false;
                            a.FocusEditCustomLayoutButton();
                        }
                        _ = a.MaybeAutoCheckForUpdatesAsync();
                    }
                    break;

                case "Troubleshoot":
                    var t = NavigateFrame(SettingsContentFrame, typeof(TroubleshootView)) as TroubleshootView;
                    t?.SetHwnd(Hwnd);
                    break;

                case "Pro":
                    var p = NavigateFrame(SettingsContentFrame, typeof(ProView)) as ProView;
                    if (p != null)
                    {
                        p.SetHwnd(Hwnd);
                        p.Initialize();
                    }
                    break;

                case "About":
                    // AboutView 在 OnNavigatedTo 內擷取環境快照，毋須額外呼叫。
                    NavigateFrame(SettingsContentFrame, typeof(AboutView));
                    break;

                case "GamepadMapping":
                    InitGamepadMappingPage();
                    break;
            }
        }

        /// <summary>子分頁分類索引/同意狀態變更後重評底部手把提示列。</summary>
        private void SubView_HintsDirty(object? sender, EventArgs e) => UpdateGamepadHints();

        /// <summary>進階分頁請求重新整理頂部更新 InfoBar。</summary>
        private void AdvancedView_UpdateInfoBarRefreshRequested(object? sender, EventArgs e) => ShowSettingsUpdateInfoBar();

        /// <summary>
        /// 進階分頁變更背景材質：更新 App 級分層 brush、轉知 MainWindow 套視窗背景（根 Grid 背景與自繪桌布玻璃層），
        /// 並重新導覽目前分頁讓卡片以新分層 brush 重建（背景材質下拉只在進階分頁，目前頁即進階分頁）。
        /// 導覽選單面板背景於下次進設定頁時隨之更新（NavigationView 重新解析面板背景 brush）。
        /// </summary>
        private void AdvancedView_BackgroundMaterialChanged(object? sender, string material)
        {
            BackgroundMaterialService.ApplyAppResources(material);
            BackgroundMaterialChangeRequested?.Invoke(this, material);
            ActivateTab(_currentNavTag);
            // 重建後把焦點還原至新頁的背景材質下拉（原下拉實例已隨舊頁銷毀）。
            if (SettingsContentFrame.Content is AdvancedView rebuilt) rebuilt.RestoreBackgroundMaterialFocus();
        }

        /// <summary>
        /// 進階分頁按下下載並安裝後的完整流程：依序跑 PhantomPaw 前置擋、更新檢查、安裝。
        /// 更新檢查排在前置擋之後，使用者於前置對話方塊取消時即不會多打一次更新檢查 API。
        /// 無可用下載連結時回退開啟發行頁。整段以 MainWindow.IsInstallFlowInProgress 上鎖。
        /// </summary>
        private async Task RunInstallFlowAsync()
        {
            if (MainWindow.IsInstallFlowInProgress) return;
            MainWindow.IsInstallFlowInProgress = true;
            try
            {
                // 前置擋：PhantomPaw dll 被鎖住時彈對話方塊鎖住著，使用者取消則放棄整個安裝流程。
                if (!await CheckAndPromptLockedAppsAsync())
                {
                    // 取消鎖定對話方塊後，把焦點還原至觸發安裝的下載並安裝按鈕（該按鈕在進階分頁內）。
                    (SettingsContentFrame.Content as AdvancedView)?.RestoreInstallButtonFocus();
                    return;
                }

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

                await RunInstallBundleWithDialogInternalAsync(
                    phantomLinkUrl, mainUrl, targetVersion, mainSkippable, resumeFromPhase2: false);
            }
            finally
            {
                MainWindow.IsInstallFlowInProgress = false;
            }
        }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 手把輸入處理（IGamepadInputScope） ──
        // 輪詢計時器由 MainWindow 集中管理；本頁只宣告「焦點搜尋根 + 各鍵語意」，全套覆寫含 X/Y/LB/RB/Menu。

        /// <summary>焦點搜尋根：D-pad 在設定導覽容器內找下一個焦點元素。</summary>
        UIElement IGamepadInputScope.SearchRoot => this.SettingsNav;

        void IGamepadInputScope.OnA() => OnGamepadAButtonPressed();
        bool IGamepadInputScope.OnB() { OnGamepadBButtonPressed(); return true; }
        void IGamepadInputScope.OnX() => OnGamepadXButtonPressed();
        void IGamepadInputScope.OnY() => OnGamepadYButtonPressed();
        void IGamepadInputScope.OnLB() => OnGamepadLBPressed();
        void IGamepadInputScope.OnRB() => OnGamepadRBPressed();
        void IGamepadInputScope.OnMenu() => OnGamepadMenuButtonPressed();

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式（設定介面）。
        /// 有額外副作用的焦點元素明列處理（平台卡片設選取狀態、導覽項收側邊欄、漢堡按鈕開合、返回按鈕攔截）；
        /// 其餘按鈕 / 開關 / 下拉 / 連結交給 ActivateFocusedElement 通用觸發（AutomationPeer Invoke / Toggle / Expand）。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);

            // 手把映射分頁有自己的 A 鍵語意：焦點在內容區時才走專屬邏輯，
            // 焦點在左側 NavigationView / 漢堡 / 返回按鈕 時讓 switch 走預設處理(切 NavigationView / 開合 pane)
            if (_currentNavTag == "GamepadMapping" &&
                focused is DependencyObject focusedDep && GamepadNavigationService.IsDescendantOf(SettingsContentFrame, focusedDep))
            {
                if (IsGamepadMappingEditorVisible)
                {
                    GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);
                    return;
                }
                // 清單頁：焦點落在 ListView / 列項時呼叫 EditSelected，垃圾桶 Button 走一般觸發
                if (focused is ListView || focused is SelectorItem)
                {
                    CurrentMappingHost?.EditSelected();
                    return;
                }
                GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);
                return;
            }

            // 一般分頁專屬的 A 鍵語意（平台卡片確認選取 / 分類索引標籤切換）已隨內容搬至 GeneralHostView 及其子頁，
            // 焦點落在其內部元素時交由它處理；處理了就結束，否則落入下方殼層通用分流。
            if (_currentNavTag == "General" && CurrentGeneral?.TryHandleAButton(focused) == true)
                return;

            switch (focused)
            {
                // 設定導覽項目（一般 / 進階 / 疑難排解）：選取頁面並收合側邊欄
                case NavigationViewItem navItem:
                    SettingsNav.SelectedItem = navItem;
                    SettingsNav.IsPaneOpen = false;
                    break;

                // NavigationView 內建返回按鈕：無操作（明列攔下，避免落入通用分支被 Invoke 觸發返回）
                case Button { Name: "NavigationViewBackButton" }:
                    break;

                // 漢堡選單按鈕：切換側邊欄展開 / 收合狀態
                case FrameworkElement { Name: "TogglePaneButton" }:
                    SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
                    break;

                // UI 目前隱藏的開關備忘（見 SettingsHostView.xaml 註解），復原時走下方通用分支、毋須在此加 case：
                //   - UsePhantomKeySwitch：PhantomKey 手把輸入開關，已移除（FSE 常駐）
                //   - UseGameBarLibrarySwitch：Game Bar 媒體櫃開關（On = 媒體櫃按鈕開啟 OmniConsole 設定；Off = 開啟預設平台）
                //   - EnablePassthroughSwitch：Passthrough 開關（首頁 / 媒體櫃按鈕直接導向預設平台，跳過 OmniConsole）

                // 其餘按鈕 / 開關 / 下拉 / 連結：等同點選焦點元素（AutomationPeer Invoke / Toggle / Expand）。
                // 停用（IsEnabled=false）的控制項 AutomationPeer 不觸發，故停用態開關按 A 不動作。
                default:
                    GamepadNavigationService.ActivateFocusedElement(this.XamlRoot);
                    break;
            }
        }

        /// <summary>
        /// 處理手把 'B' 鍵被按下的回呼函式。
        /// 導覽選單展開時先收合，否則觸發全域退出。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            // 導覽選單展開時，B 一律先收合選單，蓋過任何子頁自訂返回：
            // 選單是覆蓋在子頁之上的臨時層，B 要先退掉它才輪到子頁邏輯。
            if (SettingsNav.IsPaneOpen)
            {
                SettingsNav.IsPaneOpen = false;
                return;
            }

            // 手把映射編輯器頁的 B 鍵 = 儲存並返回
            if (TryHandleGamepadMappingBackKey()) return;

            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 手把 LB 肩鍵：手把映射清單頁時往上跳一頁；General 頁時切到上一個分類索引標籤。
        /// </summary>
        private void OnGamepadLBPressed()
        {
            if (IsGamepadMappingListVisible)
            {
                CurrentMappingHost?.PageUp();
                return;
            }
            if (_currentNavTag != "General") return;
            if (CurrentGeneral?.IsCurrentCategoryUser == true)
                CurrentGeneral.SwitchCategoryTabInternal("System");
        }

        /// <summary>
        /// 手把 RB 肩鍵：手把映射清單頁時往下跳一頁；General 頁時切到下一個分類索引標籤。
        /// </summary>
        private void OnGamepadRBPressed()
        {
            if (IsGamepadMappingListVisible)
            {
                CurrentMappingHost?.PageDown();
                return;
            }
            if (_currentNavTag != "General") return;
            if (CurrentGeneral?.IsCurrentCategoryUser == false)
                CurrentGeneral.SwitchCategoryTabInternal("User");
        }

        /// <summary>
        /// 手把 Y 鍵：手把映射清單頁時在搜尋方塊與清單間切換焦點；General 頁使用者索引標籤時觸發新增平台。
        /// </summary>
        private void OnGamepadYButtonPressed()
        {
            if (IsGamepadMappingListVisible)
            {
                CurrentMappingHost?.ToggleSearchFocus();
                return;
            }
            if (_currentNavTag != "General") return;
            if (CurrentGeneral?.IsCurrentCategoryUser == true && SettingsService.GetCustomPlatformConsentAccepted())
                _ = CurrentGeneral.AddPlatformInternal();
        }

        /// <summary>
        /// 手把 X 鍵：使用者索引標籤時觸發編輯目前聚焦的平台。
        /// </summary>
        private void OnGamepadXButtonPressed()
        {
            // 手把映射分頁的 X 鍵 = 刪除（清單頁刪選中項；編輯器頁刪目前 profile）
            if (TryHandleGamepadMappingDeleteKey()) return;
            if (_currentNavTag != "General") return;
            if (CurrentGeneral is not { IsCurrentCategoryUser: true } g) return;
            if (!SettingsService.GetCustomPlatformConsentAccepted()) return;

            _ = g.EditFocusedPlatformInternal(FocusManager.GetFocusedElement(this.XamlRoot));
        }

        /// <summary>
        /// 底部提示列「Menu 啟動」按鈕的滑鼠點選處理。
        /// </summary>
        private void LaunchPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            OnGamepadMenuButtonPressed();
        }

        /// <summary>底部提示列「Y 新增」按鈕的滑鼠點選處理（轉入一般分頁新增平台）。</summary>
        private void AddPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentGeneral is { } g) _ = g.AddPlatformInternal();
        }

        /// <summary>底部提示列「X 編輯」按鈕的滑鼠點選處理（轉入一般分頁編輯目前選取的使用者平台）。</summary>
        private void EditPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (CurrentGeneral is { } g) _ = g.EditSelectedPlatformInternal();
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
            if (CurrentGeneral is not { } g) return;
            if (g.IsCurrentCategoryUser && !SettingsService.GetCustomPlatformConsentAccepted()) return;

            // 若焦點在可用卡片上先確認選取（更新預設平台），回傳是否有可啟動目標
            if (!g.TrySelectFocusedCardForLaunch()) return;

            LaunchPlatformDirectlyRequested?.Invoke(this, EventArgs.Empty);
        }

        // ── 更新檢查 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 安裝前置擋：查 PhantomPaw dll 是否被任何行程鎖住、有的話彈 AppsUsingPhantomPawDialog 顯示清單。
        /// 使用者按「重試」清空所有鎖才回 true 進入安裝；按「取消」/B 鍵回 false 放棄。
        /// 無鎖直接 true。
        /// </summary>
        private async Task<bool> CheckAndPromptLockedAppsAsync()
        {
            var pids = PhantomKeyService.GetProcessesLockingPawDlls();
            if (pids.Count == 0) return true;

            var apps = UpdateCheckService.ResolveLockingApps(pids);
            var dialog = new AppsUsingPhantomPawDialog(this.XamlRoot, apps);
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        /// <summary>
        /// MainWindow 安裝續做入口：套上 MainWindow.IsInstallFlowInProgress 鎖，跑前置擋後執行安裝。
        /// 續做的下載連結來自待續狀態，不需重跑更新檢查（進階分頁的入口走 RunInstallFlowAsync）。
        /// </summary>
        internal async Task RunInstallBundleWithDialogAsync(
            string phantomLinkUrl, string mainUrl, string targetVersion,
            bool mainSkippable, bool resumeFromPhase2)
        {
            // 連點/雙觸發防呆 + 外部入口防護：整個安裝流程（含前置對話方塊等待使用者階段）期間擋第二次進入。
            // 用 MainWindow.IsInstallFlowInProgress static 同時讓 App.ShowSettingsFromRedirect /
            // ReactivateFromRedirect / PassthroughFromRedirect 三條外部入口讀取後提前 return。
            if (MainWindow.IsInstallFlowInProgress) return;
            MainWindow.IsInstallFlowInProgress = true;
            try
            {
                // 前置擋：若 PhantomPaw dll 仍被任何行程鎖住（注入中的遊戲 / App），
                // 彈對話方塊鎖住著並等使用者關閉重試；使用者取消則整個安裝流程放棄。
                if (!await CheckAndPromptLockedAppsAsync())
                {
                    (SettingsContentFrame.Content as AdvancedView)?.RestoreInstallButtonFocus();
                    return;
                }

                await RunInstallBundleWithDialogInternalAsync(
                    phantomLinkUrl, mainUrl, targetVersion, mainSkippable, resumeFromPhase2);
            }
            finally
            {
                MainWindow.IsInstallFlowInProgress = false;
            }
        }

        /// <summary>
        /// 將 InstallBundleAsync 包進 UpdateProgressDialog，由對話方塊以模態方式擋住手把 B 鍵與 Esc，
        /// 並在 MainWindow 端攔截視窗關閉。失敗後解除鎖定並顯示失敗訊息於原 InfoBar。
        /// 呼叫端負責 MainWindow.IsInstallFlowInProgress 鎖與前置擋。
        /// </summary>
        private async Task RunInstallBundleWithDialogInternalAsync(
            string phantomLinkUrl, string mainUrl, string targetVersion,
            bool mainSkippable, bool resumeFromPhase2)
        {

            var dialog = new UpdateProgressDialog(this.XamlRoot);
            // 在 try 外宣告，使失敗 catch 能 await 對話方塊關閉完成（背景設定頁 IsEnabled 還原後再聚焦）。
            Task<ContentDialogResult>? showTask = null;

            try
            {
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

                // 保險：強制刪 PhantomKey 部署的 exe + PhantomKey.dll + PhantomPaw(.dll/32.dll) 共四個檔。
                // 對抗「前置對話方塊清空後安裝前」競態視窗：萬一舊 PhantomKey 與遊戲
                // 在此期間被誤啟動又抓 dll、Kill 後 handle 通常已釋放、Delete 此時最易成功；
                // 失敗也不擋流程、後續 PhantomKeyService.Start() 仍會試 File.Copy(overwrite)。
                PhantomKeyService.DeleteDeployedFiles();

                // PhantomSigil 中繼副本一併清掉，與 Install.bat 的清理清單一致。
                PhantomSigilService.DeleteDeployedFile();

                // MSIX 更新前取消 FSE 狀態通知
                FseService.StopListening();

                // 註冊自動重啟，ForceApplicationShutdown 結束 OmniConsole 後 Windows 會自動重新啟動 OmniConsole
                UpdateCheckService.RegisterAutoRestart();

                // 設定安裝鎖定旗標供 MainWindow 讀取
                MainWindow.IsUpdateInstallInProgress = true;

                // 對話方塊期間背景設定頁輪詢由 Pull 模型自動避讓（UpdateProgressDialog 繼承 GamepadDialogBase）

                // 不 await ShowAsync，與 InstallBundleAsync 並行執行
                showTask = dialog.ShowAsync().AsTask();

                await UpdateCheckService.InstallBundleAsync(
                    phantomLinkUrl, mainUrl, targetVersion,
                    mainSkippable, resumeFromPhase2,
                    progress, status);

                // ForceApplicationShutdown / RequestRestartAsync 路徑會結束本行程，此後程式碼為回退路徑
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[SettingsHostView] Download/install failed: {ex.Message}");
                dialog.RequestClose();
                MainWindow.IsUpdateInstallInProgress = false;

                // 等對話方塊真正關閉完成，GamepadDialogBase.Closed 才會反註冊並把背景設定頁 IsEnabled 還原；
                // 在此之前背景仍為停用狀態、聚焦下載並安裝按鈕會靜默失敗，故先 await 關閉再聚焦。
                if (showTask != null)
                    await showTask;

                // 失敗訊息顯示在進階分頁的狀態文字上（該元素在 AdvancedView 內）；
                // 安裝期間若已切離進階分頁、該 Frame 內容已銷毀則無處可顯示，略過。
                // 對話方塊關閉後一併把焦點還原至下載並安裝按鈕。
                if (SettingsContentFrame.Content is AdvancedView advanced)
                {
                    advanced.ShowInstallFailed();
                    advanced.RestoreInstallButtonFocus();
                }
            }
        }

        /// <summary>
        /// 跨日且開關啟用時非同步檢查更新，檢查結果寫入快取後重新整理頂部 InfoBar。
        /// 由殼層在進設定頁時驅動，使檢查不依賴進階分頁被導覽；進階分頁的同名方法保留，
        /// 兩者靠 ShouldAutoCheck 的跨日條件去重：先跑的一方 RecordCheckDate 後另一方即略過，只有一方真正呼叫 API。
        /// </summary>
        private async Task MaybeAutoCheckForUpdatesAsync()
        {
            if (!UpdateCheckService.ShouldAutoCheck()) return;

            DebugLogger.Log($"[UpdCheck] entry=Shell tick={Environment.TickCount64}");
            var (kind, version) = await UpdateCheckService.CheckForUpdateAsync();
            UpdateCheckService.RecordCheckDate();
            DebugLogger.Log($"[UpdCheck] entry=Shell result kind={kind} version={version} tick={Environment.TickCount64}");

            if (kind != UpdateCheckService.UpdateKind.None)
                ShowSettingsUpdateInfoBar();
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
                SettingsUpdateInfoBar.Title = _resourceLoader.Loc("UpdateInfoBar_MissingPhantomLink_Title");
                SettingsUpdateInfoBar.Message = _resourceLoader.Loc("UpdateInfoBar_MissingPhantomLink_Message");
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else if (kindStr == UpdateCheckService.UpdateKind.MainAppUpdate.ToString()
                && !string.IsNullOrEmpty(cached))
            {
                SettingsUpdateInfoBar.Title = "";
                SettingsUpdateInfoBar.Message = string.Format(
                    _resourceLoader.Loc("UpdateAvailable_InfoBar_Settings"), cached);
                SettingsUpdateInfoBar.IsOpen = true;
            }
            else
            {
                SettingsUpdateInfoBar.IsOpen = false;
            }
        }
    }
}
