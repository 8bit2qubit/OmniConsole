using Microsoft.UI.Xaml;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 手把映射編輯流程通用的訊息／確認對話方塊（刪除確認 / 全 None 確認 / 黑名單提示等共用）。
    /// </summary>
    public sealed partial class GamepadMessageDialog : GamepadDialog
    {
        /// <summary>使用者按下 primary 鈕（確定／是）後為 true；否則為 false。</summary>
        public bool Result { get; private set; }

        /// <summary>建立訊息／確認對話方塊；closeText 傳 null 表示「純資訊單按鈕」，primaryText 當關閉鈕字樣。
        /// defaultToPrimary=true 時預設醒目 Primary 鈕（如「立即重啟」方便直接確認）；預設 false＝醒目 Close
        /// （危險操作如刪除確認，預設不 highlight 確認鍵）。僅雙按鈕（closeText 非 null）時有意義。</summary>
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
                if (defaultToPrimary) DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary;
            }
            // 手把導航（A=觸發焦點元素、B=關閉）由 GamepadDialog 基底類別自動提供；
            // 初始焦點預設由 XAML DefaultButton="Close" 控制，defaultToPrimary 時改醒目 Primary。
        }
    }
}
