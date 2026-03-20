using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;
using Windows.UI.Input.Preview.Injection;
using Windows.UI.ViewManagement.Core;

namespace OmniConsole.Services
{
    /// <summary>
    /// 提供 Xbox 手把 (Gamepad) 導覽服務，利用 20 FPS 輪詢 (Polling) 機制將手把的十字鍵、左搖桿、
    /// A/B/X/Y 鍵、LB/RB 肩鍵輸入映射至 WinUI 3 的焦點導覽與元素觸發，並支援螢幕鍵盤顯示與 ContentDialog 鍵盤閃避。
    /// 支援多手把同時連線，各手把狀態獨立追蹤。
    /// </summary>
    public class GamepadNavigationService
    {
        // ── 螢幕鍵盤 ──────────────────────────────────────────────────────────
        // Windows 11 現代遊戲控制器鍵盤（Gamepad Keyboard）透過 CoreInputView.TryShow(CoreInputViewKind.Gamepad)
        // 呼叫（CoreInputViewKind = 7，SDK < 26100.3624 以 int cast 存取）。

        /// <summary>
        /// 根據目前焦點元素類型執行對應動作：
        /// TextBox / AutoSuggestBox → 顯示遊戲控制器鍵盤；其他 → 透過 AutomationPeer 觸發。
        /// </summary>
        /// <param name="xamlRoot">用於查詢目前焦點元素的 XamlRoot。</param>
        public static void ActivateFocusedElement(XamlRoot xamlRoot)
        {
            var focused = FocusManager.GetFocusedElement(xamlRoot);
            if (focused is TextBox || focused is AutoSuggestBox)
                ShowGamepadKeyboard();
            else
                InvokeElement(focused);
        }

        // ── IFrameworkInputPane 直接 vtable 呼叫（用於查詢遊戲控制器鍵盤位置）──────────
        // CoreInputView.OcclusionsChanged 對遊戲控制器鍵盤回傳 count=0，無法使用。
        // IFrameworkInputPane::Location() 是目前能取得遊戲控制器鍵盤實體像素位置 API 的方法之一。
        //
        // 不使用 [ComImport] 介面 + Marshal.GetObjectForIUnknown：Release 建置啟用 trimming 後，
        // 介面資訊被裁剪導致 cast 回傳 null。改以 unsafe vtable 直呼完全繞過 COM marshalling 層。
        //
        // vtable layout（IUnknown: 0=QI 1=AddRef 2=Release；IFrameworkInputPane: 3=Advise 4=AdviseWithHWND 5=Unadvise 6=Location）
        // CLSID: D5120AA3-46BA-44C5-822D-CA8092C1FC72
        // IID:   5752238B-24F0-495A-82F1-2FD593056796
        [StructLayout(LayoutKind.Sequential)]
        private struct Win32Rect { public int Left, Top, Right, Bottom; }

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, out IntPtr ppv);

        /// <summary>透過 vtable slot 6 直呼 IFrameworkInputPane::Location()，取得螢幕鍵盤實體像素 RECT。</summary>
        private static unsafe int CallInputPaneLocation(IntPtr pPane, out Win32Rect rect)
        {
            rect = default;
            if (pPane == IntPtr.Zero) return unchecked((int)0x80004003); // E_POINTER
            // vtable: **pPane → array of function pointers; slot 6 = Location
            var fn = (delegate* unmanaged<IntPtr, Win32Rect*, int>)(*(*(void***)pPane + 6));
            fixed (Win32Rect* pRect = &rect)
                return fn(pPane, pRect);
        }

