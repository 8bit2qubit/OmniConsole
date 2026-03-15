using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 讓使用者貼上 JSON 字串以匯入自訂平台的對話方塊。
    /// 驗證通過後將結果存入 <see cref="ResultEntry"/>，由呼叫端寫入 UserPlatformStore。
    /// </summary>
    public sealed partial class ImportPlatformDialog : ContentDialog
    {
        private readonly ResourceLoader _resourceLoader;
        private GamepadNavigationService? _gamepadNav;
        private Action? _keyboardAvoidanceCleanup;

        /// <summary>驗證通過後建立的平台項目，由呼叫端寫入 UserPlatformStore。</summary>
        public UserPlatformEntry? ResultEntry { get; private set; }

        /// <summary>
        /// 建立匯入平台對話方塊。
        /// </summary>
        /// <param name="xamlRoot">呼叫端視窗的 XamlRoot，ContentDialog 顯示所需。</param>
        /// <param name="resourceLoader">在地化字串載入器。</param>
        public ImportPlatformDialog(XamlRoot xamlRoot, ResourceLoader resourceLoader)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resourceLoader = resourceLoader;

            Title = resourceLoader.GetString("ImportPlatformDialog_Title");
            SecurityWarning.Text = resourceLoader.GetString("ImportPlatformDialog_Warning");
            JsonInputBox.PlaceholderText = resourceLoader.GetString("ImportPlatformDialog_Placeholder");
            PrimaryButtonText = resourceLoader.GetString("ImportPlatformDialog_Primary");
            CloseButtonText = resourceLoader.GetString("PlatformDialog_Cancel");

            PrimaryButtonClick += ImportPlatformDialog_PrimaryButtonClick;
            Opened += ImportPlatformDialog_Opened;
            Closed += ImportPlatformDialog_Closed;
        }

        private void ImportPlatformDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // D-pad 向下離開內容區時，優先跳到「匯入」(Primary) 而非「取消」(Close)
            if (Content is FrameworkElement contentRoot
                && GetTemplateChild("PrimaryButton") is Button primaryButton)
            {
                contentRoot.XYFocusDown = primaryButton;
            }

            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: null);   // B 鍵不動作，避免誤觸關閉對話方塊遺失輸入
            _gamepadNav.Start();

            // 螢幕鍵盤彈出時自動將對話方塊上移，避免鍵盤遮蓋內容
            _keyboardAvoidanceCleanup = GamepadNavigationService.EnableKeyboardAvoidance(
                GetTemplateChild("BackgroundElement") as FrameworkElement, XamlRoot);
        }

        private void ImportPlatformDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
            _keyboardAvoidanceCleanup?.Invoke();
            _keyboardAvoidanceCleanup = null;
        }

        /// <summary>
        /// 「匯入」按鈕點選時呼叫 <see cref="UserPlatformShareService.Import"/> 驗證 JSON。
        /// 驗證失敗時顯示錯誤訊息並取消關閉；通過後存入 <see cref="ResultEntry"/>。
        /// </summary>
        private void ImportPlatformDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();

            var (entry, errorKey) = UserPlatformShareService.Import(JsonInputBox.Text);

            if (errorKey is not null)
            {
                ErrorText.Text = _resourceLoader.GetString(errorKey);
                ErrorText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            else
            {
                ResultEntry = entry;
                ErrorText.Visibility = Visibility.Collapsed;
            }

            deferral.Complete();
        }
    }
}
