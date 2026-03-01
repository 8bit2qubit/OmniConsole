using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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

        /// <summary>
        /// 初始化 <see cref="GamepadNavigationService"/> 類別的新執行個體。
        /// </summary>
        /// <param name="searchRoot">要在其中搜尋下一個焦點元素的根容器 (通常是 Window.Content)。</param>
        /// <param name="dispatcherQueue">目前 UI 執行緒的 DispatcherQueue，用於建立輪詢計時器。</param>
        /// <param name="onAButtonPressed">當按下手把 'A' 鍵時觸發的委派動作。</param>
        public GamepadNavigationService(UIElement searchRoot, DispatcherQueue dispatcherQueue, Action onAButtonPressed)
        {
            _searchRoot = searchRoot;
            _onAButtonPressed = onAButtonPressed;

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

                    // 也將左搖桿映射到上下
                    if (reading.LeftThumbstickY < -0.5 && _previousReading.LeftThumbstickY >= -0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Down);
                    else if (reading.LeftThumbstickY > 0.5 && _previousReading.LeftThumbstickY <= 0.5)
                        TryMoveGamepadFocus(FocusNavigationDirection.Up);

                    _previousReading = reading;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Gamepad Error: {ex}");
            }
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
                var nextElement = FocusManager.FindNextElement(direction, options);
                if (nextElement is Microsoft.UI.Xaml.Controls.Control control)
                {
                    control.Focus(FocusState.Keyboard);
                }
            }
            catch
            {
                try { FocusManager.TryMoveFocus(direction); } catch { }
            }
        }

        /// <summary>
        /// 檢查特定的手把按鈕是否在當前影格被按下 (排除按住不放的情況)。
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
