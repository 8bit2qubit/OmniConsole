using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Services;
using System;

namespace OmniConsole.Dialogs
{
    /// <summary>對話方塊此刻允許哪些關閉路徑。</summary>
    public enum DialogDismissPolicy
    {
        /// <summary>全部放行（預設）。</summary>
        Allow,

        /// <summary>
        /// 擋掉「不做選擇就走」的路徑：關閉按鈕、標題列 X、Esc 與手把 B；
        /// 按主要或次要按鈕仍照常關閉。用於必須做出選擇才能繼續的情境。
        /// </summary>
        RequireChoice,

        /// <summary>
        /// 擋掉全部關閉路徑，含按鈕。用於作業進行中不容中斷的情境；
        /// 呼叫端要主動關閉時先把 <see cref="GamepadDialogBase.DismissPolicy"/> 設回 Allow 再 Hide()。
        /// </summary>
        Block,
    }

    /// <summary>
    /// 手把感知的 ContentDialog 基底類別。繼承它的對話方塊自動取得手把導航：
    /// A=觸發焦點元素、B=關閉、D-pad=對話方塊內 XY 焦點移動。
    /// 開啟時自我註冊進全域 <see cref="GamepadNavigationService"/>（Pull 模型）。
    /// 需要 B 鍵特殊行為（上層目錄等）的對話方塊覆寫 <see cref="OnB"/>；
    /// 需要擋下關閉的設 <see cref="DismissPolicy"/>，不必自行掛 Closing 或覆寫 OnB；
    /// 需要螢幕鍵盤閃避的覆寫 <see cref="EnableKeyboardAvoidanceOnOpen"/> 為 true。
    /// </summary>
    public partial class GamepadDialogBase : ContentDialog, IGamepadInputScope
    {
        private static GamepadNavigationService? s_service;
        private Action? _keyboardAvoidanceCleanup;

        /// <summary>App 啟動早期注入全域服務，供所有對話方塊自我註冊。須在任何對話方塊開啟前呼叫。</summary>
        public static void AttachService(GamepadNavigationService service) => s_service = service;

        /// <summary>
        /// 此刻允許哪些關閉路徑。所有攔截集中在基底處理：<see cref="ContentDialog.Closing"/> 擋
        /// 關閉按鈕/標題列 X/Esc，<see cref="OnB"/> 擋手把 B，兩條路各自要攔一次。
        /// 狀態會變的對話方塊（如進行中不給關）在狀態切換處改這個值即可。
        /// </summary>
        public DialogDismissPolicy DismissPolicy { get; set; } = DialogDismissPolicy.Allow;

        /// <summary>掛上 Opened/Closed/Closing 事件，開啟時自我註冊進全域服務、關閉時反註冊並清理。</summary>
        public GamepadDialogBase()
        {
            Opened += OnGamepadDialogOpened;
            Closed += OnGamepadDialogClosed;
            Closing += OnGamepadDialogClosing;
        }

        /// <summary>
        /// 依 <see cref="DismissPolicy"/> 攔截關閉按鈕/標題列 X/Esc。
        /// RequireChoice 只擋 Result 為 None 的路徑（按主要或次要按鈕仍照常關閉），Block 則連按鈕一起擋。
        /// 手把 B 走的是 <see cref="OnB"/>，另外擋一次。
        /// </summary>
        private void OnGamepadDialogClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            bool blocked = DismissPolicy == DialogDismissPolicy.Block
                || (DismissPolicy == DialogDismissPolicy.RequireChoice && args.Result == ContentDialogResult.None);
            if (blocked) args.Cancel = true;
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

        /// <summary>
        /// B 鍵預設：關閉對話方塊並回 true。特殊對話方塊可覆寫（如改做 NavigateUp）。
        /// <see cref="DismissPolicy"/> 不是 Allow 時擋下，並回 false 讓 service 不補播返回音效
        /// （回 true 會發出「已返回」的聲音卻什麼都沒發生，與畫面不符）。
        /// </summary>
        public virtual bool OnB()
        {
            if (DismissPolicy != DialogDismissPolicy.Allow) return false;
            Hide();
            return true;
        }

        /// <summary>Y 鍵預設：不動作（吃掉、不轉送給背景頁）。需要 Y 鍵語意的對話方塊覆寫（如搜尋方塊/清單切換焦點）。</summary>
        public virtual void OnY() { }

        /// <summary>是否在開啟時啟用螢幕鍵盤閃避（含 TextBox 的對話方塊覆寫為 true）。</summary>
        protected virtual bool EnableKeyboardAvoidanceOnOpen => false;
    }
}
