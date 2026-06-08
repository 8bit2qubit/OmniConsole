using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Models;
using OmniConsole.Services;
using System.Collections.Generic;
using Windows.ApplicationModel.Resources;

namespace OmniConsole.Dialogs
{
    /// <summary>「從其他程式讀入」對話：選一個現有 profile，回傳其 AppId 供呼叫端複製 bindings。</summary>
    public sealed partial class CopyFromProfileDialog : GamepadDialog
    {
        private readonly ResourceLoader _resw;

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

        /// <summary>對話方塊開啟：設定初始焦點到 ProfileCombo（排 dispatcher 待佈局完成，避免被框架焦點操作搶回）。手把導航由 GamepadDialog 基底類別自動提供。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() => ProfileCombo.Focus(FocusStateHelper.Preferred));
        }
    }
}
