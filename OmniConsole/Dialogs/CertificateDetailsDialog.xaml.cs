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
    public sealed partial class CertificateDetailsDialog : ContentDialog
    {
        private GamepadNavigationService? _gamepadNav;
        private readonly string _thumbprint;

        /// <summary>建構：接收指紋（冒號分隔大寫）、設定按鈕文字、掛上事件。Source URL 指向官方 repo。</summary>
        public CertificateDetailsDialog(XamlRoot xamlRoot, string thumbprint)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _thumbprint = thumbprint ?? string.Empty;

            var loader = new ResourceLoader();
            Title = loader.GetString("CertDetailsDialog_Title");
            PrimaryButtonText = loader.GetString("CertDetailsDialog_CopyButton");
            CloseButtonText = loader.GetString("CertDetailsDialog_CloseButton");

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
            Closed += OnClosed;
        }

        /// <summary>對話方塊開啟：啟動自帶手把輪詢（A=觸發焦點元素、B=關閉）。初始焦點由 XAML DefaultButton="Close" 控制。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: () => Hide());
            _gamepadNav.Start();
        }

        /// <summary>對話方塊關閉：停止手把輪詢並釋放。</summary>
        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
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
