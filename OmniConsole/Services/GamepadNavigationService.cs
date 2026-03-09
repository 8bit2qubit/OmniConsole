using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Gaming.Input;

namespace OmniConsole.Services
{
    /// <summary>
    /// 提供 Xbox 手把 (Gamepad) 導覽服務，利用輪詢 (Polling) 機制將手把的十字鍵與左搖桿輸入映射至 WinUI 3 的焦點導覽。
    /// 解決 WinUI 3 桌面應用程式缺乏原生手把方向鍵 UI 導覽支援的問題。
    /// </summary>
    public class GamepadNavigationService
    {
        private DispatcherQueueTimer? _gamepadTimer;
        private GamepadReading _previousReading;
        private readonly UIElement _searchRoot;
        private readonly Action _onAButtonPressed;
        private readonly Action? _onBButtonPressed;
        private readonly Action? _onLBPressed;
        private readonly Action? _onRBPressed;
        private readonly Action? _onXButtonPressed;
        private readonly Action? _onYButtonPressed;

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
        public GamepadNavigationService(UIElement searchRoot, DispatcherQueue dispatcherQueue, Action onAButtonPressed, Action? onBButtonPressed = null, Action? onLBPressed = null, Action? onRBPressed = null, Action? onXButtonPressed = null, Action? onYButtonPressed = null)
        {
            _searchRoot = searchRoot;
            _onAButtonPressed = onAButtonPressed;
            _onBButtonPressed = onBButtonPressed;
            _onLBPressed = onLBPressed;
            _onRBPressed = onRBPressed;
            _onXButtonPressed = onXButtonPressed;
            _onYButtonPressed = onYButtonPressed;

            _gamepadTimer = dispatcherQueue.CreateTimer();
            _gamepadTimer.Interval = TimeSpan.FromMilliseconds(50); // 20 FPS
            _gamepadTimer.Tick += GamepadTimer_Tick;
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
                if (Gamepad.Gamepads.Count > 0)
                {
                    var gamepad = Gamepad.Gamepads[0];
                    var reading = gamepad.GetCurrentReading();

                    // 在處理任何按鈕或位移前，先確保焦點位於有效的控制項上
                    if (IsAnyInputActive(reading))
                    {
                        EnsureFocus();
                    }

                    if (IsButtonPressed(reading, _previousReading, GamepadButtons.DPadDown))
                        TryMoveGamepadFocus(FocusNavigationDirection.Down);
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.DPadUp))
                        TryMoveGamepadFocus(FocusNavigationDirection.Up);
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.DPadLeft))
                        TryMoveGamepadFocus(FocusNavigationDirection.Left);
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.DPadRight))
                        TryMoveGamepadFocus(FocusNavigationDirection.Right);
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.A))
                    {
                        _onAButtonPressed?.Invoke();
                    }
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.B))
                    {
                        _onBButtonPressed?.Invoke();
                    }
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.LeftShoulder))
                    {
                        _onLBPressed?.Invoke();
                    }
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.RightShoulder))
                    {
                        _onRBPressed?.Invoke();
                    }
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.X))
                    {
                        _onXButtonPressed?.Invoke();
                    }
                    else if (IsButtonPressed(reading, _previousReading, GamepadButtons.Y))
                    {
                        _onYButtonPressed?.Invoke();
                    }

                    // 也將左搖桿映射到上下左右（支援橫向卡片網格導覽）
                    if (reading.LeftThumbstickY < -0.5 && _previousReading.LeftThumbstickY >= -0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Down);
                    else if (reading.LeftThumbstickY > 0.5 && _previousReading.LeftThumbstickY <= 0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Up);
                    else if (reading.LeftThumbstickX < -0.5 && _previousReading.LeftThumbstickX >= -0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Left);
                    else if (reading.LeftThumbstickX > 0.5 && _previousReading.LeftThumbstickX <= 0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Right);

                    _previousReading = reading;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gamepad Error: {ex}");
            }
        }

        /// <summary>
        /// 檢查手把是否有任何具備意圖的輸入（方向或 A 鍵）。
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

                // 檢查焦點是否遺失、不是 Control，或是焦點不在預期的 _searchRoot 範圍內
                if (focusedElement == null ||
                    !(focusedElement is Microsoft.UI.Xaml.Controls.Control) ||
                    !IsDescendantOf(_searchRoot, focusedElement as DependencyObject))
                {
                    var firstElement = FocusManager.FindFirstFocusableElement(_searchRoot);
                    if (firstElement is Microsoft.UI.Xaml.Controls.Control firstControl)
                    {
                        firstControl.Focus(FocusState.Keyboard);
                    }
                }
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
        /// 嘗試將焦點朝指定方向移動，並強制使用高反差外框的 Keyboard 焦點狀態定錨選項。
        /// </summary>
        /// <param name="direction">焦點移動的方向 (上下左右)。</param>
        private void TryMoveGamepadFocus(FocusNavigationDirection direction)
        {
            try
            {
                var options = new FindNextElementOptions { SearchRoot = _searchRoot };
                _ = FocusManager.TryMoveFocusAsync(direction, options);
            }
            catch
            {
                try { FocusManager.TryMoveFocus(direction); } catch { }
            }
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
