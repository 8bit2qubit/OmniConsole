using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Models;
using OmniConsole.Services;
using System.Collections.Generic;
using Windows.ApplicationModel.Resources;

namespace OmniConsole.Dialogs
{
    /// <summary>「從其他程式讀入」對話：選一個現有 profile，回傳其 AppId 供呼叫端複製 bindings。</summary>
    public sealed partial class CopyFromProfileDialog : ContentDialog
    {
        private readonly ResourceLoader _resw;
        private GamepadNavigationService? _gamepadNav;

        /// <summary>使用者選到的來源 profile AppId；取消為 null。</summary>
        public AppId? SelectedAppId { get; private set; }

        /// <summary>建立讀入對話方塊；others 為「除了目前 profile 外」的其餘 profile 集合。</summary>
        public CopyFromProfileDialog(XamlRoot xamlRoot, ResourceLoader resw, IEnumerable<GamepadProfile> others)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resw = resw;

            Title = _resw.Loc("GamepadMappingCopyFromTitle");
            PrimaryButtonText = _resw.Loc("GamepadKeyPickerOk");
            CloseButtonText = _resw.Loc("GamepadKeyPickerCancel");
            HintText.Text = _resw.Loc("GamepadMappingCopyFromHint");

            foreach (var p in others)
            {
                string label = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : (p.AppId?.Value ?? string.Empty);
                // path-bound profile 後綴接資料夾名稱
                if (p.AppId != null && p.AppId.Kind == IdKind.Process && !string.IsNullOrEmpty(p.AppId.FullPath))
                {
                    string folder = AppId.ExtractFolderName(p.AppId.FullPath);
                    if (!string.IsNullOrEmpty(folder))
                        label = label + " · " + folder;
                }
                ProfileCombo.Items.Add(new ComboBoxItem { Content = label, Tag = p.AppId });
            }
            if (ProfileCombo.Items.Count > 0) ProfileCombo.SelectedIndex = 0;

            PrimaryButtonClick += OnPrimary;
            Opened += OnOpened;
            Closed += OnClosed;
        }

        /// <summary>確定鈕：取選到的 AppId 寫入 SelectedAppId；未選則取消提交。</summary>
        private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (ProfileCombo.SelectedItem is ComboBoxItem item && item.Tag is AppId id)
            {
                SelectedAppId = id;
            }
            else
            {
                args.Cancel = true;
            }
        }

        /// <summary>對話方塊開啟：啟動自帶手把輪詢（A=觸發焦點元素、B=關閉）。初始焦點到 ProfileCombo。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: () => Hide());
            _gamepadNav.Start();
            ProfileCombo.Focus(FocusState.Programmatic);
        }

        /// <summary>對話方塊關閉：停止手把輪詢並釋放。</summary>
        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
        }

    }
}
