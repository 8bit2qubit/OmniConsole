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
    public sealed partial class PhantomLinkWidget : Page
    {
        private bool _loading;
        private bool _builtInMapping;

        // 此時刻之前送達的 D-pad Down 視為 Game Bar 合成輸入，攔下
        private DateTime _focusGuardUntil;

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

                SetInitialFocus();
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
        /// Game Bar 主題變更事件可能來自非 UI thread，marshal 回 UI thread 再套用。
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
            SetInitialFocus();
        }

        // ── 焦點守門窗：吞掉 Game Bar 開啟時合成的 D-pad Down ────────────────

        /// <summary>
        /// 焦點從 widget 外部（Game Bar 本體 / 其他 widget）重新進入 → 開啟守門窗，
        /// 用以吞掉 Game Bar 隨後合成的 D-pad Down 事件。
        /// </summary>
        private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
        {
            var oldFE = args.OldFocusedElement as FrameworkElement;
            if (oldFE == null || !IsDescendant(oldFE))
            {
                _focusGuardUntil = DateTime.UtcNow.AddMilliseconds(500);
                DebugLogger.Log("[Widget] focus re-entered → arm guard");
            }
        }

        /// <summary>
        /// 判斷節點是否為本 Page 視覺樹內的子元素；用於區分焦點是否從 widget 外部進入。
        /// </summary>
        private bool IsDescendant(DependencyObject node)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, this)) return true;
                node = Windows.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>
        /// 初始焦點放 Page 本身（IsTabStop=True + UseSystemFocusVisuals=False）：
        /// 有「目前焦點所有者」可承接合成 Down、但不顯示 focus visual → 視覺上無按鈕被醒目提示，
        /// 直到第一個 Down 被攔下時才 Focus 到 Mode 按鈕觸發醒目提示（對齊微軟 Widget 行為）。
        /// </summary>
        private void SetInitialFocus()
        {
            _focusGuardUntil = DateTime.UtcNow.AddMilliseconds(500);
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                try
                {
                    bool ok = this.Focus(FocusState.Programmatic);
                    DebugLogger.Log("[Widget] SetInitialFocus -> Page ok=" + ok);
                }
                catch (Exception ex) { DebugLogger.Log("[Widget] SetInitialFocus FAIL: " + ex); }
            });
        }

        // ── 跨 Section D-pad 導航 ───────────────────────────────────────────

        /// <summary>
        /// 攔 Up/Down 兩種情境：
        ///   1) 守門窗期內的合成 Down 直接吃掉（Game Bar 啟用訊號）
        ///   2) 跨 section 導航時，落點挑「已選中」的 ToggleButton（ToggleButton row）
        ///      或唯一的 Slider / ComboBox — 通用規則，未來新增 section 不需改程式。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            bool down = e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.Down;
            bool up = e.Key == VirtualKey.GamepadDPadUp || e.Key == VirtualKey.Up;
            if (!down && !up) return;

            if (down && DateTime.UtcNow < _focusGuardUntil)
            {
                DebugLogger.Log("[Widget] swallow synthetic Down → focus current Mode");
                _focusGuardUntil = DateTime.MinValue;
                e.Handled = true;
                FocusSection(ModeSection);
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
        /// 走到 RootPanel 的直屬 child，作為「section」代表。
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
        /// Section 內挑焦點目標：checked ToggleButton > 第一顆 ToggleButton > Slider > ComboBox。
        /// </summary>
        private bool FocusSection(FrameworkElement section)
        {
            if (section == null) return false;
            var toggles = FindDescendants<ToggleButton>(section).Where(t => t.IsEnabled).ToList();
            if (toggles.Count > 0)
            {
                var target = toggles.FirstOrDefault(t => t.IsChecked == true) ?? toggles[0];
                return target.Focus(FocusState.Keyboard);
            }
            var slider = FindDescendants<Slider>(section).FirstOrDefault(s => s.IsEnabled);
            if (slider != null) return slider.Focus(FocusState.Keyboard);
            var combo = FindDescendants<ComboBox>(section).FirstOrDefault(c => c.IsEnabled);
            if (combo != null) return combo.Focus(FocusState.Keyboard);
            return false;
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
            }
            finally
            {
                _loading = false;
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

        // ── UI 事件處理 ─────────────────────────────────────────────────────

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
