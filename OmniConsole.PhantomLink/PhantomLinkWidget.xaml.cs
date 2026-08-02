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
        private bool _builtInMapping;

        // 前景程式狀態：顯示文字 + 「自訂此 App」按鈕傳給 PhantomBridge.OpenProfileEditor 的 appId / name
        private string? _foregroundAppId;     // "process:xxx" / "aumid:xxx"；null=取不到或在黑名單
        private string _foregroundAppName = string.Empty;   // 顯示用 title（PhantomBridge 端做 URL 編碼）
        private string _foregroundFullPath = string.Empty;  // 前景 exe 完整路徑（Win32 桌面 process 才有，packaged 為空字串）；用於建 profile 時帶入 AppId.FullPath

        // 焦點從外部進入後，自動從哨兵前進到第一個真 section 的去重旗標（GettingFocus 進入時連發數次，只前進一次）
        private bool _advancePending;

        // D-pad Down 進入時：自動前進已跳一格，緊接著那個 Down 本身會再導航一格 → 吞掉避免雙跳。
        // A 鍵展開無 Down 故不受影響。AdvanceFromSentinel 真的前進時才 arm。
        private DateTime _swallowNextDownUntil;

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public PhantomLinkWidget()
        {
            DebugLogger.Log("[Widget] ctor enter");
            try { this.InitializeComponent(); DebugLogger.Log("[Widget] InitializeComponent OK"); }
            catch (Exception ex) { DebugLogger.Log("[Widget] InitializeComponent FAIL: " + ex); throw; }

            this.PreviewKeyDown += OnPreviewKeyDown;
            this.GettingFocus += OnGettingFocus;

            this.Loaded += (s, e) =>
            {
                DebugLogger.Log("[Widget] Loaded");
                try { ReloadFromStore(); DebugLogger.Log("[Widget] Reload OK"); }
                catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }

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
        /// 焦點從 Widget 外部進入（A 鍵展開 或 D-pad Down 移入，事件無法區分）→ 先重導到隱形哨兵（吸收
        /// Game Bar「首次進入不顯框」的 quirk），再 AdvanceFromSentinel 自動前進到第一個真 section 點亮焦點框。
        /// D-pad Down 進入時 arm swallow 吞掉緊接的那個 Down 避免雙跳；A 鍵展開無 Down，swallow 窗自然過期。
        /// </summary>
        private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var oldFE = args.OldFocusedElement as DependencyObject;
            if (!IsDescendant(oldFE))
            {
                // 焦點從外部進入 Widget（D-pad Down 移入 或 A 鍵展開，兩者事件無法區分：皆 inputDevice=
                // Keyboard / direction=None / new=哨兵）。先重導到 0 高度透明哨兵（吸收 Game Bar「首次進入不
                // 顯框」的 quirk），再排 dispatcher 自動前進到第一個真 section，走 FocusSection
                // 的 Focus(FocusState.Keyboard) 真轉移點亮焦點框。統一兩條路徑：都落哨兵→自動前進→真按鈕有框。
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
                DebugLogger.Log("[Widget] focus re-entered → sentinel + arm advance");
            }
        }

        /// <summary>判斷節點是否為本 Page 視覺樹內的子元素。</summary>
        private bool IsDescendant(DependencyObject node)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, this)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ── 跨 Section D-pad 導航 ───────────────────────────────────────────

        /// <summary>
        /// 跨 section D-pad 導航：落點挑選中態的 ToggleButton 或 Slider / ComboBox，
        /// 為通用規則，未來新增 section 不需改程式。初始焦點由 OnGettingFocus 重導哨兵 + AdvanceFromSentinel
        /// 自動前進到第一個真 section；D-pad Down 進入時吞掉緊接的那個 Down 避免雙跳。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool down = e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.Down;
            bool up = e.Key == VirtualKey.GamepadDPadUp || e.Key == VirtualKey.Up;
            if (!down && !up) return;

            // D-pad Down 進入後 AdvanceFromSentinel 已自動前進一格，吞掉緊接著那個 Down 避免雙跳。
            if (down && DateTime.UtcNow < _swallowNextDownUntil)
            {
                _swallowNextDownUntil = DateTime.MinValue;
                DebugLogger.Log("[Widget] swallow entry Down (post-advance)");
                e.Handled = true;
                return;
            }

            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null) return;

            var currentSection = FindSection(focused);
            if (currentSection == null) return;

            var sections = RootPanel.Children.OfType<FrameworkElement>().ToList();
            int idx = sections.IndexOf(currentSection);
            int step = down ? 1 : -1;
            for (int i = idx + step; i >= 0 && i < sections.Count; i += step)
            {
                if (FocusSection(sections[i])) { e.Handled = true; return; }
            }
        }

        /// <summary>
        /// 焦點從外部進入落哨兵後，自動前進到第一個可聚焦的真 section（跳過 sections[0] 哨兵）。
        /// 走 FocusSection 的 Focus(FocusState.Keyboard) 真轉移點亮焦點框，統一 D-pad Down 與 A 鍵展開兩條
        /// 路徑都直接落第一個真按鈕有框。僅在焦點仍停在哨兵時前進（使用者若已手動導航走則不干預）。
        /// </summary>
        private void AdvanceFromSentinel()
        {
            // 焦點已不在哨兵（使用者已自行導航）→ 不干預
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !ReferenceEquals(FindSection(focused), FocusSentinel))
                return;

            var sections = RootPanel.Children.OfType<FrameworkElement>().ToList();
            for (int i = 1; i < sections.Count; i++) // 跳過 index 0（哨兵）
            {
                if (FocusSection(sections[i]))
                {
                    // 已自動前進一格。若是 D-pad Down 進入，緊接著那個 Down 還會再導航一格 → arm swallow 吞掉
                    // 避免雙跳。A 鍵展開無 Down，swallow 視窗自然過期不影響。
                    _swallowNextDownUntil = DateTime.UtcNow.AddMilliseconds(200);
                    return;
                }
            }
        }

        /// <summary>
        /// 走到 RootPanel 的直屬子元素，作為「section」代表。
        /// </summary>
        private FrameworkElement? FindSection(DependencyObject? node)
        {
            while (node != null)
            {
                var parent = VisualTreeHelper.GetParent(node);
                if (parent == RootPanel) return node as FrameworkElement;
                node = parent;
            }
            return null;
        }

        /// <summary>
        /// Section 內挑焦點目標：checked ToggleButton > 第一顆 ToggleButton > Slider > ComboBox > Button。
        /// Button 回退 供 Quick Actions 等只含一次性動作按鈕的 section 使用（無 checked 狀態）。
        /// </summary>
        private Control? PickFocusTarget(FrameworkElement? section)
        {
            if (section == null) return null;
            var toggles = FindDescendants<ToggleButton>(section).Where(t => t.IsEnabled).ToList();
            if (toggles.Count > 0)
                return toggles.FirstOrDefault(t => t.IsChecked == true) ?? toggles[0];
            var slider = FindDescendants<Slider>(section).FirstOrDefault(s => s.IsEnabled);
            if (slider != null) return slider;
            var combo = FindDescendants<ComboBox>(section).FirstOrDefault(c => c.IsEnabled);
            if (combo != null) return combo;
            return FindDescendants<Button>(section).FirstOrDefault(b => b.IsEnabled);
        }

        /// <summary>聚焦 section 的落點控制項；供跨 section 導航呼叫。</summary>
        private bool FocusSection(FrameworkElement section)
        {
            var target = PickFocusTarget(section);
            return target != null && target.Focus(FocusState.Keyboard);
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
            try
            {
                using var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.GetForegroundAppInfo(out title, out proc, out fullPath, out aumid, out displayName, out isElevated, out isBigPicture, out canSendElevatedInput, out canCustomizeElevated, out hasPro);
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
                ApplyLayoutProVisibility(false);
                return;
            }

            ApplyLayoutProVisibility(hasPro);

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

            CustomizeAppBtn.IsEnabled = _foregroundAppId != null && !_builtInMapping && !customizeBlocked;
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
        ///   - 內建廠商手把映射存在（ROG Ally 等）→ Mode 三顆全部停用、顯示說明
        ///   - Mode=Off → Layout / CursorSpeed 停用
        /// </summary>
        private void ApplyEnabledState(string mode)
        {
            bool mouseOn = !_builtInMapping && mode != PhantomKeyStore.MouseModeOff;

            ModeOffBtn.IsEnabled = !_builtInMapping;
            ModeOnBtn.IsEnabled = !_builtInMapping;

            LayoutNavBtn.IsEnabled = mouseOn;
            LayoutClassicBtn.IsEnabled = mouseOn;
            LayoutCustomBtn.IsEnabled = mouseOn;
            CursorSpeedSlider.IsEnabled = mouseOn;

            BuiltInMappingNote.Visibility = _builtInMapping ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Quick Actions：一次性動作按鈕 ────────────────────────────────────

        /// <summary>
        /// 收合 Game Bar 後喚起目標 App。
        /// </summary>
        private async System.Threading.Tasks.Task LaunchViaGameBarAsync(string tag, string uri)
        {
            var widget = App.CurrentWidget;
            await GameBarLauncher.DismissGameBarAsync(widget, tag);
            await GameBarLauncher.LaunchAsync(widget, uri, tag);
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
