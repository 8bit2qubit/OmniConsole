using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 手把映射編輯流程通用的訊息/確認對話方塊（刪除確認 / 全 None 確認 / 黑名單提示等共用）。
    /// </summary>
    public sealed partial class GamepadMessageDialog : GamepadDialogBase
    {
        /// <summary>使用者按下 primary 鈕（確定/是）後為 true；否則為 false。</summary>
        public bool Result { get; private set; }

        /// <summary>
        /// 建立訊息/確認對話方塊；closeText 傳 null 表示「純資訊單按鈕」，primaryText 當關閉鈕字樣。
        /// defaultToPrimary=true 時預設醒目 Primary 鈕（如「立即重啟」方便直接確認）；預設 false＝醒目 Close
        /// （危險操作如刪除確認，預設不醒目確認鍵）。僅雙按鈕（closeText 非 null）時有意義。
        /// </summary>
        public GamepadMessageDialog(XamlRoot xamlRoot,
                                    string title, string body, string primaryText, string? closeText,
                                    bool defaultToPrimary = false)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            Title = title;
            BodyText.Text = body;

            if (closeText == null)
            {
                CloseButtonText = primaryText;
                CloseButtonClick += (s, e) => Result = true;
            }
            else
            {
                PrimaryButtonText = primaryText;
                CloseButtonText = closeText;
                PrimaryButtonClick += (s, e) => Result = true;
                if (defaultToPrimary)
                {
                    DefaultButton = ContentDialogButton.Primary;
                    Opened += OnOpened;
                }
            }
            // 手把導航（A=觸發焦點元素、B=關閉）由 GamepadDialogBase 基底類別自動提供。
        }

        /// <summary>
        /// defaultToPrimary 時，排進 dispatcher 佇列等樣板套用後，把焦點設到樣板的 Primary 鈕。
        /// 純訊息方塊單次 Focus 即可、不需 GotFocus 守衛（守衛僅貓又雙欄清單 ChangeKey/ManageLanguages 需要）。
        /// </summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
                (GetTemplateChild("PrimaryButton") as Control)?.Focus(FocusStateHelper.Preferred));
        }
    }
}
