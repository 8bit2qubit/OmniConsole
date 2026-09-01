using OmniConsole.PhantomLink.Services;
using System;
using System.Linq;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using static OmniConsole.PhantomLink.Services.WidgetHelpers;

namespace OmniConsole.PhantomLink
{
    /// <summary>
    /// PhantomLink Game Bar widget 主面板：顯示前景程式資訊、Quick Actions、Mouse Mode / Layout / CursorSpeed 設定。
    /// 設定值透過 PhantomKeyStore 寫入 Shared.ini，動作委派給 PhantomBridge Full Trust COM Server 執行。
    /// </summary>
    public sealed partial class PhantomLinkWidget : Page
    {
        private bool _loading;

        // 內建廠商映射的機種（ROG Ally 家族）；ReloadFromStore 先給值，PhantomBridge 回報後覆寫。
        private bool _builtInMapping;

        // 上一次寫進日誌的 builtInMapping；值有變動才再寫一行。
        private bool? _loggedBuiltInMapping;

        // 內建廠商映射的機種靠它決定貓又控制項解不解禁；取不到時一律當作沒有。
        private bool _hasPro;

        // 內建廠商映射機種的開啟確認面板是否展開；展開期間分頁列與其他一般頁區塊都收起。
        private bool _confirmingBuiltInMapping;

        // 讀到 RTSS 現值之前，疊加層的控制項一律不得送出寫入命令。
        private bool _overlayStateLoaded;

        // 前景程式狀態：顯示文字 + 「自訂此 App」按鈕傳給 PhantomBridge.OpenProfileEditor 的 appId / name
        private string? _foregroundAppId;     // "process:xxx" / "aumid:xxx"；null=取不到或在黑名單
        private string _foregroundAppName = string.Empty;   // 顯示用 title（PhantomBridge 端做 URL 編碼）
        private string _foregroundFullPath = string.Empty;  // 前景 exe 完整路徑（Win32 桌面 process 才有，packaged 為空字串）；用於建 profile 時帶入 AppId.FullPath

        // 焦點從外部進入後，自動從哨兵前進到第一個真 section 的去重旗標（GettingFocus 進入時連發數次，只前進一次）
        private bool _advancePending;

        // D-pad Down 進入時：自動前進已跳一格，緊接著那個 Down 本身會再導航一格 → 吞掉避免雙跳。
        // A 鍵展開無 Down 故不受影響。哨兵真的前進一格時才 arm。
        private DateTime _swallowNextDownUntil;

        // 吞掉宿主接手後緊接的那個重複 Up。只吃重複的那一個，第一個一律照常冒泡。
        private DateTime _swallowNextUpUntil;

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public PhantomLinkWidget()
        {
            DebugLogger.Log("[Widget] ctor enter");
            try { this.InitializeComponent(); DebugLogger.Log("[Widget] InitializeComponent OK"); }
            catch (Exception ex) { DebugLogger.Log("[Widget] InitializeComponent FAIL: " + ex); throw; }

            // 不走哨兵時把它移出版面。
            if (!WidgetFocusFlow.UseSentinel) FocusSentinel.Visibility = Visibility.Collapsed;

            this.PreviewKeyDown += OnPreviewKeyDown;
            this.GettingFocus += OnGettingFocus;

            this.Loaded += (s, e) =>
            {
                DebugLogger.Log("[Widget] Loaded");
                // ApplyPageVisibility 內含 ReloadFromStore；一併把分頁列的醒目設成初始狀態。
                try { ApplyPageVisibility(); DebugLogger.Log("[Widget] Reload OK"); }
                catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }

                ApplyFileExplorerButtonLabel();

                SyncThemeFromGameBar();

                // Widget 從背景返回前景時重新讀設定（外部 OmniConsole 主程式可能改過），由 LeavingBackground 觸發
                try { Application.Current.LeavingBackground += OnLeavingBackground; }
                catch (Exception ex) { DebugLogger.Log("[Widget] Hook LeavingBackground FAIL: " + ex); }

                // Game Bar 主題變更事件：Light/Dark 切換時同步更新
                var w = App.CurrentWidget;
                if (w != null)
                {
                    try { w.RequestedThemeChanged += OnGameBarThemeChanged; }
                    catch (Exception ex) { DebugLogger.Log("[Widget] Hook ThemeChanged FAIL: " + ex); }

                    // Widget 重新顯示時重讀狀態。安裝/移除提權工作 與 匯入/移除授權 都發生在主程式
                    try { w.VisibleChanged += OnGameBarVisibleChanged; }
                    catch (Exception ex) { DebugLogger.Log("[Widget] Hook VisibleChanged FAIL: " + ex); }
                }
            };

            this.Unloaded += (s, e) =>
            {
                try { Application.Current.LeavingBackground -= OnLeavingBackground; } catch { }
                var w = App.CurrentWidget;
                if (w != null)
                {
                    try { w.RequestedThemeChanged -= OnGameBarThemeChanged; } catch { }
                    try { w.VisibleChanged -= OnGameBarVisibleChanged; } catch { }
                }
            };
        }

        // ── Game Bar 主題同步 ────────────────────────────────────────────────

        /// <summary>
        /// Page 預設不跟隨 XboxGameBarWidget.RequestedTheme，必須手動橋接
        /// 才能在 Game Bar Light/Dark 主題下正確顯示文字顏色。
        /// </summary>
        private void SyncThemeFromGameBar()
        {
            var w = App.CurrentWidget;
            if (w == null) return;
            this.RequestedTheme = w.RequestedTheme;
        }