        /// <summary>
        /// 為 ContentDialog 啟用螢幕鍵盤迴避：遊戲控制器鍵盤彈出時自動將對話方塊上移，收起後復位。
        /// 使用方式：在 Opened 事件中以 <c>GetTemplateChild("BackgroundElement") as FrameworkElement</c> 取得對話方塊 Border 並傳入；
        /// 將回傳的清除委派存起來，於 Closed 事件中呼叫以停止輪詢。
        /// 注意：CoreInputView.OcclusionsChanged 對遊戲控制器鍵盤回傳 count=0，無法用於偵測，
        /// 因此改以 100ms 計時器輪詢 IFrameworkInputPane::Location() 取得鍵盤位置。
        /// 注意：必須傳入 "BackgroundElement"（實際對話方塊 Border），而非 "Container"（全螢幕 overlay）；
        /// 傳入 Container 時 ActualHeight == screenHeight，上移計算會完全錯誤。
        /// </summary>
        /// <param name="dialogContainer">ContentDialog ControlTemplate 中的實際對話方塊 Border（名稱 "BackgroundElement"）。</param>
        /// <param name="xamlRoot">對話方塊所在視窗的 XamlRoot，用於取得螢幕尺寸與縮放比例。</param>
        /// <returns>清除委派，於 Closed 時呼叫以停止計時器。</returns>
        public static Action EnableKeyboardAvoidance(FrameworkElement? dialogContainer, XamlRoot xamlRoot)
        {
            DebugLogger.Log($"[KeyboardAvoidance] dialogContainer={(dialogContainer == null ? "null" : dialogContainer.GetType().Name)}");
            if (dialogContainer == null) return () => { };

            // CoCreate IFrameworkInputPane，保留原始 COM 指標（不透過 RCW 以避免 trimming 問題）
            IntPtr pInputPane = IntPtr.Zero;
            try
            {
                var clsid = new Guid("D5120AA3-46BA-44C5-822D-CA8092C1FC72");
                var iid = new Guid("5752238B-24F0-495A-82F1-2FD593056796");
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out pInputPane);
                DebugLogger.Log($"[KeyboardAvoidance] CoCreateInstance hr=0x{hr:X8}, ptr={(pInputPane == IntPtr.Zero ? "null" : "ok")}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[KeyboardAvoidance] CoCreateInstance exception: {ex.Message}");
            }

            if (pInputPane == IntPtr.Zero) return () => { };

            // 改用 layout 定位（VerticalAlignment=Top + Margin.Top）取代 TranslateTransform：
            // BackgroundElement 預設 VerticalAlignment=Center，設 MaxHeight 後元素縮小會重新置中，
            // 導致 TranslateTransform 偏移量失準。Layout 定位是可靠的解法之一。
            var originalVAlignment = dialogContainer.VerticalAlignment;
            var originalMargin = dialogContainer.Margin;
            bool wasVisible = false;
            double naturalDialogHeight = 0;
            ScrollViewer? contentScrollViewer = null;
            ScrollBarVisibility originalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ScrollMode originalScrollMode = ScrollMode.Disabled;

