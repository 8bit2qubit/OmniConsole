using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Services;
using System;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 手把感知的 ContentDialog 基底類別。繼承它的對話方塊自動取得手把導航：
    /// A=觸發焦點元素、B=關閉、D-pad=對話方塊內 XY 焦點移動。
    /// 開啟時自我註冊進全域 <see cref="GamepadNavigationService"/>（Pull 模型）。
    /// 需要 B 鍵特殊行為（擋掉 / 上層目錄等）的對話方塊覆寫 <see cref="OnB"/>；
    /// 需要螢幕鍵盤閃避的覆寫 <see cref="EnableKeyboardAvoidanceOnOpen"/> 為 true。
    /// </summary>
    public partial class GamepadDialogBase : ContentDialog, IGamepadInputScope
    {
        private static GamepadNavigationService? s_service;
        private Action? _keyboardAvoidanceCleanup;

        /// <summary>App 啟動早期注入全域服務，供所有對話方塊自我註冊。須在任何對話方塊開啟前呼叫。</summary>
        public static void AttachService(GamepadNavigationService service) => s_service = service;

        /// <summary>掛上 Opened/Closed 事件，開啟時自我註冊進全域服務、關閉時反註冊並清理。</summary>
        public GamepadDialogBase()
        {
            Opened += OnGamepadDialogOpened;
            Closed += OnGamepadDialogClosed;
        }

        /// <summary>開啟時：向全域 <see cref="GamepadNavigationService"/> 註冊自己（Pull 模型），並依旗標啟用螢幕鍵盤閃避。初始焦點不在此設，留給各子類。</summary>
        private void OnGamepadDialogOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            if (s_service != null)
                s_service.RegisterDialog(this);
            else
                DebugLogger.Log("[GamepadDialog] s_service not attached; gamepad navigation disabled (AttachService missed or called too late)");

            if (EnableKeyboardAvoidanceOnOpen)
                _keyboardAvoidanceCleanup = GamepadNavigationService.EnableKeyboardAvoidance(
                    GetTemplateChild("BackgroundElement") as FrameworkElement, XamlRoot);

            // 初始焦點由各對話方塊自己在 OnOpened 設定（目標元素.Focus(FocusStateHelper.Preferred)）。
            // 基底類別不在此搶焦點，避免與子類初始焦點打架。
        }

        /// <summary>關閉時：向全域服務反註冊自己，並執行螢幕鍵盤閃避的清理（若有啟用）。</summary>
        private void OnGamepadDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            s_service?.UnregisterDialog(this);
            _keyboardAvoidanceCleanup?.Invoke();
            _keyboardAvoidanceCleanup = null;
        }

        // ── 虛擬化清單捲動 / 聚焦的共用時序工具（雙欄 ListView 對話方塊共用） ──────────

        /// <summary>把虛擬化清單捲到指定索引項目（等容器就緒再捲、根治 ScrollIntoView 時序問題）。</summary>
        protected static void ScrollIntoViewWhenReady(ListView list, int index)
            => FocusNavHelper.ScrollIntoViewWhenReady(list, index);

        /// <summary>同上但容器就緒後再聚焦該項（捲動 + 聚焦合一）。</summary>
        protected static void FocusListItemWhenReady(ListView list, int index)
            => FocusNavHelper.FocusListItemWhenReady(list, index);

        /// <summary>
        /// 開啟初期焦點守衛：對抗 ContentDialog 框架預設焦點搶位，短視窗內把焦點拉回目標。
        /// 子類在 OnOpened 呼叫，傳「焦點是否已在目標上」與「聚焦目標」兩個委派；呼叫後自行做首次聚焦嘗試。
        /// </summary>
        protected void GuardInitialFocus(Func<bool> isOnTarget, Action focusTarget)
            => FocusNavHelper.GuardInitialFocus(this, isOnTarget, focusTarget);

        // ── IGamepadInputScope（A/B 鍵與焦點搜尋根的預設語意；子類可覆寫，OnB 最常見） ──────────

        UIElement IGamepadInputScope.SearchRoot => this;

        /// <summary>A 鍵預設：觸發目前焦點元素。</summary>
        public virtual void OnA() => GamepadNavigationService.ActivateFocusedElement(XamlRoot);

        /// <summary>B 鍵預設：關閉對話方塊並回 true。特殊對話方塊可覆寫（如回 true 但不關，或改做 NavigateUp）。</summary>
        public virtual bool OnB() { Hide(); return true; }

        /// <summary>Y 鍵預設：不動作（吃掉、不轉送給背景頁）。需要 Y 鍵語意的對話方塊覆寫（如搜尋方塊/清單切換焦點）。</summary>
        public virtual void OnY() { }

        /// <summary>是否在開啟時啟用螢幕鍵盤閃避（含 TextBox 的對話方塊覆寫為 true）。</summary>
        protected virtual bool EnableKeyboardAvoidanceOnOpen => false;
    }
}
