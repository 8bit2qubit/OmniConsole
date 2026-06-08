using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Models;
using OmniConsole.Services;
using Windows.ApplicationModel.Resources;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 「改鍵」對話：選擇單一 VK（KeyTap / KeyHold）或修飾鍵組合 + VK（KeyCombo）。
    /// </summary>
    public sealed partial class ChangeKeyDialog : GamepadDialog
    {
        private readonly ResourceLoader _resw;
        private readonly bool _isCombo;

        /// <summary>使用者按下確定後的結果；取消則為 null。</summary>
        public GamepadAction? Result { get; private set; }

        /// <summary>建立改鍵對話方塊；current 為現值（用於預設選取），isCombo=true 顯示 modifier toggle。</summary>
        public ChangeKeyDialog(XamlRoot xamlRoot, ResourceLoader resw, GamepadAction current, bool isCombo)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resw = resw;
            _isCombo = isCombo;

            Title = _resw.Loc(isCombo ? "GamepadMappingChangeComboTitle" : "GamepadMappingChangeKeyTitle");
            PrimaryButtonText = _resw.Loc("GamepadKeyPickerOk");
            CloseButtonText = _resw.Loc("GamepadKeyPickerCancel");

            ModifiersPanel.Visibility = isCombo ? Visibility.Visible : Visibility.Collapsed;
            if (isCombo)
            {
                ModCtrl.IsChecked = (current.Mods & GamepadModifier.Ctrl) != 0;
                ModShift.IsChecked = (current.Mods & GamepadModifier.Shift) != 0;
                ModAlt.IsChecked = (current.Mods & GamepadModifier.Alt) != 0;
                ModWin.IsChecked = (current.Mods & GamepadModifier.Win) != 0;
            }

            PopulateKeyCombo(current.Vk);
            PrimaryButtonClick += OnPrimary;
            Opened += OnOpened;
        }

        /// <summary>填 KeyCombo：每組先加分組標題（IsEnabled=false、Tag=null），再加該組各 VK（Tag=Vk）；組合鍵跳過 Modifiers 分組。</summary>
        private void PopulateKeyCombo(int currentVk)
        {
            int? selectIdx = null;
            int firstSelectable = -1;
            int idx = 0;
            VirtualKeyGroup? lastGroup = null;
            foreach (var entry in VirtualKeys.All)
            {
                if (_isCombo && entry.Group == VirtualKeyGroup.Modifiers) continue;

                if (lastGroup != entry.Group)
                {
                    var header = new ComboBoxItem
                    {
                        Content = GroupName(entry.Group),
                        IsEnabled = false,
                        Tag = null
                    };
                    KeyCombo.Items.Add(header);
                    idx++;
                    lastGroup = entry.Group;
                }

                var item = new ComboBoxItem
                {
                    Content = KeyEntryName(entry),
                    Tag = entry.Vk
                };
                KeyCombo.Items.Add(item);
                if (firstSelectable < 0) firstSelectable = idx;
                if (entry.Vk == currentVk) selectIdx = idx;
                idx++;
            }
            KeyCombo.SelectedIndex = selectIdx ?? (firstSelectable >= 0 ? firstSelectable : 0);
        }

        /// <summary>確定鈕：依 isCombo 收集修飾鍵 + 選到的 VK → 寫到 Result；未選到主鍵則取消提交。</summary>
        private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (KeyCombo.SelectedItem is not ComboBoxItem item || item.Tag is not int vk)
            {
                args.Cancel = true;
                return;
            }

            if (_isCombo)
            {
                var mods = GamepadModifier.None;
                if (ModCtrl.IsChecked == true) mods |= GamepadModifier.Ctrl;
                if (ModShift.IsChecked == true) mods |= GamepadModifier.Shift;
                if (ModAlt.IsChecked == true) mods |= GamepadModifier.Alt;
                if (ModWin.IsChecked == true) mods |= GamepadModifier.Win;
                Result = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = vk, Mods = mods };
            }
            else
            {
                var entry = VirtualKeys.FindByVk(vk);
                bool isModifier = entry != null && entry.Group == VirtualKeyGroup.Modifiers;
                Result = new GamepadAction
                {
                    Kind = isModifier ? GamepadActionKind.KeyHold : GamepadActionKind.KeyTap,
                    Vk = vk
                };
            }
        }

        /// <summary>對話方塊開啟：設定初始焦點到 KeyCombo（排 dispatcher 待佈局完成，避免被框架焦點操作搶回）。手把導航由 GamepadDialog 基底類別自動提供。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() => KeyCombo.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>取 VK 條目的顯示名稱（resw → FallbackText 兩段回退）。</summary>
        private string KeyEntryName(VirtualKeyEntry e)
        {
            if (!string.IsNullOrEmpty(e.ReswKey))
            {
                string s = _resw.Loc(e.ReswKey);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return e.FallbackText;
        }

        /// <summary>取分組名稱（resw → enum.ToString 兩段回退）。</summary>
        private string GroupName(VirtualKeyGroup g)
        {
            string key = "GamepadKeyGroup_" + g.ToString();
            string s = _resw.Loc(key);
            return string.IsNullOrEmpty(s) ? g.ToString() : s;
        }

    }
}