            var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(100);
            timer.Tick += (s, e) =>
            {
                try
                {
                    int hr = CallInputPaneLocation(pInputPane, out Win32Rect rect);
                    bool hasKeyboard = hr == 0 && (rect.Right - rect.Left) > 0 && (rect.Bottom - rect.Top) > 0;

                    if (!hasKeyboard)
                    {
                        if (wasVisible)
                        {
                            DebugLogger.Log("[KeyboardAvoidance] Keyboard hidden, restoring layout");
                            dialogContainer.VerticalAlignment = originalVAlignment;
                            dialogContainer.Margin = originalMargin;
                            dialogContainer.MaxHeight = double.PositiveInfinity;
                            if (contentScrollViewer != null)
                            {
                                contentScrollViewer.VerticalScrollMode = originalScrollMode;
                                contentScrollViewer.VerticalScrollBarVisibility = originalScrollBarVisibility;
                            }
                        }
                        wasVisible = false;
                        naturalDialogHeight = 0;
                        contentScrollViewer = null;
                        return;
                    }

                    // 鍵盤剛出現時快取自然高度與 ContentScrollViewer 原始捲動設定
                    if (!wasVisible)
                    {
                        naturalDialogHeight = dialogContainer.ActualHeight;
                        contentScrollViewer = FindDescendant<ScrollViewer>(dialogContainer);
                        if (contentScrollViewer != null)
                        {
                            originalScrollBarVisibility = contentScrollViewer.VerticalScrollBarVisibility;
                            originalScrollMode = contentScrollViewer.VerticalScrollMode;
                            DebugLogger.Log($"[KeyboardAvoidance] ContentScrollViewer found, origVisibility={originalScrollBarVisibility} origMode={originalScrollMode}");
                        }
                        else
                        {
                            DebugLogger.Log("[KeyboardAvoidance] ContentScrollViewer not found");
                        }
                    }

                    double scale = xamlRoot.RasterizationScale;
                    double screenHeight = xamlRoot.Size.Height;           // DIP
                    double keyboardTopDip = rect.Top / scale;              // DIP
                    const double edgePadding = 16;

                    // 對話方塊自然頂部（垂直置中時的位置）
                    double dialogNaturalTop = (screenHeight - naturalDialogHeight) / 2.0;

                    // 理想頂部：上移到鍵盤上方 edgePadding 處，但不超出螢幕頂部
                    double dialogBottom = dialogNaturalTop + naturalDialogHeight;
                    double overlap = dialogBottom - keyboardTopDip + edgePadding;
                    double targetTop = Math.Max(edgePadding, dialogNaturalTop - overlap);

                    // 可用高度：鍵盤頂部到螢幕頂部（含 padding）
                    double availableHeight = keyboardTopDip - targetTop - edgePadding;

                    dialogContainer.VerticalAlignment = VerticalAlignment.Top;
                    dialogContainer.Margin = new Thickness(
                        originalMargin.Left, targetTop, originalMargin.Right, 0);
                    bool needsCompression = availableHeight < naturalDialogHeight;
                    dialogContainer.MaxHeight = needsCompression
                        ? Math.Max(availableHeight, 200)
                        : double.PositiveInfinity;

                    // 壓縮時啟用 ContentScrollViewer 捲動，讓被擠壓的內容可捲動
                    if (contentScrollViewer != null)
                    {
                        contentScrollViewer.VerticalScrollMode = needsCompression ? ScrollMode.Auto : originalScrollMode;
                        contentScrollViewer.VerticalScrollBarVisibility = needsCompression ? ScrollBarVisibility.Auto : originalScrollBarVisibility;
                    }

                    if (!wasVisible)
                        DebugLogger.Log($"[KeyboardAvoidance] Keyboard shown: scale={scale} screenH={screenHeight} kbTop={keyboardTopDip} naturalH={naturalDialogHeight} targetTop={targetTop} availH={availableHeight} MaxH={dialogContainer.MaxHeight}");
                    wasVisible = true;
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[KeyboardAvoidance] Poll exception: {ex.Message}");
                    dialogContainer.VerticalAlignment = originalVAlignment;
                    dialogContainer.Margin = originalMargin;
                    dialogContainer.MaxHeight = double.PositiveInfinity;
                }
            };
            timer.Start();

