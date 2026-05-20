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
    public sealed partial class ChangeKeyDialog : ContentDialog
    {
        private readonly ResourceLoader _resw;
        private readonly bool _isCombo;
        private GamepadNavigationService? _gamepadNav;

        /// <summary>使用者按下確定後的結果；取消則為 null。</summary>
        public GamepadAction? Result { get; private set; }

        /// <summary>建立改鍵對話方塊；current 為現值（用於預設選取），isCombo=true 顯示 modifier toggle。</summary>
        public ChangeKeyDialog(XamlRoot xamlRoot, ResourceLoader resw, GamepadAction current, bool isCombo)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resw = resw;
            _isCombo = isCombo;

            Title = Loc(isCombo ? "GamepadMappingChangeComboTitle" : "GamepadMappingChangeKeyTitle");
            PrimaryButtonText = Loc("GamepadKeyPickerOk");
            CloseButtonText = Loc("GamepadKeyPickerCancel");

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
            Closed += OnClosed;
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

        /// <summary>對話方塊開啟：啟動自帶手把輪詢（A=觸發焦點元素、B=取消關閉），預設焦點到 KeyCombo。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: () => Hide());
            _gamepadNav.Start();
            KeyCombo.Focus(FocusState.Programmatic);
        }

        /// <summary>對話方塊關閉：停止手把輪詢並釋放。</summary>
        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
        }

        /// <summary>取 VK 條目的顯示名稱（resw → FallbackText 兩段回退）。</summary>
        private string KeyEntryName(VirtualKeyEntry e)
        {
            if (!string.IsNullOrEmpty(e.ReswKey))
            {
                string s = Loc(e.ReswKey);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return e.FallbackText;
        }

        /// <summary>取分組名稱（resw → enum.ToString 兩段回退）。</summary>
        private string GroupName(VirtualKeyGroup g)
        {
            string key = "GamepadKeyGroup_" + g.ToString();
            string s = Loc(key);
            return string.IsNullOrEmpty(s) ? g.ToString() : s;
        }

        /// <summary>resw 查詢；依 key 本身 / .Text / .Content 三候選試查，皆查不到時回 key 字面，try/catch 包住例外。</summary>
        private string Loc(string key)
        {
            string[] candidates = { key, key + "/Text", key + "/Content" };
            foreach (var c in candidates)
            {
                try
                {
                    var s = _resw.GetString(c);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            return key;
        }
    }
}