        /// <summary>
        /// Game Bar 主題變更事件：marshal 回 UI 執行緒套用 SyncThemeFromGameBar。
        /// </summary>
        private async void OnGameBarThemeChanged(Microsoft.Gaming.XboxGameBar.XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, SyncThemeFromGameBar);
        }

        /// <summary>
        /// Widget 重新顯示時重讀設定與前景狀態，隱藏時不做事。
        /// 涵蓋「主程式改了狀態、使用者再回到 Game Bar」這條路：安裝或移除提權工作會改變
        /// 提權輸入是否可用，匯入或移除授權會改變 Pro 狀態，兩者都影響按鈕的啟用與否。
        /// </summary>
        private async void OnGameBarVisibleChanged(Microsoft.Gaming.XboxGameBar.XboxGameBarWidget sender, object args)
        {
            bool visible;
            try { visible = sender.Visible; }
            catch (Exception ex) { DebugLogger.Log("[Widget] VisibleChanged read FAIL: " + ex); return; }
            if (!visible) return;

            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                DebugLogger.Log("[Widget] VisibleChanged → reload");
                try { ReloadFromStore(); }
                catch (Exception ex) { DebugLogger.Log("[Widget] VisibleChanged reload FAIL: " + ex); }
            });
        }

        // ── 設定重新載入 ─────────────────────────────────────────────────────

        /// <summary>
        /// 從背景返回前景時重新讀取設定，反映外部行程（OmniConsole 主程式）的變更。
        /// </summary>
        private void OnLeavingBackground(object sender, Windows.ApplicationModel.LeavingBackgroundEventArgs e)
        {
            DebugLogger.Log("[Widget] LeavingBackground → reload");
            try { ReloadFromStore(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }
            // 主程式設定頁可能改過語言偏好 → 回前景時跟上，偏好已就位下次啟動即正確。
            try { App.ApplyUiLanguage(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] ApplyUiLanguage FAIL: " + ex); }
        }

        // ── 焦點進入偵測：重導至選中態按鈕 + 吞掉進入時的 D-pad Down ─────────

        /// <summary>
        /// 焦點從 Widget 外部進入（A 鍵展開 或 D-pad Down 移入，事件無法區分）→ 先重導到隱形哨兵，再排 dispatcher 前進到第一個真 section 點亮焦點框。
        /// D-pad Down 進入時 arm swallow 吞掉緊接的那個 Down 避免雙跳；A 鍵展開無 Down，swallow 窗自然過期。
        /// </summary>
        private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var oldFE = args.OldFocusedElement as DependencyObject;
            if (!WidgetFocusFlow.IsDescendant(this, oldFE))
            {
                if (WidgetFocusFlow.UseSentinel)
                {
                    // 兩條進入路徑（D-pad Down 移入、A 鍵展開）事件無法區分，一律先重導到哨兵。
                    if (!ReferenceEquals(FocusSentinel, args.NewFocusedElement))
                    {
                        try { args.TrySetNewFocusedElement(FocusSentinel); }
                        catch (Exception ex) { DebugLogger.Log("[Widget] TrySetNewFocusedElement FAIL: " + ex); }
                    }

                    // 連發去重：GettingFocus 進入時會連觸發數次，只 arm 一次自動前進。
                    if (!_advancePending)
                    {
                        _advancePending = true;
                        _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                            _advancePending = false;
                            AdvanceFromSentinel();
                        });
                    }
                }
                WidgetFocusFlow.ArmNudge();
                DebugLogger.Log("[Widget] focus re-entered → sentinel + arm advance");
            }
        }

        // ── 分頁 ────────────────────────────────────────────────────────────

        /// <summary>目前所在分頁；值對應各 section 在 XAML 標的 Tag。</summary>
        private string _currentPage = "Main";

        /// <summary>目前分頁是不是指定的那一頁；供條件顯隱的控制項一併判斷。</summary>
        private bool IsOnPage(string tag) => _currentPage == tag;

        /// <summary>
        /// 分頁按鈕取得焦點就切換過去，D-pad 左右移動即完成切頁，不必再按 A。
        /// 焦點停在分頁列上時不移動焦點，只換內容。
        /// </summary>
        private void PageTab_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { Tag: string tag }) SwitchToPage(tag, moveFocus: false);
        }

        /// <summary>分頁按鈕被點選（滑鼠路徑）。</summary>
        private void PageTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton { Tag: string tag }) SwitchToPage(tag, moveFocus: false);
        }

        /// <summary>
        /// 切換分頁：只改 section 的 Visibility。
        /// Collapsed 的 section 挑不到焦點落點，隱藏頁的 section 因此自動被跳過。
        /// </summary>
        private void SwitchToPage(string tag, bool moveFocus)
        {
            if (_currentPage == tag)
            {
                // 同一頁也要重設選中態：ToggleButton 自己的切換會把 GotFocus 點亮的那顆翻掉。
                SyncPageTabChecked();
                return;
            }

            _currentPage = tag;
            ApplyPageVisibility();

            if (!moveFocus) return;

            // 焦點移到新分頁的第一個可聚焦 section，否則會停在已隱藏的控制項上。
            foreach (var section in RootPanel.Children.OfType<FrameworkElement>())
            {
                if (section.Visibility == Visibility.Visible
                    && section.Tag is string sectionTag
                    && sectionTag == tag
                    && WidgetFocusFlow.FocusSection(section))
                {
                    break;
                }
            }
        }

        /// <summary>
        /// 把分頁列的選中態設成目前所在的分頁。
        /// 焦點落點的挑選也靠它把焦點落回目前這一頁的分頁按鈕。
        /// </summary>
        private void SyncPageTabChecked()
        {
            MainPageTab.IsChecked = IsOnPage("Main");
            OverlayPageTab.IsChecked = IsOnPage("Overlay");
        }

        /// <summary>
        /// 依目前分頁顯隱各 section，並更新分頁列的醒目。
        /// 沒有 Tag 的元素（焦點哨兵）不屬於任何分頁，一律保留。
        /// 條件顯隱的控制項（BuiltInMappingNote）由 ReloadFromStore 最後覆寫。
        /// </summary>
        private void ApplyPageVisibility()
        {
            foreach (var section in RootPanel.Children.OfType<FrameworkElement>())
            {
                if (section.Tag is not string tag) continue;
                section.Visibility = tag == _currentPage ? Visibility.Visible : Visibility.Collapsed;
            }

            SyncPageTabChecked();

            ReloadFromStore();
        }

        // ── 跨 Section D-pad 導航 ───────────────────────────────────────────

        /// <summary>
        /// 跨 section D-pad 導航：落點挑選中態的 ToggleButton 或 Slider / ComboBox，D-pad Down 進入時吞掉緊接的那個 Down 避免雙跳。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool down = e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.Down;
            bool up = e.Key == VirtualKey.GamepadDPadUp || e.Key == VirtualKey.Up;
            if (!down && !up) return;

            // D-pad Down 進入後已自動前進一格，吞掉緊接著那個 Down 避免雙跳。
            if (down && DateTime.UtcNow < _swallowNextDownUntil)
            {
                _swallowNextDownUntil = DateTime.MinValue;
                DebugLogger.Log("[Widget] swallow entry Down (post-advance)");
                e.Handled = true;
                return;
            }

            // 吞掉緊接的那個重複 Up。
            if (up && DateTime.UtcNow < _swallowNextUpUntil)
            {
                _swallowNextUpUntil = DateTime.MinValue;
                DebugLogger.Log("[Widget] swallow exit Up (duplicate)");
                e.Handled = true;
                return;
            }

            if (WidgetFocusFlow.NavigateSection(RootPanel, down)) { e.Handled = true; return; }

            // 走到這個方向的邊界。這次的 Up 不設 Handled，只 arm 下一個重複 Up 的吞鍵窗。
            if (up)
            {
                _swallowNextUpUntil = DateTime.UtcNow.AddMilliseconds(WidgetFocusFlow.SwallowUpWindowMs);
                WidgetFocusFlow.NudgeFocusPastChrome(this, Dispatcher);
            }
        }

        /// <summary>
        /// 焦點落哨兵之後前進到第一個真 section；真的前進時 arm swallow，吞掉 D-pad Down 進入時緊接著的那個 Down 避免雙跳。
        /// </summary>
        private void AdvanceFromSentinel()
        {
            if (WidgetFocusFlow.AdvanceFromSentinel(RootPanel, FocusSentinel))
                _swallowNextDownUntil = DateTime.UtcNow.AddMilliseconds(WidgetFocusFlow.SwallowWindowMs);
        }

        // ── 資料繫結與啟用狀態 ──────────────────────────────────────────────

        /// <summary>
        /// 從 Shared.ini 讀值並同步所有 UI 控制項狀態。
        /// _loading 旗標避免同步過程觸發 Click/ValueChanged 回寫造成遞迴。
        /// </summary>
        private void ReloadFromStore()
        {
            _loading = true;
            try
            {
                PhantomKeyStore.EnsureDefaultsIfMissing();
                _builtInMapping = HardwareDetection.HasBuiltInGamepadMapping();

                // SteamInGameOverlay 觸發按鈕條件可見性（夾於 [Off] [Trigger] [On] 中央）
                // 條件：FSE 模式 + DefaultPlatform=SteamBigPicture
                //   - 桌面模式不顯示
                //   - 非 SteamBigPicture 平台不顯示
                // 不可見時 StackPanel 會收合該位置，Off/On 視覺上相鄰；
                // 此時水平 XYFocus 改讓 Off/On 直接相連，避免 D-pad 走入隱藏按鈕。
                string defaultPlatform = PhantomKeyStore.GetDefaultPlatform();
                bool steamBtnVisible =
                    FseStatus.IsActive() &&
                    defaultPlatform == PhantomKeyStore.PlatformSteamBigPicture;
                TriggerSteamInGameOverlayBtn.Visibility =
                    steamBtnVisible ? Visibility.Visible : Visibility.Collapsed;
                SteamInGameOverlayOffBtn.XYFocusRight =
                    steamBtnVisible ? (DependencyObject)TriggerSteamInGameOverlayBtn : SteamInGameOverlayOnBtn;
                SteamInGameOverlayOnBtn.XYFocusLeft =
                    steamBtnVisible ? (DependencyObject)TriggerSteamInGameOverlayBtn : SteamInGameOverlayOffBtn;
                // StackPanel.Spacing 不跳過 Collapsed 子元素：三顆全顯時 Off-Trigger-On 兩段 6px 共 12px；
                // Trigger 隱藏時若仍為 6，Off-On 之間仍累計 12px（兩段 spacing 都還在），與下方 Mode
                // 區塊「三顆兩段 12px」失去一致性（Mode 是三顆全顯）。隱藏時改 3，使 Off-On 視覺間距
                // 收斂為單段 6px，與其它區塊「相鄰兩顆按鈕」的間距一致。
                SteamInGameOverlayButtonRow.Spacing = steamBtnVisible ? 6 : 3;

                // Steam In-Game Overlay（獨立於 Mouse Mode，不受 _builtInMapping 影響）
                bool overlay = PhantomKeyStore.GetSteamInGameOverlayEnabled();
                SteamInGameOverlayOnBtn.IsChecked = overlay;
                SteamInGameOverlayOffBtn.IsChecked = !overlay;

                // Mouse Mode（Off / On 兩態）
                string mode = PhantomKeyStore.GetMouseMode();
                ModeOffBtn.IsChecked = mode == PhantomKeyStore.MouseModeOff;
                ModeOnBtn.IsChecked = mode != PhantomKeyStore.MouseModeOff;

                // Layout
                SyncLayoutButtons(PhantomKeyStore.GetMouseModeLayout());

                // Cursor Speed
                int pct = PhantomKeyStore.GetCursorSpeedPercent();
                int idx = Array.IndexOf(PhantomKeyStore.ValidCursorSpeedPercents, pct);
                if (idx < 0) idx = 3; // 100%
                CursorSpeedSlider.Value = idx;
                CursorSpeedValueText.Text = $"{pct}%";

                ApplyEnabledState(mode);

                // 前景程式區塊（每次 reload 同步重抓一次）
                RefreshForegroundApp();
            }
            finally
            {
                _loading = false;
            }

            // 只在疊加層分頁讀 RTSS 現值，不看是誰觸發的 reload；停在該頁時每次 reload 都重讀。
            if (IsOnPage("Overlay")) LoadOverlayState();
        }

        /// <summary>
        /// 呼叫 PhantomBridge.GetForegroundAppInfo 取前景 title / proc / aumid / displayName / isElevated，
        /// 更新 ForegroundAppLineText 與 _foregroundAppId / _foregroundAppName，並依黑名單、內建廠商映射、
        /// elevated 狀態決定 CustomizeAppBtn 與 CustomizeAppNoteText 的可見性與啟用狀態。
        /// </summary>
        private void RefreshForegroundApp()
        {
            var resw = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();
            string title = string.Empty;
            string proc = string.Empty;
            string fullPath = string.Empty;
            string aumid = string.Empty;
            string displayName = string.Empty;
            bool isElevated = false;
            bool isBigPicture = false;
            bool canSendElevatedInput = false;
            bool canCustomizeElevated = false;
            bool hasPro = false;
            bool hasBuiltInGamepadMapping = false;
            try
            {
                using var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.GetForegroundAppInfo(out title, out proc, out fullPath, out aumid, out displayName, out isElevated, out isBigPicture, out canSendElevatedInput, out canCustomizeElevated, out hasPro, out hasBuiltInGamepadMapping);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] GetForegroundAppInfo failed: " + ex.Message);
                ForegroundAppLineText.Text = LocSafe(resw, "Widget_ForegroundApp_None", "Current: —");
                _foregroundAppId = null;
                _foregroundAppName = string.Empty;
                _foregroundFullPath = string.Empty;
                CustomizeAppBtn.IsEnabled = false;
                CustomizeAppNoteText.Visibility = Visibility.Collapsed;
                // 取不到就當作沒有 Pro：Pro 專屬選項寧可不出現，也不要出現了卻按不動
                _hasPro = false;
                // _builtInMapping 保留 ReloadFromStore 取得的值，不在此處清掉
                ApplyLayoutProVisibility(false);
                ApplyOverlayPageAvailability(false);
                ApplyEnabledState(PhantomKeyStore.GetMouseMode());
                return;
            }

            _hasPro = hasPro;
            if (_loggedBuiltInMapping != hasBuiltInGamepadMapping)
            {
                _loggedBuiltInMapping = hasBuiltInGamepadMapping;
                DebugLogger.Log($"[Widget] Bridge reported builtInMapping={hasBuiltInGamepadMapping}");
            }
            _builtInMapping = hasBuiltInGamepadMapping;
            ApplyLayoutProVisibility(hasPro);
            ApplyOverlayPageAvailability(hasPro);
            // 反灰規則吃 Pro 狀態與機型判定，取得後重跑一次讓內建廠商映射機種的控制項跟上。
            ApplyEnabledState(PhantomKeyStore.GetMouseMode());

            // 一行式顯示「目前: <displayName> (<proc>)」；displayName 為空回退 proc，與 proc 相等或 proc 為空時改走 NoDesc 格式
            string identifier = !string.IsNullOrEmpty(displayName) ? displayName : (!string.IsNullOrEmpty(proc) ? proc : "—");
            string desc = proc ?? string.Empty;

            string lineText;
            if (string.IsNullOrEmpty(identifier) || identifier == "—")
            {
                lineText = LocSafe(resw, "Widget_ForegroundApp_None", "Current: —");
            }
            else if (string.IsNullOrEmpty(desc) ||
                     string.Equals(desc, identifier, StringComparison.OrdinalIgnoreCase))
            {
                string fmt = LocSafe(resw, "Widget_ForegroundApp_LineFormat_NoDesc", "Current: {0}");
                lineText = string.Format(fmt, identifier);
            }
            else
            {
                string fmt = LocSafe(resw, "Widget_ForegroundApp_LineFormat", "Current: {0} ({1})");
                lineText = string.Format(fmt, identifier, desc);
            }
            ForegroundAppLineText.Text = lineText;

            bool isUwp = !string.IsNullOrEmpty(aumid);

            // 黑名單比對：process 名或 AUMID 內含任一 PFN 子字串即擋
            bool blocked = false;
            if (!string.IsNullOrEmpty(proc))
                blocked = ShouldBlockCustomizeProcess(proc, isBigPicture);
            if (!blocked && isUwp)
                blocked = IsBlacklistedAumid(aumid);

            // packaged 優先用 aumid: 前綴，桌面 process 用 process: 前綴
            if (blocked)
                _foregroundAppId = null;
            else if (isUwp)
                _foregroundAppId = "aumid:" + aumid;
            else if (!string.IsNullOrEmpty(proc))
                _foregroundAppId = "process:" + proc;
            else
                _foregroundAppId = null;
            _foregroundAppName = !string.IsNullOrEmpty(displayName) ? displayName : (title ?? string.Empty);
            // packaged 行程或 blocked 時 fullPath 不適用（packaged 走 aumid 主鍵；blocked 不會建 profile）
            _foregroundFullPath = (!blocked && !isUwp) ? (fullPath ?? string.Empty) : string.Empty;

            // 管理員身分的前景要不要擋，兩顆按鈕的條件並不相同，各自對應一個由 Bridge 算好的能力旗標：
            //   自訂此 App     = 建立設定檔，桌面和 FSE 可用，故只要求 Pro 加上提權工作已安裝。
            //   Steam 內嵌介面 = 當下就要送鍵，故要求提權且持 Pro 的 PhantomKey 正在執行。
            bool customizeBlocked = isElevated && !canCustomizeElevated;
            bool liveInputBlocked = isElevated && !canSendElevatedInput;

            // 內建廠商映射的機種要持有 Pro 才解禁；判斷式與 ApplyEnabledState 的 overrideAllowed 相同。
            CustomizeAppBtn.IsEnabled = _foregroundAppId != null && (!_builtInMapping || _hasPro) && !customizeBlocked;
            CustomizeAppNoteText.Visibility =
                (customizeBlocked && _foregroundAppId != null) ? Visibility.Visible : Visibility.Collapsed;

            TriggerSteamInGameOverlayBtn.IsEnabled = !liveInputBlocked;
        }

        /// <summary>
        /// 「自訂此 App 的手把映射」按鈕：收合 Game Bar 並喚起主程式手把映射編輯器
        /// （omniconsole://edit-gamepad-profile?appId=...&displayName=...&fullPath=...）。
        /// </summary>
        private async void CustomizeAppBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_foregroundAppId)) return;
            var appId = _foregroundAppId;
            var name = _foregroundAppName ?? string.Empty;
            var fullPath = _foregroundFullPath ?? string.Empty;

            DebugLogger.Log($"[Widget] CustomizeAppBtn_Click → edit-gamepad-profile appId=[{appId}]");
            await LaunchViaGameBarAsync("CustomizeApp", GameBarUris.EditGamepadProfile(appId, name, fullPath));
        }

        /// <summary>
        /// 套用 IsEnabled 規則：
        ///   - 內建廠商手把映射存在（ROG Ally 等）且未持 Pro → Mode 兩顆全部停用、顯示說明
        ///   - 同機種持有 Pro 但貓又模式尚未開啟 → 只開放「開」那一顆，按下先展開確認面板
        ///   - 同機種持有 Pro 且已開啟 → 與一般機種完全相同
        ///   - Mode=Off → Layout / CursorSpeed 停用
        /// </summary>
        private void ApplyEnabledState(string mode)
        {
            bool modeOn = mode != PhantomKeyStore.MouseModeOff;
            // 內建廠商映射的機種要持有 Pro 才解禁，且開啟必須經過確認，所以未開啟時只放行「開」那顆。
            bool overrideAllowed = !_builtInMapping || _hasPro;
            bool mouseOn = overrideAllowed && modeOn;

            ModeOffBtn.IsEnabled = overrideAllowed && (!_builtInMapping || modeOn);
            ModeOnBtn.IsEnabled = overrideAllowed;

            LayoutNavBtn.IsEnabled = mouseOn;
            LayoutClassicBtn.IsEnabled = mouseOn;
            LayoutCustomBtn.IsEnabled = mouseOn;
            CursorSpeedSlider.IsEnabled = mouseOn;

            // 一併看分頁：這兩則只屬於主分頁，切到其他分頁時不論機種都要收起。
            // 未持 Pro 講停用與如何解鎖，持有 Pro 講怎麼與廠商映射並存；兩則互斥，與主程式進階頁一致。
            bool noteOnThisPage = _builtInMapping && IsOnPage("Main") && !_confirmingBuiltInMapping;
            BuiltInMappingNote.Visibility =
                noteOnThisPage && !_hasPro ? Visibility.Visible : Visibility.Collapsed;
            BuiltInMappingProNote.Visibility =
                noteOnThisPage && _hasPro ? Visibility.Visible : Visibility.Collapsed;

            // 確認面板的顯隱由這裡收單一持有：它掛 Tag="Main"，會被 ApplyPageVisibility 一併打開。
            BuiltInMappingConfirmSection.Visibility =
                _confirmingBuiltInMapping && IsOnPage("Main") ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Quick Actions：一次性動作按鈕 ────────────────────────────────────

        /// <summary>
        /// 收合 Game Bar 並喚起目標 App。
        /// </summary>
        private System.Threading.Tasks.Task LaunchViaGameBarAsync(string tag, string uri)
        {
            var widget = App.CurrentWidget;
            return GameBarUris.SendDismissThenLaunchAsync(
                () => GameBarLauncher.DismissGameBarAsync(widget, tag),
                () => GameBarLauncher.LaunchAsync(widget, uri, tag));
        }

        /// <summary>
        /// 收合 Game Bar 後委派 PhantomBridge 執行動作。
        /// 順序不可對調：Bridge 端各方法開頭皆等待收合完成才動作，需要下層視窗已取回前景。
        /// 委派維持同步呼叫：Bridge COM server 於 client 消失時一併退出，委派不可在 widget 生命週期外執行。
        /// </summary>
        private async System.Threading.Tasks.Task RunBridgeActionAsync(string tag, Action<PhantomBridgeFactory> action)
        {
            try
            {
                await GameBarLauncher.DismissGameBarAsync(App.CurrentWidget, tag);
                PhantomBridgeHelper.InvokeWithRetry(action);
                DebugLogger.Log($"[Widget] {tag}: bridge returned");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Widget] {tag} FAIL: HResult=0x{ex.HResult:X8} type={ex.GetType().Name} msg={ex.Message}");
            }
        }

        /// <summary>收合 Game Bar 並透過 PhantomBridge 開啟 Windows 工作檢視。</summary>
        private async void TaskViewBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] TaskViewBtn_Click → PhantomBridge.SendTaskView");
            await RunBridgeActionAsync("TaskView", bridge => bridge.SendTaskView());
        }

        /// <summary>
        /// 收合 Game Bar 並透過 PhantomBridge 觸發 Steam In-Game Overlay。快捷鍵字串從 Shared.ini 讀取
        /// （PhantomKey 從 Steam VDF 解析後寫入），確保符合使用者在 Steam 自訂的快捷鍵。
        /// 僅在 DefaultPlatform=SteamBigPicture 時可見，避免對非 Steam 遊戲送 Shift+Tab 造成意外。
        /// </summary>
        private async void TriggerSteamInGameOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            string shortcut = PhantomKeyStore.GetSteamInGameOverlayShortcut();
            DebugLogger.Log($"[Widget] TriggerSteamInGameOverlayBtn_Click → PhantomBridge.TriggerSteamInGameOverlay(\"{shortcut}\")");
            await RunBridgeActionAsync("SteamOverlay", bridge => bridge.TriggerSteamInGameOverlay(shortcut));
        }

        /// <summary>
        /// 收合 Game Bar 並啟動 Xbox 媒體櫃。
        /// 全域可見：補回 Game Bar Library 原本啟動 Xbox 媒體櫃的功能（被 OmniConsole 接管後遺漏）。
        /// </summary>
        private async void XboxLibraryBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] XboxLibraryBtn_Click → Xbox Library");
            await LaunchViaGameBarAsync("XboxLibrary", GameBarUris.XboxLibrary);
        }

        /// <summary>收合 Game Bar 並透過 PhantomBridge 開啟檔案總管。</summary>
        private async void FileExplorerBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] FileExplorerBtn_Click → PhantomBridge.OpenFileExplorer");
            await RunBridgeActionAsync("FileExplorer", bridge => bridge.OpenFileExplorer());
        }

        /// <summary>把檔案總管按鈕的名稱設到提示文字與無障礙名稱上。</summary>
        private void ApplyFileExplorerButtonLabel()
        {
            var resw = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();
            string label = LocSafe(resw, "Widget_OpenFileExplorer", "File Explorer");
            ToolTipService.SetToolTip(FileExplorerBtn, label);
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(FileExplorerBtn, label);
        }

        /*
        // SettingsBtn 暫時註解保留：使用者可從 Game Bar Library 入口替代。日後研究後再啟用。
        /// <summary>收合 Game Bar 並喚起 OmniConsole 主程式設定頁。</summary>
        private async void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] SettingsBtn_Click → omniconsole://show-settings");
            await LaunchViaGameBarAsync("Settings", GameBarUris.ShowSettings);
        }
        */

        // ── UI 事件處理 ─────────────────────────────────────────────────────

        /// <summary>
        /// Steam In-Game Overlay 兩顆 ToggleButton 共用 Click：On/Off 互斥切換、寫入 Store。
        /// 獨立於 Mouse Mode：不受 _builtInMapping 或 mode=Off 影響，永遠可操作。
        /// </summary>
        private void SteamInGameOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton btn)) return;

            bool enabled = (btn.Tag as string) == "On";

            _loading = true;
            try
            {
                SteamInGameOverlayOnBtn.IsChecked = enabled;
                SteamInGameOverlayOffBtn.IsChecked = !enabled;
            }
            finally { _loading = false; }

            PhantomKeyStore.SetSteamInGameOverlayEnabled(enabled);
        }

        /// <summary>
        /// Mouse Mode 三顆 ToggleButton 共用 Click：依 Tag 決定模式、互斥勾選狀態、寫入 Store 並更新 IsEnabled。
        /// </summary>
        private void ModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton btn)) return;

            string mode = btn.Tag as string ?? PhantomKeyStore.MouseModeOn;

            // 內建廠商映射的機種要開啟時先徵詢確認，與主程式進階頁的開關是同一道關卡。
            if (mode != PhantomKeyStore.MouseModeOff && _builtInMapping
                && PhantomKeyStore.GetMouseMode() == PhantomKeyStore.MouseModeOff)
            {
                ShowBuiltInMappingConfirm();
                return;
            }

            ApplyMouseMode(mode);
        }

        /// <summary>寫入 Mouse Mode 並同步兩顆按鈕的互斥勾選狀態與反灰規則。</summary>
        private void ApplyMouseMode(string mode)
        {
            // Off / On 兩顆 ToggleButton 互斥：選中一顆時取消另一顆
            _loading = true;
            try
            {
                ModeOffBtn.IsChecked = mode == PhantomKeyStore.MouseModeOff;
                ModeOnBtn.IsChecked = mode != PhantomKeyStore.MouseModeOff;
            }
            finally { _loading = false; }

            PhantomKeyStore.SetMouseMode(mode);
            ApplyEnabledState(mode);
        }

        /// <summary>
        /// 展開內建廠商映射機種的開啟確認面板：收起一般頁其他區塊與分頁列，焦點落在「取消」。
        /// 分頁列不屬於任何分頁，切頁時永遠保留，這裡必須連它一起收起，否則往上移焦會切頁離開。
        /// </summary>
        private void ShowBuiltInMappingConfirm()
        {
            _confirmingBuiltInMapping = true;

            foreach (var section in RootPanel.Children.OfType<FrameworkElement>())
            {
                if (section.Tag is not string tag || tag != "Main") continue;
                section.Visibility = section == BuiltInMappingConfirmSection
                    ? Visibility.Visible : Visibility.Collapsed;
            }
            PageTabSection.Visibility = Visibility.Collapsed;

            BuiltInMappingCancelBtn.Focus(FocusState.Keyboard);
        }

        /// <summary>收起確認面板並還原一般頁的原有版面。</summary>
        private void HideBuiltInMappingConfirm()
        {
            _confirmingBuiltInMapping = false;
            PageTabSection.Visibility = Visibility.Visible;

            // 還原各區塊顯隱與所有依狀態計算的規則（Steam 那顆的條件顯隱、確認面板自身的收起都在裡面）。
            ApplyPageVisibility();
        }

        /// <summary>確認面板的「開啟」：寫入 Mouse Mode 後收起面板。</summary>
        private void BuiltInMappingConfirmBtn_Click(object sender, RoutedEventArgs e)
        {
            ApplyMouseMode(PhantomKeyStore.MouseModeOn);
            HideBuiltInMappingConfirm();
            ModeOnBtn.Focus(FocusState.Keyboard);
        }

        /// <summary>確認面板的「取消」：不動設定，把勾選狀態還原成關閉後收起面板。</summary>
        private void BuiltInMappingCancelBtn_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            try
            {
                ModeOffBtn.IsChecked = true;
                ModeOnBtn.IsChecked = false;
            }
            finally { _loading = false; }

            HideBuiltInMappingConfirm();
            ModeOnBtn.Focus(FocusState.Keyboard);
        }

        /// <summary>
        /// Layout 三顆 ToggleButton 共用 Click：依 Tag 決定配置、互斥勾選狀態、寫入 Store。
        /// </summary>
        private void LayoutBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton btn)) return;

            string layout = btn.Tag as string ?? PhantomKeyStore.LayoutOmniNav;
            SyncLayoutButtons(layout);
            PhantomKeyStore.SetMouseModeLayout(layout);
        }

        /// <summary>
        /// 依目前配置設定三顆 Layout 按鈕的互斥勾選狀態。
        /// _loading 期間 Click handler 直接 return，故此處的程式化賦值不會觸發寫回。
        /// </summary>
        private void SyncLayoutButtons(string layout)
        {
            _loading = true;
            try
            {
                LayoutNavBtn.IsChecked = layout == PhantomKeyStore.LayoutOmniNav;
                LayoutClassicBtn.IsChecked = layout == PhantomKeyStore.LayoutClassic;
                LayoutCustomBtn.IsChecked = layout == PhantomKeyStore.LayoutCustom;
            }
            finally { _loading = false; }
        }

        /// <summary>
        /// 疊加層分頁的顯隱與可用性。
        ///   - 沒有 Pro：整個分頁按鈕收起，使用者不會看到一個點不動的分頁。
        ///   - 有 Pro 但缺提權或沒裝 RTSS：分頁在，控制項反灰並顯示對應說明。
        /// </summary>
        private void ApplyOverlayPageAvailability(bool hasPro)
        {
            OverlayPageTab.Visibility = hasPro ? Visibility.Visible : Visibility.Collapsed;
            MainPageTab.XYFocusRight = hasPro ? (DependencyObject)OverlayPageTab : MainPageTab;

            // 沒有 Pro 卻停在疊加層分頁（例如剛移除授權）→ 退回一般分頁，否則會卡在一個連分頁鈕都看不到的頁面。
            // 不可改呼叫 ApplyPageVisibility：它末端會呼叫 ReloadFromStore，而本方法就在 ReloadFromStore 底下，會繞回自己。
            if (!hasPro && IsOnPage("Overlay"))
            {
                _currentPage = "Main";
                MainPageTab.IsChecked = true;
                OverlayPageTab.IsChecked = false;

                foreach (var section in RootPanel.Children.OfType<FrameworkElement>())
                {
                    if (section.Tag is not string tag) continue;
                    section.Visibility = tag == "Main" ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            ApplyOverlayEnabledState();
        }

        // ── 疊加層分頁 ──────────────────────────────────────────────────────

        /// <summary>提權服務是否就緒；疊加層設定的寫入需要它。</summary>
        private bool _overlayElevationReady;

        /// <summary>RTSS 是否已安裝。沒安裝時整組控制項反灰並顯示提示。</summary>
        private bool _overlayRtssInstalled;

        /// <summary>RTSS 是否正在執行；與「沒安裝」分開判定，提示不同。</summary>
        private bool _overlayRtssRunning;

        /// <summary>幀率限制的離散檔位；0 為不限。</summary>
        private static readonly int[] _overlayFpsLimits = { 0, 30, 40, 60, 90, 120, 144, 165, 240 };

        /// <summary>連續調整的延後送出；滑鼠放開時另有立即送出的路徑。</summary>
        private DispatcherTimer? _overlayApplyTimer;

        /// <summary>等待送出的命令；同一項連續調整只會保留最後一次的值。</summary>
        private string? _pendingOverlayCommand;

        /// <summary>依提權與 RTSS 狀態決定控制項可不可用，並顯示對應說明。</summary>
        private void ApplyOverlayEnabledState()
        {
            bool usable = _overlayElevationReady && _overlayRtssInstalled && _overlayRtssRunning;

            OverlayOsdOffBtn.IsEnabled = usable;
            OverlayOsdOnBtn.IsEnabled = usable;
            OverlayStatOffBtn.IsEnabled = usable;
            OverlayStatOnBtn.IsEnabled = usable;
            OverlayShadowOffBtn.IsEnabled = usable;
            OverlayShadowOnBtn.IsEnabled = usable;
            OverlayZoomSlider.IsEnabled = usable;
            OverlayFpsLimitSlider.IsEnabled = usable;

            // 三則說明依先決條件的順序擇一顯示：沒提權 → 沒裝 → 裝了但沒在跑。
            OverlayElevationNote.Visibility =
                _overlayElevationReady ? Visibility.Collapsed : Visibility.Visible;
            OverlayRtssNote.Visibility =
                (_overlayElevationReady && !_overlayRtssInstalled) ? Visibility.Visible : Visibility.Collapsed;
            OverlayRtssStoppedNote.Visibility =
                (_overlayElevationReady && _overlayRtssInstalled && !_overlayRtssRunning)
                    ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 讀 RTSS 現值並同步控制項。
        /// 只在切到疊加層分頁時呼叫，不進 ReloadFromStore。不快取，每次都讀 RTSS 現值。
        /// </summary>
        private void LoadOverlayState()
        {
            bool installed = false;
            bool running = false;
            bool elevationReady = false;
            int osd = -1, stat = -1, shadow = -1, zoom = -1, fpsLimit = -1;

            try
            {
                using var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.GetOverlayState(out installed, out running, out elevationReady, out osd, out stat, out shadow, out zoom, out fpsLimit);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] GetOverlayState failed: " + ex.Message);
            }

            _overlayRtssInstalled = installed;
            _overlayRtssRunning = running;
            _overlayElevationReady = elevationReady;
            DebugLogger.Log($"[Widget] overlay state installed={installed} running={running} elevation={elevationReady} osd={osd} zoom={zoom} fps={fpsLimit}");

            _loading = true;
            try
            {
                // 控制項一律留在畫面上，不可用時反灰；只有「RTSS 裝著卻讀不到某一項」才收起那一項。
                bool showAll = !installed;
                OverlayOsdSection.Visibility = SectionVisibility(showAll || osd >= 0);
                OverlayStatSection.Visibility = SectionVisibility(showAll || stat >= 0);
                OverlayShadowSection.Visibility = SectionVisibility(showAll || shadow >= 0);
                OverlayZoomSection.Visibility = SectionVisibility(showAll || zoom >= 0);
                OverlayFpsLimitSection.Visibility = SectionVisibility(showAll || fpsLimit >= 0);

                OverlayOsdOnBtn.IsChecked = osd == 1;
                OverlayOsdOffBtn.IsChecked = osd == 0;
                OverlayStatOnBtn.IsChecked = stat == 1;
                OverlayStatOffBtn.IsChecked = stat == 0;
                OverlayShadowOnBtn.IsChecked = shadow == 1;
                OverlayShadowOffBtn.IsChecked = shadow == 0;

                if (zoom >= 0)
                {
                    OverlayZoomSlider.Value = Math.Clamp(zoom, 1, 8);
                    OverlayZoomValueText.Text = $"{(int)OverlayZoomSlider.Value}x";
                }

                if (fpsLimit >= 0)
                {
                    int idx = Array.IndexOf(_overlayFpsLimits, fpsLimit);
                    // 現值不在檔位上時顯示最接近的一格，不回寫。
                    if (idx < 0) idx = NearestFpsLimitIndex(fpsLimit);
                    OverlayFpsLimitSlider.Value = idx;
                    OverlayFpsLimitValueText.Text = FormatFpsLimit(_overlayFpsLimits[idx]);
                }
            }
            finally
            {
                _loading = false;
            }

            // 控制項已與 RTSS 現值同步，從這一刻起使用者的操作才有東西可寫回去。
            _overlayStateLoaded = true;

            ApplyOverlayEnabledState();
        }

        /// <summary>目前在疊加層分頁，且該項目可用時才顯示。</summary>
        private Visibility SectionVisibility(bool available)
            => (available && IsOnPage("Overlay")) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>找出最接近的檔位；RTSS 的現值不一定落在檔位上。</summary>
        private static int NearestFpsLimitIndex(int value)
        {
            int best = 0;
            for (int i = 1; i < _overlayFpsLimits.Length; i++)
            {
                if (Math.Abs(_overlayFpsLimits[i] - value) < Math.Abs(_overlayFpsLimits[best] - value))
                    best = i;
            }
            return best;
        }

        /// <summary>0 顯示成「不限」，其餘顯示數字。</summary>
        private string FormatFpsLimit(int value)
        {
            if (value != 0) return value.ToString();
            var resw = Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView();
            return LocSafe(resw, "Widget_Overlay_FpsLimit_Unlimited", "Off");
        }

        /// <summary>
        /// 排定送出；同一項的後一次呼叫會覆蓋前一次待送的命令。
        /// </summary>
        private void QueueOverlayApply(string command)
        {
            _pendingOverlayCommand = command;

            _overlayApplyTimer ??= CreateOverlayApplyTimer();
            _overlayApplyTimer.Stop();
            _overlayApplyTimer.Start();
        }

        /// <summary>建立延後送出的計時器；停手後才真的送。</summary>
        private DispatcherTimer CreateOverlayApplyTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                _ = FlushOverlayApplyAsync();
            };
            return timer;
        }

        /// <summary>
        /// 真正把命令送給 Bridge，開關類直接呼叫這裡。走背景執行緒，不卡住畫面。
        /// </summary>
        private async System.Threading.Tasks.Task FlushOverlayApplyAsync()
        {
            string? command = _pendingOverlayCommand;
            _pendingOverlayCommand = null;
            if (string.IsNullOrEmpty(command)) return;

            int result = -1;
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                    PhantomBridgeHelper.InvokeWithRetry(bridge => result = bridge.ApplyOverlaySettings(command)));
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Widget] ApplyOverlaySettings FAIL: {ex.Message}");
                return;
            }

            DebugLogger.Log($"[Widget] ApplyOverlaySettings result={result} command=[{command}]");

            // 套用失敗時把控制項退回 RTSS 的實際狀態，不留一個與畫面不符的值。
            if (result != 0) LoadOverlayState();
        }

        /// <summary>On-Screen Display support 的 Off / On 兩顆共用 Click。</summary>
        private void OverlayOsdBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || !_overlayStateLoaded) return;
            if (!(sender is ToggleButton btn)) return;

            bool on = (btn.Tag as string) == "On";
            SyncOverlayToggle(OverlayOsdOffBtn, OverlayOsdOnBtn, on);

            _pendingOverlayCommand = $"rtss-apply --osd {(on ? 1 : 0)}";
            _ = FlushOverlayApplyAsync();
        }

        /// <summary>Show own statistics 的 Off / On 兩顆共用 Click。</summary>
        private void OverlayStatBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || !_overlayStateLoaded) return;
            if (!(sender is ToggleButton btn)) return;

            bool on = (btn.Tag as string) == "On";
            SyncOverlayToggle(OverlayStatOffBtn, OverlayStatOnBtn, on);

            _pendingOverlayCommand = $"rtss-apply --stat {(on ? 1 : 0)}";
            _ = FlushOverlayApplyAsync();
        }

        /// <summary>On-Screen Display shadow 的 Off / On 兩顆共用 Click。</summary>
        private void OverlayShadowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading || !_overlayStateLoaded) return;
            if (!(sender is ToggleButton btn)) return;

            bool on = (btn.Tag as string) == "On";
            SyncOverlayToggle(OverlayShadowOffBtn, OverlayShadowOnBtn, on);

            _pendingOverlayCommand = $"rtss-apply --shadow {(on ? 1 : 0)}";
            _ = FlushOverlayApplyAsync();
        }

        /// <summary>Off / On 兩顆 ToggleButton 的互斥勾選狀態。</summary>
        private void SyncOverlayToggle(ToggleButton offBtn, ToggleButton onBtn, bool on)
        {
            _loading = true;
            try
            {
                onBtn.IsChecked = on;
                offBtn.IsChecked = !on;
            }
            finally { _loading = false; }
        }

        /// <summary>On-Screen Display zoom：值域 1..8，直接對應 RTSS 的屬性值。</summary>
        private void OverlayZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !_overlayStateLoaded) return;

            int zoom = Math.Clamp((int)Math.Round(e.NewValue), 1, 8);
            OverlayZoomValueText.Text = $"{zoom}x";
            QueueOverlayApply($"rtss-apply --zoom {zoom}");
        }

        /// <summary>Framerate limit：Slider 走檔位索引，送出的是檔位對應的實際幀率。</summary>
        private void OverlayFpsLimitSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading || !_overlayStateLoaded) return;

            int idx = Math.Clamp((int)Math.Round(e.NewValue), 0, _overlayFpsLimits.Length - 1);
            int limit = _overlayFpsLimits[idx];
            OverlayFpsLimitValueText.Text = FormatFpsLimit(limit);
            QueueOverlayApply($"rtss-apply --fps-limit {limit}");
        }

        /// <summary>
        /// 依是否持有 Pro 決定「自訂」按鈕的顯隱，並把撞到它的焦點連結回來。
        /// 未持有 Pro 但設定值是「自訂」時，改顯示實際生效的 OmniNav 為勾選，
        /// 但不寫回 Store：那是使用者的資料，重新匯入授權後能原樣復活。
        /// </summary>
        private void ApplyLayoutProVisibility(bool hasPro)
        {
            LayoutCustomBtn.Visibility = hasPro ? Visibility.Visible : Visibility.Collapsed;
            // 隱藏中的元素不可被指向，故「自訂」收起時「經典」的右鍵改成撞自己這面牆
            LayoutClassicBtn.XYFocusRight = hasPro ? (DependencyObject)LayoutCustomBtn : LayoutClassicBtn;

            if (!hasPro && PhantomKeyStore.GetMouseModeLayout() == PhantomKeyStore.LayoutCustom)
                SyncLayoutButtons(PhantomKeyStore.LayoutOmniNav);
        }

        /// <summary>
        /// Slider 值（0..7）映射為百分比（25/50/75/100/125/150/175/200），更新顯示並寫入 Store。
        /// </summary>
        private void CursorSpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_loading) return;

            int idx = (int)Math.Round(e.NewValue);
            if (idx < 0) idx = 0;
            if (idx >= PhantomKeyStore.ValidCursorSpeedPercents.Length)
                idx = PhantomKeyStore.ValidCursorSpeedPercents.Length - 1;

            int pct = PhantomKeyStore.ValidCursorSpeedPercents[idx];
            CursorSpeedValueText.Text = $"{pct}%";
            PhantomKeyStore.SetCursorSpeedPercent(pct);
        }
    }
}
