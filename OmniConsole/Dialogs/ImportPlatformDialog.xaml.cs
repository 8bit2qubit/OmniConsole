using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 讓使用者貼上 JSON 字串以匯入自訂平台的對話方塊。
    /// 驗證通過後將結果存入 <see cref="ResultEntry"/>，由呼叫端寫入 UserPlatformStore。
    /// </summary>
    public sealed partial class ImportPlatformDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>B 鍵不動作，避免誤觸關閉對話方塊遺失輸入（回 false = 不處理、不關閉）。</summary>
        public override bool OnB() => false;

        /// <summary>含 TextBox，啟用螢幕鍵盤閃避。</summary>
        protected override bool EnableKeyboardAvoidanceOnOpen => true;

        /// <summary>驗證通過後建立的平台項目，由呼叫端寫入 UserPlatformStore。</summary>
        public UserPlatformEntry? ResultEntry { get; private set; }

        /// <summary>
        /// 建立匯入平台對話方塊。
        /// </summary>
        /// <param name="xamlRoot">呼叫端視窗的 XamlRoot，ContentDialog 顯示所需。</param>
        public ImportPlatformDialog(XamlRoot xamlRoot)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            Title = _resourceLoader.Loc("ImportPlatformDialog_Title");
            SecurityWarning.Text = _resourceLoader.Loc("ImportPlatformDialog_Warning");
            JsonInputBox.PlaceholderText = _resourceLoader.Loc("ImportPlatformDialog_Placeholder");
            PrimaryButtonText = _resourceLoader.Loc("ImportPlatformDialog_Primary");
            CloseButtonText = _resourceLoader.Loc("PlatformDialog_Cancel");

            PrimaryButtonClick += ImportPlatformDialog_PrimaryButtonClick;
            Opened += ImportPlatformDialog_Opened;
        }

        private void ImportPlatformDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // D-pad 向下離開內容區時，優先跳到「匯入」(Primary) 而非「取消」(Close)
            if (Content is FrameworkElement contentRoot
                && GetTemplateChild("PrimaryButton") is Button primaryButton)
            {
                contentRoot.XYFocusDown = primaryButton;
            }
            // 顯式把初始焦點設到內容區第一個可聚焦元素（JsonInputBox）
            DispatcherQueue.TryEnqueue(() =>
                JsonInputBox.Focus(FocusStateHelper.Preferred));
            // 手把導航（A=觸發焦點元素、B 不動作）與螢幕鍵盤閃避由 GamepadDialogBase 基底類別自動提供。
        }

        /// <summary>
        /// 「匯入」按鈕點選時呼叫 <see cref="UserPlatformShareService.ImportAsync"/> 驗證 JSON。
        /// 驗證失敗時顯示錯誤訊息並取消關閉；通過後存入 <see cref="ResultEntry"/>。
        /// </summary>
        private async void ImportPlatformDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var (entry, errorKey) = await UserPlatformShareService.ImportAsync(JsonInputBox.Text);

                if (errorKey is not null)
                {
                    ErrorText.Text = _resourceLoader.Loc(errorKey);
                    ErrorText.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
                else
                {
                    ResultEntry = entry;
                    ErrorText.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
