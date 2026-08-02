using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.IO;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 輸入或匯入 Pro 授權的對話方塊。
    /// 檔案匯入只是把授權檔的金鑰填進輸入方塊，驗證與提交仍走同一條路徑。
    /// </summary>
    public sealed partial class ProLicenseDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>本次是否由檔案選擇器返回後重開，決定初始焦點落在何處。</summary>
        private bool _reopenedFromFile;

        /// <summary>B 鍵不動作，避免誤觸關閉對話方塊遺失已貼上的金鑰（回 false = 不處理、不關閉）。</summary>
        public override bool OnB() => false;

        /// <summary>含 TextBox，啟用螢幕鍵盤閃避。</summary>
        protected override bool EnableKeyboardAvoidanceOnOpen => true;

        /// <summary>啟用成功的授權內容；未成功時為 null。</summary>
        public LicenseService.LicenseInfo? ResultInfo { get; private set; }

        /// <summary>是否需要呼叫端開啟檔案選擇器（匯入按鈕觸發 Hide 時設為 true）。</summary>
        public bool RequestFilePicker { get; private set; }

        /// <summary>檔案選擇器的設定選項。</summary>
        public FilePickerOptions? FilePickerRequest { get; private set; }

        /// <summary>建立授權輸入對話方塊。</summary>
        /// <param name="xamlRoot">呼叫端視窗的 XamlRoot，ContentDialog 顯示所需。</param>
        public ProLicenseDialog(XamlRoot xamlRoot)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            Title = _resourceLoader.Loc("ProLicenseDialog_Title");
            HintText.Text = _resourceLoader.Loc("ProLicenseDialog_Hint");
            KeyInputBox.PlaceholderText = _resourceLoader.Loc("ProLicenseDialog_Placeholder");
            ImportFileButton.Content = _resourceLoader.Loc("ProLicenseDialog_ImportFile");
            PrimaryButtonText = _resourceLoader.Loc("ProLicenseDialog_Primary");
            CloseButtonText = _resourceLoader.Loc("ProLicenseDialog_Cancel");

            PrimaryButtonClick += ProLicenseDialog_PrimaryButtonClick;
            Opened += ProLicenseDialog_Opened;
        }

        /// <summary>設定離開內容區的向下焦點目標，並把初始焦點放到本次該落的元素上。</summary>
        private void ProLicenseDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // D-pad 向下離開內容區時，優先跳到「啟用」(Primary) 而非「取消」(Close)
            if (Content is FrameworkElement contentRoot
                && GetTemplateChild("PrimaryButton") is Button primaryButton)
            {
                contentRoot.XYFocusDown = primaryButton;

                // 從檔案選擇器返回時內容已備妥，下一步就是按啟用，故焦點直接落在該鈕上。
                if (_reopenedFromFile)
                {
                    _reopenedFromFile = false;
                    DispatcherQueue.TryEnqueue(() =>
                        primaryButton.Focus(FocusStateHelper.Preferred));
                    return;
                }
            }

            DispatcherQueue.TryEnqueue(() =>
                KeyInputBox.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>要求呼叫端開啟檔案選擇器並關閉自己，待選取結果回來後由呼叫端重新開啟。</summary>
        private void ImportFileButton_Click(object sender, RoutedEventArgs e)
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

            FilePickerRequest = new FilePickerOptions
            {
                FileTypeFilters = [".lic"],
                InitialDirectory = Directory.Exists(downloads) ? downloads : null,
                ShowImagePreview = false,
                FilterDisplayName = _resourceLoader.Loc("ProLicense_FileFilter"),
            };
            RequestFilePicker = true;
            Hide();
        }

        /// <summary>
        /// 接收檔案選擇器的結果：讀得到金鑰就填進輸入方塊，讀不到則顯示錯誤。
        /// 傳入 null 代表使用者取消選取，保留原本的輸入內容。
        /// </summary>
        public void ApplyFilePickerResult(string? filePath)
        {
            RequestFilePicker = false;
            FilePickerRequest = null;
            _reopenedFromFile = true;

            if (filePath == null) return;

            var (keyText, error) = LicenseService.TryReadKeyFromFile(filePath);
            if (keyText == null)
            {
                ShowError(error);
                return;
            }

            KeyInputBox.Text = keyText;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        /// <summary>驗證輸入的金鑰；失敗時顯示原因並取消關閉，成功則存入結果。</summary>
        private void ProLicenseDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                var (info, error) = LicenseService.TryActivate(KeyInputBox.Text);
                if (info == null)
                {
                    ShowError(error);
                    args.Cancel = true;
                }
                else
                {
                    ResultInfo = info;
                    ErrorText.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>顯示對應錯誤碼的在地化訊息。</summary>
        private void ShowError(LicenseService.LicenseError error)
        {
            ErrorText.Text = _resourceLoader.Loc(LicenseService.ErrorResourceKey(error));
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
