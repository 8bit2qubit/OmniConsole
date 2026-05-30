using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 手把映射編輯流程通用的訊息／確認對話方塊（刪除確認 / 全 None 確認 / 黑名單提示等共用）。
    /// </summary>
    public sealed partial class GamepadMessageDialog : ContentDialog
    {
        private GamepadNavigationService? _gamepadNav;

        /// <summary>使用者按下 primary 鈕（確定／是）後為 true；否則為 false。</summary>
        public bool Result { get; private set; }

        /// <summary>建立訊息／確認對話方塊；closeText 傳 null 表示「純資訊單按鈕」，primaryText 當關閉鈕字樣。</summary>
        public GamepadMessageDialog(XamlRoot xamlRoot,
                                    string title, string body, string primaryText, string? closeText)
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
            }
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
    }
}
