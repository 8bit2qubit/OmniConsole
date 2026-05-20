using OmniConsole.PhantomLink.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

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
        private string _foregroundAppId;     // "process:xxx" / "aumid:xxx"；null=取不到或在黑名單
        private string _foregroundAppName;   // 顯示用 title（PhantomBridge 端做 URL 編碼）

        // 焦點剛從外部進入 Widget → 吞掉緊接著的一顆 D-pad Down，避免雙跳（OnGettingFocus 把焦點重導到選中態按鈕 + OnPreviewKeyDown 又推進一 section）
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
                }
            };

            this.Unloaded += (s, e) =>
            {
                try { Application.Current.LeavingBackground -= OnLeavingBackground; } catch { }
                var w = App.CurrentWidget;
                if (w != null) { try { w.RequestedThemeChanged -= OnGameBarThemeChanged; } catch { } }
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

        // ── 設定重新載入 ─────────────────────────────────────────────────────

        /// <summary>
        /// 從背景返回前景時重新讀取設定，反映外部行程（OmniConsole 主程式）的變更。
        /// </summary>
        private void OnLeavingBackground(object sender, Windows.ApplicationModel.LeavingBackgroundEventArgs e)
        {
            DebugLogger.Log("[Widget] LeavingBackground → reload");
            try { ReloadFromStore(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] Reload FAIL: " + ex); }
        }

        // ── 焦點進入偵測：重導至選中態按鈕 + 吞掉進入時的 D-pad Down ─────────

        /// <summary>
        /// 焦點從 Widget 外部進入（A 或 D-pad）→ 重導到第一 section 的選中態按鈕（checked），
        /// 並開啟 150ms 吞 Down 窗。A 鍵進入無 Down 事件，窗自然過期；D-pad Down 進入時，
        /// 同一顆 Down 會被窗吃掉，避免「系統送焦點進按鈕 + OnPreviewKeyDown 又推進下一 section」雙跳。
        /// </summary>
        private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var oldFE = args.OldFocusedElement as DependencyObject;
            if (!IsDescendant(oldFE))
            {
                // 已知 Game Bar 冷啟動後首次 D-pad Down 進 Widget 的行為是 Game Bar 自身的 quirk：
                // 焦點會落在第一組第一顆按鈕但無 focus ring，再按一次 Down 才正確顯示第二組的
                // 選中態。無法從 Widget 這邊修正（Game Bar 側的焦點渲染問題），Widget 已 loaded
                // 後再進入就正常。
                _swallowNextDownUntil = DateTime.UtcNow.AddMilliseconds(150);
                var target = PickFocusTarget(QuickActionsSection);
                if (target != null && !ReferenceEquals(target, args.NewFocusedElement))
                {
                    try { args.TrySetNewFocusedElement(target); }
                    catch (Exception ex) { DebugLogger.Log("[Widget] TrySetNewFocusedElement FAIL: " + ex); }
                }
                DebugLogger.Log("[Widget] focus re-entered → redirect to checked + arm swallow-Down");
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
        /// 跨 section D-pad 導航：落點挑選中態的 ToggleButton 或 Slider / ComboBox —
        /// 通用規則，未來新增 section 不需改程式。初始焦點由 OnGettingFocus 重導到選中態按鈕，
        /// 外部進入時緊接著的 D-pad Down 由 _swallowNextDownUntil 吃掉，避免雙跳。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool down = e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.Down;
            bool up = e.Key == VirtualKey.GamepadDPadUp || e.Key == VirtualKey.Up;
            if (!down && !up) return;

            if (down && DateTime.UtcNow < _swallowNextDownUntil)
            {
                _swallowNextDownUntil = DateTime.MinValue;
                DebugLogger.Log("[Widget] swallow entry Down");
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
        /// 走到 RootPanel 的直屬子元素，作為「section」代表。
        /// </summary>
        private FrameworkElement FindSection(DependencyObject node)
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
        private Control PickFocusTarget(FrameworkElement section)
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

        /// <summary>
        /// 遞迴走訪視覺樹，列舉所有指定型別的子元素。
        /// </summary>
        private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in FindDescendants<T>(child)) yield return d;
            }
        }

        // ── 資料綁定與啟用狀態 ──────────────────────────────────────────────

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

                // Mouse Mode
                string mode = PhantomKeyStore.GetMouseMode();
                ModeOffBtn.IsChecked = mode == PhantomKeyStore.MouseModeOff;
                ModeAutoBtn.IsChecked = mode == PhantomKeyStore.MouseModeAuto;
                ModeForceOnBtn.IsChecked = mode == PhantomKeyStore.MouseModeForceOn;

                // Layout
                string layout = PhantomKeyStore.GetMouseModeLayout();
                LayoutNavBtn.IsChecked = layout == PhantomKeyStore.LayoutOmniNav;
                LayoutClassicBtn.IsChecked = layout == PhantomKeyStore.LayoutClassic;

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
            string aumid = string.Empty;
            string displayName = string.Empty;
            bool isElevated = false;
            try
            {
                var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.GetForegroundAppInfo(out title, out proc, out aumid, out displayName, out isElevated);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] GetForegroundAppInfo failed: " + ex.Message);
                ForegroundAppLineText.Text = LocSafe(resw, "Widget_ForegroundApp_None", "Current: —");
                _foregroundAppId = null;
                _foregroundAppName = string.Empty;
                CustomizeAppBtn.IsEnabled = false;
                CustomizeAppNoteText.Visibility = Visibility.Collapsed;
                return;
            }

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

            // 黑名單比對：process 名單命中或 AUMID 內含任一 PFN 子字串即擋
            bool blocked = false;
            if (!string.IsNullOrEmpty(proc))
                blocked = IsBlacklistedProcess(proc);
            if (!blocked && isUwp)
            {
                blocked = aumid.IndexOf("Microsoft.GamingApp", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("B9ECED6F.ArmouryCrateSE", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("windows.immersivecontrolpanel", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("Microsoft.WindowsStore", StringComparison.OrdinalIgnoreCase) >= 0
                       || aumid.IndexOf("b5fbce6b-2d7d-4da0-b419-4beb30e2b808", StringComparison.OrdinalIgnoreCase) >= 0;
            }

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

            CustomizeAppBtn.IsEnabled = _foregroundAppId != null && !_builtInMapping && !isElevated;
            CustomizeAppNoteText.Visibility =
                (isElevated && _foregroundAppId != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>resw 安全查詢：不存在或擲例外時回退到 `fallback` 參數值。</summary>
        private static string LocSafe(Windows.ApplicationModel.Resources.ResourceLoader resw, string key, string fallback)
        {
            try
            {
                var s = resw.GetString(key);
                return string.IsNullOrEmpty(s) ? fallback : s;
            }
            catch { return fallback; }
        }

        /// <summary>
        /// 行程名稱比對（大小寫不敏感）是否為 IsBlacklisted 條目 (a)/(b) 涵蓋的程式。
        /// 跟 OmniConsole/Services/GamepadProfileStore 同一份名單。
        /// </summary>
        private static bool IsBlacklistedProcess(string proc)
        {
            string[] names =
            {
                // (a) 自家 / 內建手把導覽
                "OmniConsole", "Playnite.FullscreenApp", "steamwebhelper",
                // (b) Mouse Mode Auto 白名單
                "msedge", "chrome", "firefox", "opera", "brave",
                "EpicGamesLauncher", "Discord", "explorer",
            };
            foreach (var n in names)
                if (string.Equals(proc, n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 「自訂此 App 的手把映射」按鈕：透過 PhantomBridge.OpenProfileEditor 喚起主程式
        /// （Win+G 收 Game Bar → omniconsole://edit-gamepad-profile?appId=...&displayName=...）。
        /// </summary>
        private void CustomizeAppBtn_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_foregroundAppId)) return;
            DebugLogger.Log("[Widget] CustomizeAppBtn_Click → PhantomBridge.OpenProfileEditor: " + _foregroundAppId);
            try
            {
                var bridge = PhantomBridgeHelper.CreateFactory();
                bridge.OpenProfileEditor(_foregroundAppId, _foregroundAppName ?? string.Empty);
            }
            catch (Exception ex)
            {
                DebugLogger.Log("[Widget] OpenProfileEditor failed: " + ex.Message);
            }
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
            ModeAutoBtn.IsEnabled = !_builtInMapping;
            ModeForceOnBtn.IsEnabled = !_builtInMapping;

            LayoutNavBtn.IsEnabled = mouseOn;
            LayoutClassicBtn.IsEnabled = mouseOn;
            CursorSpeedSlider.IsEnabled = mouseOn;

            BuiltInMappingNote.Visibility = _builtInMapping ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Quick Actions：一次性動作按鈕 ────────────────────────────────────
        //
        // 委派給 PhantomBridge Full Trust COM Server 執行；Widget 受 UWP AppContainer 限制，
        // SendInput 會被靜默封鎖，LaunchUriAsync 跨套件 protocol 不可靠。
        // Bridge 為獨立的 full trust 桌面行程，Windows 於 CoCreateInstance 時按需啟動。

        /// <summary>透過 PhantomBridge 送 Win+Tab 開啟 Windows 工作檢視。</summary>
        private void TaskViewBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] TaskViewBtn_Click → PhantomBridge.SendTaskView");
            try { PhantomBridgeHelper.CreateFactory().SendTaskView(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] TaskView FAIL: " + ex); }
        }

        /// <summary>
        /// 透過 PhantomBridge 觸發 Steam In-Game Overlay。快捷鍵字串從 Shared.ini 讀取
        /// （PhantomKey 從 Steam VDF 解析後寫入），確保符合使用者在 Steam 自訂的快捷鍵。
        /// 僅在 DefaultPlatform=SteamBigPicture 時可見，避免對非 Steam 遊戲送 Shift+Tab 造成意外。
        /// </summary>
        private void TriggerSteamInGameOverlayBtn_Click(object sender, RoutedEventArgs e)
        {
            string shortcut = PhantomKeyStore.GetSteamInGameOverlayShortcut();
            DebugLogger.Log($"[Widget] TriggerSteamInGameOverlayBtn_Click → PhantomBridge.TriggerSteamInGameOverlay(\"{shortcut}\")");
            try { PhantomBridgeHelper.CreateFactory().TriggerSteamInGameOverlay(shortcut); }
            catch (Exception ex) { DebugLogger.Log("[Widget] TriggerSteamInGameOverlay FAIL: " + ex); }
        }

        /// <summary>
        /// 透過 PhantomBridge 啟動 xbox://library（Xbox 媒體櫃）。
        /// 全域可見：補回 Game Bar Library 原本啟動 xbox://library 的功能（被 OmniConsole 接管後遺漏）。
        /// </summary>
        private void XboxLibraryBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] XboxLibraryBtn_Click → PhantomBridge.OpenXboxLibrary");
            try { PhantomBridgeHelper.CreateFactory().OpenXboxLibrary(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] OpenXboxLibrary FAIL: " + ex); }
        }

        /*
        // SettingsBtn 暫時註解保留 —— 使用者可從 Game Bar Library 入口替代。日後研究後再啟用。
        /// <summary>透過 PhantomBridge 喚起 OmniConsole 主程式設定頁。</summary>
        private void SettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            DebugLogger.Log("[Widget] SettingsBtn_Click → PhantomBridge.OpenSettings");
            try { PhantomBridgeHelper.CreateFactory().OpenSettings(); }
            catch (Exception ex) { DebugLogger.Log("[Widget] Settings FAIL: " + ex); }
        }
        */

        // ── UI 事件處理 ─────────────────────────────────────────────────────

        /// <summary>
        /// Steam In-Game Overlay 兩顆 ToggleButton 共用 Click：On/Off 互斥切換、寫入 Store。
        /// 獨立於 Mouse Mode —— 不受 _builtInMapping 或 mode=Off 影響，永遠可操作。
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

            string mode = btn.Tag as string ?? PhantomKeyStore.MouseModeAuto;

            // 三顆 ToggleButton 互斥：選中一顆時取消其餘兩顆
            _loading = true;
            try
            {
                ModeOffBtn.IsChecked = mode == PhantomKeyStore.MouseModeOff;
                ModeAutoBtn.IsChecked = mode == PhantomKeyStore.MouseModeAuto;
                ModeForceOnBtn.IsChecked = mode == PhantomKeyStore.MouseModeForceOn;
            }
            finally { _loading = false; }

            PhantomKeyStore.SetMouseMode(mode);
            ApplyEnabledState(mode);
        }

        /// <summary>
        /// Layout 兩顆 ToggleButton 共用 Click：依 Tag 決定配置、互斥勾選狀態、寫入 Store。
        /// </summary>
        private void LayoutBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (!(sender is ToggleButton btn)) return;

            string layout = btn.Tag as string ?? PhantomKeyStore.LayoutOmniNav;

            // 兩顆 ToggleButton 互斥
            _loading = true;
            try
            {
                LayoutNavBtn.IsChecked = layout == PhantomKeyStore.LayoutOmniNav;
                LayoutClassicBtn.IsChecked = layout == PhantomKeyStore.LayoutClassic;
            }
            finally { _loading = false; }

            PhantomKeyStore.SetMouseModeLayout(layout);
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