            return () =>
            {
                timer.Stop();
                dialogContainer.VerticalAlignment = originalVAlignment;
                dialogContainer.Margin = originalMargin;
                dialogContainer.MaxHeight = double.PositiveInfinity;
                if (pInputPane != IntPtr.Zero) Marshal.Release(pInputPane);
            };
        }

        /// <summary>
        /// 透過 <see cref="CoreInputView"/> 顯示 Windows 11 遊戲控制器鍵盤（Layout 7）。
        /// </summary>
        private static void ShowGamepadKeyboard()
        {
            try
            {
                // CoreInputViewKind.Gamepad = 7，Windows 11 SDK 26100.3624+ 正式命名；
                // 較舊 SDK 以 int cast 存取，行為相同
                CoreInputView.GetForCurrentView().TryShow((CoreInputViewKind)7);
            }
            catch { }
        }

        /// <summary>
        /// 透過 AutomationPeer 觸發焦點元素的主要動作（Invoke / Toggle / ExpandCollapse）。
        /// </summary>
        private static void InvokeElement(object? focused)
        {
            if (focused is not FrameworkElement fe) return;
            try
            {
                var peer = FrameworkElementAutomationPeer.FromElement(fe)
                        ?? FrameworkElementAutomationPeer.CreatePeerForElement(fe);
                if (peer is null) return;

                if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                    invoke.Invoke();
                else if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                    toggle.Toggle();
                else if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
                    expand.Expand();
            }
            catch { }
        }

        private DispatcherQueueTimer? _gamepadTimer;
        private readonly Dictionary<Gamepad, GamepadReading> _previousReadings = new();
        private ComboBox? _activeComboBox;
        private InputInjector? _inputInjector;
        private readonly UIElement _searchRoot;
        private readonly Action _onAButtonPressed;
        private readonly Action? _onBButtonPressed;
        private readonly Action? _onLBPressed;
        private readonly Action? _onRBPressed;
        private readonly Action? _onXButtonPressed;
        private readonly Action? _onYButtonPressed;
        private readonly Action? _onMenuButtonPressed;

        /// <summary>
        /// 初始化 <see cref="GamepadNavigationService"/> 類別的新執行個體。
        /// </summary>
        /// <param name="searchRoot">要在其中搜尋下一個焦點元素的根容器 (通常是 Window.Content)。</param>
        /// <param name="dispatcherQueue">目前 UI 執行緒的 DispatcherQueue，用於建立輪詢計時器。</param>
        /// <param name="onAButtonPressed">當按下手把 'A' 鍵時觸發的委派動作。</param>
        /// <param name="onBButtonPressed">當按下手把 'B' 鍵時觸發的委派動作（可選）。</param>
        /// <param name="onLBPressed">當按下手把 'LB' 肩鍵時觸發的委派動作（可選）。</param>
        /// <param name="onRBPressed">當按下手把 'RB' 肩鍵時觸發的委派動作（可選）。</param>
        /// <param name="onXButtonPressed">當按下手把 'X' 鍵時觸發的委派動作（可選）。</param>
        /// <param name="onYButtonPressed">當按下手把 'Y' 鍵時觸發的委派動作（可選）。</param>
        /// <param name="onMenuButtonPressed">當按下手把 'Menu（☰）' 鍵時觸發的委派動作（可選）。</param>
        public GamepadNavigationService(UIElement searchRoot, DispatcherQueue dispatcherQueue, Action onAButtonPressed, Action? onBButtonPressed = null, Action? onLBPressed = null, Action? onRBPressed = null, Action? onXButtonPressed = null, Action? onYButtonPressed = null, Action? onMenuButtonPressed = null)
        {
            _searchRoot = searchRoot;
            _onAButtonPressed = onAButtonPressed;
            _onBButtonPressed = onBButtonPressed;
            _onLBPressed = onLBPressed;
            _onRBPressed = onRBPressed;
            _onXButtonPressed = onXButtonPressed;
            _onYButtonPressed = onYButtonPressed;
            _onMenuButtonPressed = onMenuButtonPressed;

            _gamepadTimer = dispatcherQueue.CreateTimer();
            _gamepadTimer.Interval = TimeSpan.FromMilliseconds(50); // 20 FPS
            _gamepadTimer.Tick += GamepadTimer_Tick;

            try { _inputInjector = InputInjector.TryCreate(); } catch { }
        }

        /// <summary>
        /// 啟動手把輸入的輪詢計時器。
        /// </summary>
        public void Start()
        {
            _gamepadTimer?.Start();
        }

        /// <summary>
        /// 停止手把輸入的輪詢計時器。
        /// </summary>
        public void Stop()
        {
            _gamepadTimer?.Stop();
        }

        /// <summary>
        /// 定期輪詢手把狀態並將十字鍵/類比搖桿輸入轉換為焦點導覽動作。
        /// </summary>
        private void GamepadTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            try
            {
                var gamepads = Gamepad.Gamepads;
                if (gamepads.Count == 0) return;

                foreach (var gamepad in gamepads)
                {
                    var reading = gamepad.GetCurrentReading();
                    _previousReadings.TryGetValue(gamepad, out var prev);

                    // 在處理任何按鈕或位移前，先確保焦點位於有效的控制項上
                    if (IsAnyInputActive(reading))
                        EnsureFocus();

                    // 防呆：下拉清單若已被外部關閉（滑鼠點選旁邊），清除狀態
                    if (_activeComboBox != null && !_activeComboBox.IsDropDownOpen)
                        _activeComboBox = null;
                    bool inputHandled = false;

                    if (_activeComboBox != null)
                    {
                        // ── ComboBox 下拉清單已展開：攔截方向鍵與 A/B ──────────────
                        int focusedIdx = GetFocusedComboBoxItemIndex(_activeComboBox);

                        if (IsButtonPressed(reading, prev, GamepadButtons.A))
                        {
                            // A 確認：把鍵盤焦點所在項目設為選取值，然後收合
                            if (focusedIdx != -1)
                                _activeComboBox.SelectedIndex = focusedIdx;
                            _activeComboBox.IsDropDownOpen = false;
                            _activeComboBox.Focus(FocusState.Keyboard);
                            _activeComboBox = null;
                            inputHandled = true;
                        }
                        else if (IsButtonPressed(reading, prev, GamepadButtons.B))
                        {
                            // B 取消：不改 SelectedIndex，直接收合
                            _activeComboBox.IsDropDownOpen = false;
                            _activeComboBox.Focus(FocusState.Keyboard);
                            _activeComboBox = null;
                            inputHandled = true;
                        }
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadDown))
                        {
                            int from = focusedIdx == -1 ? Math.Max(0, _activeComboBox.SelectedIndex) : focusedIdx;
                            if (from < _activeComboBox.Items.Count - 1
                                && _activeComboBox.ContainerFromIndex(from + 1) is Control next)
                                next.Focus(FocusState.Keyboard);
                            inputHandled = true;
                        }
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadUp))
                        {
                            int from = focusedIdx == -1 ? Math.Max(0, _activeComboBox.SelectedIndex) : focusedIdx;
                            if (from > 0
                                && _activeComboBox.ContainerFromIndex(from - 1) is Control prev2)
                                prev2.Focus(FocusState.Keyboard);
                            inputHandled = true;
                        }
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadLeft)
                              || IsButtonPressed(reading, prev, GamepadButtons.DPadRight))
                        {
                            inputHandled = true; // 封鎖左右，避免離開清單
                        }
                    }
                    else
                    {
                        // ── 無作用中下拉清單：偵測展開觸發條件 ─────────────────────
                        var focused = FocusManager.GetFocusedElement(_searchRoot.XamlRoot);

                        // ComboBox：A 鍵展開
                        if (focused is ComboBox comboBox && IsButtonPressed(reading, prev, GamepadButtons.A))
                        {
                            comboBox.IsDropDownOpen = true;
                            _activeComboBox = comboBox;
                            inputHandled = true;
                        }
                        // AutoSuggestBox：焦點可能落在 ASB 內部的 TextBox，用 FindParent 向上確認
                        // 清單已開啟時，以 InputInjector 模擬鍵盤事件，由 ASB 原生處理導覽與選取
                        else if (focused is DependencyObject focusedDep)
                        {
                            var asb = focused as AutoSuggestBox ?? FindParent<AutoSuggestBox>(focusedDep);
                            if (asb != null && asb.IsSuggestionListOpen)
                            {
                                if (IsButtonPressed(reading, prev, GamepadButtons.DPadDown))
                                { SimulateKeyPress(Windows.System.VirtualKey.Down); inputHandled = true; }
                                else if (IsButtonPressed(reading, prev, GamepadButtons.DPadUp))
                                { SimulateKeyPress(Windows.System.VirtualKey.Up); inputHandled = true; }
                                else if (IsButtonPressed(reading, prev, GamepadButtons.A))
                                { SimulateKeyPress(Windows.System.VirtualKey.Enter); inputHandled = true; }
                                else if (IsButtonPressed(reading, prev, GamepadButtons.B))
                                { SimulateKeyPress(Windows.System.VirtualKey.Escape); inputHandled = true; }
                                else if (IsButtonPressed(reading, prev, GamepadButtons.DPadLeft)
                                      || IsButtonPressed(reading, prev, GamepadButtons.DPadRight))
                                { inputHandled = true; } // 封鎖左右，避免焦點離開 ASB
                            }
                        }
                    }

                    if (!inputHandled)
                    {
                        if (IsButtonPressed(reading, prev, GamepadButtons.DPadDown))
                            TryMoveGamepadFocus(FocusNavigationDirection.Down);
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadUp))
                            TryMoveGamepadFocus(FocusNavigationDirection.Up);
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadLeft))
                            TryMoveGamepadFocus(FocusNavigationDirection.Left);
                        else if (IsButtonPressed(reading, prev, GamepadButtons.DPadRight))
                            TryMoveGamepadFocus(FocusNavigationDirection.Right);
                        else if (IsButtonPressed(reading, prev, GamepadButtons.A))
                            _onAButtonPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.B))
                            _onBButtonPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.LeftShoulder))
                            _onLBPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.RightShoulder))
                            _onRBPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.X))
                            _onXButtonPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.Y))
                            _onYButtonPressed?.Invoke();
                        else if (IsButtonPressed(reading, prev, GamepadButtons.Menu))
                            _onMenuButtonPressed?.Invoke();
                    }

                    // 也將左搖桿映射到上下左右（支援橫向卡片網格導覽）
                    // ComboBox 或 AutoSuggestBox 清單展開時，inputHandled 已為 true，搖桿亦一同封鎖
                    if (!inputHandled)
                    {
                        if (reading.LeftThumbstickY < -0.5 && prev.LeftThumbstickY >= -0.5)
                            TryMoveGamepadFocus(FocusNavigationDirection.Down);
                        else if (reading.LeftThumbstickY > 0.5 && prev.LeftThumbstickY <= 0.5)
                            TryMoveGamepadFocus(FocusNavigationDirection.Up);
                        else if (reading.LeftThumbstickX < -0.5 && prev.LeftThumbstickX >= -0.5)
                            TryMoveGamepadFocus(FocusNavigationDirection.Left);
                        else if (reading.LeftThumbstickX > 0.5 && prev.LeftThumbstickX <= 0.5)
                            TryMoveGamepadFocus(FocusNavigationDirection.Right);
                    }

                    _previousReadings[gamepad] = reading;
                }

                // 清理已斷線的手把
                var connected = new HashSet<Gamepad>(gamepads);
                foreach (var key in _previousReadings.Keys.ToList())
                    if (!connected.Contains(key))
                        _previousReadings.Remove(key);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GamepadNavigationService] Gamepad error: {ex.Message}");
            }
        }

        /// <summary>
        /// 檢查手把是否有任何具備意圖的輸入（方向鍵、A/B、LB/RB 或左搖桿傾斜）。
        /// </summary>
        private bool IsAnyInputActive(GamepadReading reading)
        {
            return (reading.Buttons & (GamepadButtons.DPadDown | GamepadButtons.DPadUp | GamepadButtons.DPadLeft | GamepadButtons.DPadRight | GamepadButtons.A | GamepadButtons.B | GamepadButtons.LeftShoulder | GamepadButtons.RightShoulder)) != 0 ||
                   Math.Abs(reading.LeftThumbstickY) > 0.5 ||
                   Math.Abs(reading.LeftThumbstickX) > 0.5;
        }

        /// <summary>
        /// 確保焦點目前落在 <see cref="_searchRoot"/> 內的有效控制項上。
        /// 若焦點遺失或位於非互動元件，則強制恢復。
        /// </summary>
        private void EnsureFocus()
        {
            try
            {
                var focusedElement = FocusManager.GetFocusedElement(_searchRoot.XamlRoot);

                // 只要有任何 Control 持有焦點（含 Popup/下拉選單內的項目），便不干預
                if (focusedElement is Microsoft.UI.Xaml.Controls.Control) return;

                // 焦點遺失或落在非互動元件 → 還原至 SearchRoot 第一個可聚焦項目
                var firstElement = FocusManager.FindFirstFocusableElement(_searchRoot);
                if (firstElement is Microsoft.UI.Xaml.Controls.Control firstControl)
                    firstControl.Focus(FocusState.Keyboard);
            }
            catch { }
        }

        /// <summary>
        /// 遞迴檢查某個元件是否為特定父元件的子系。
        /// </summary>
        private bool IsDescendantOf(DependencyObject parent, DependencyObject? child)
        {
            if (child == null) return false;
            if (ReferenceEquals(parent, child)) return true;

            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (ReferenceEquals(parent, current)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        /// <summary>
        /// 嘗試將焦點朝指定方向移動。
        /// 若焦點在 SearchRoot 內，使用限定範圍的 <see cref="FocusManager.TryMoveFocusAsync"/> 以防止焦點飄移到視窗邊緣；
        /// 若焦點在 SearchRoot 外（如下拉選單 Popup），改用無限制的 <see cref="FocusManager.TryMoveFocus"/> 讓 Popup 內項目可自由移動。
        /// </summary>
        /// <param name="direction">焦點移動的方向 (上下左右)。</param>
        private void TryMoveGamepadFocus(FocusNavigationDirection direction)
        {
            try
            {
                var focused = FocusManager.GetFocusedElement(_searchRoot.XamlRoot);
                if (focused is DependencyObject dep && IsDescendantOf(_searchRoot, dep))
                {
                    // 焦點在 SearchRoot 內 → 使用限定範圍導航
                    var options = new FindNextElementOptions { SearchRoot = _searchRoot };
                    _ = FocusManager.TryMoveFocusAsync(direction, options);
                }
                else
                {
                    // 焦點在 SearchRoot 外（如下拉選單 Popup）→ 自由導航，讓 Popup 內項目可移動
                    FocusManager.TryMoveFocus(direction);
                }
            }
            catch
            {
                try { FocusManager.TryMoveFocus(direction); } catch { }
            }
        }

        /// <summary>
        /// 模擬一次按鍵（按下後放開），供 AutoSuggestBox 建議清單導覽使用。
        /// </summary>
        private void SimulateKeyPress(Windows.System.VirtualKey key)
        {
            if (_inputInjector == null) return;
            try
            {
                var down = new InjectedInputKeyboardInfo { VirtualKey = (ushort)key, KeyOptions = InjectedInputKeyOptions.None };
                var up = new InjectedInputKeyboardInfo { VirtualKey = (ushort)key, KeyOptions = InjectedInputKeyOptions.KeyUp };
                _inputInjector.InjectKeyboardInput(new[] { down, up });
            }
            catch { }
        }

        /// <summary>
        /// 在視覺樹中向上尋找最近符合型別的祖先元素。
        /// </summary>
        private static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var current = VisualTreeHelper.GetParent(child);
            while (current != null)
            {
                if (current is T result) return result;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        /// <summary>
        /// 在視覺樹中遞迴尋找第一個符合型別的子元素。
        /// </summary>
        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result) return result;
                var descendant = FindDescendant<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }

        /// <summary>
        /// 找出目前鍵盤焦點落在 ComboBox 下拉清單中的哪個索引。
        /// 找不到時回傳 -1。
        /// </summary>
        private int GetFocusedComboBoxItemIndex(ComboBox comboBox)
        {
            var currentFocus = FocusManager.GetFocusedElement(_searchRoot.XamlRoot);
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (ReferenceEquals(comboBox.ContainerFromIndex(i), currentFocus))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 檢查特定的手把按鈕是否在目前影格被按下 (排除按住不放的情況)。
        /// </summary>
        /// <param name="current">目前的按鈕狀態。</param>
        /// <param name="previous">前一個影格的按鈕狀態。</param>
        /// <param name="button">要檢查的目標按鈕。</param>
        /// <returns>如果是剛按下的狀態則傳回 true，否則傳回 false。</returns>
        private bool IsButtonPressed(GamepadReading current, GamepadReading previous, GamepadButtons button)
        {
            return (current.Buttons & button) == button && (previous.Buttons & button) != button;
        }
    }
}
