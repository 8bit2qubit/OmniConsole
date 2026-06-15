using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using Windows.ApplicationModel.DataTransfer;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 顯示套件憑證 SHA-256 指紋與 source URL，提供 Copy 與 Close 兩動作。
    /// 指紋由呼叫端透過建構式傳入（已是冒號分隔大寫格式），Copy 將其原樣寫入剪貼簿。
    /// Source URL 顯示為可點 HyperlinkButton 開啟 GitHub repo 首頁；另有 AUTHENTICITY 連結依語系開對應的真偽說明文件。
    /// </summary>
    public sealed partial class CertificateDetailsDialog : GamepadDialog
    {
        private readonly string _thumbprint;
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>建構：接收指紋（冒號分隔大寫）、設定按鈕文字、掛上事件。Source URL 指向官方 repo。</summary>
        public CertificateDetailsDialog(XamlRoot xamlRoot, string thumbprint)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _thumbprint = thumbprint ?? string.Empty;

            Title = _resourceLoader.Loc("CertDetailsDialog_Title");
            PrimaryButtonText = _resourceLoader.Loc("CertDetailsDialog_CopyButton");
            CloseButtonText = _resourceLoader.Loc("CertDetailsDialog_CloseButton");
            // 含品牌名：由 code-behind 設值（注入品牌）。
            BodyText.Text = _resourceLoader.Loc("CertDetailsDialog_Body");

            ThumbprintText.Text = _thumbprint;

            // ContentDialog 內的 Source 永遠指向官方 repo，讓使用者點選後一定到達正版來源做比對
            const string OfficialRepoUrl = "https://github.com/8bit2qubit/OmniConsole";
            SourceLink.Content = OfficialRepoUrl.Replace("https://", string.Empty);
            SourceLink.NavigateUri = new Uri(OfficialRepoUrl);

            // AUTHENTICITY 連結依語系切換：zh-TW / zh-CN 走繁體版（無簡體 md），其他走英文版
            var lang = Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride;
            if (string.IsNullOrEmpty(lang))
            {
                lang = System.Globalization.CultureInfo.CurrentUICulture.Name;
            }
            var authenticityFile = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "AUTHENTICITY.zh-TW.md"
                : "AUTHENTICITY.md";
            OpenAuthenticityButton.NavigateUri = new Uri(
                $"https://github.com/8bit2qubit/OmniConsole/blob/main/{authenticityFile}");

            PrimaryButtonClick += OnCopyClick;
            Opened += OnOpened;
            // 手把導航（A=觸發焦點元素、B=關閉）由 GamepadDialog 基底類別自動提供。
        }

        /// <summary>對話方塊開啟：顯式把初始焦點設到內容區第一個可聚焦元素（SourceLink），比照其他對話方塊
        /// 「焦點落內容元素、明確可控」的設計，不靠 XAML DefaultButton 框架機制。排 dispatcher 待佈局完成。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() => SourceLink.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>Copy 按下：取消預設關閉、將冒號分隔大寫指紋寫入剪貼簿、顯示「已複製」提示。</summary>
        private void OnCopyClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            args.Cancel = true;

            var pkg = new DataPackage();
            pkg.SetText(_thumbprint);
            Clipboard.SetContent(pkg);

            CopiedHintText.Visibility = Visibility.Visible;
        }
    }
}
