using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System.Collections.Generic;

namespace OmniConsole.Dialogs
{
    /// <summary>左欄分類列（顯示名 + 對應 VK 分組）。</summary>
    internal sealed record KeyCategoryRow(string DisplayName, VirtualKeyGroup Group);

    /// <summary>右欄一個可選 VK 列（顯示名 + VK code）。</summary>
    internal sealed record KeyPickerRow(string DisplayName, int Vk);

    /// <summary>
    /// 「改鍵」對話方塊：選擇單一 VK（KeyTap / KeyHold）或修飾鍵組合 + VK（KeyCombo）。
    /// </summary>
    public sealed partial class ChangeKeyDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();
        private readonly bool _isCombo;

        /// <summary>使用者按下確定後的結果；取消則為 null。</summary>
        public GamepadAction? Result { get; private set; }

        /// <summary>建立改鍵對話方塊；current 為現值（用於預設選取），isCombo=true 顯示 modifier toggle。</summary>
        public ChangeKeyDialog(XamlRoot xamlRoot, GamepadAction current, bool isCombo)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _isCombo = isCombo;

            Title = _resourceLoader.Loc(isCombo ? "GamepadMappingChangeComboTitle" : "GamepadMappingChangeKeyTitle");
            PrimaryButtonText = _resourceLoader.Loc("GamepadKeyPickerOk");
            CloseButtonText = _resourceLoader.Loc("GamepadKeyPickerCancel");

            // 組合鍵模式才顯示「修飾鍵」+「按鍵」兩段標籤（對稱）；純按鍵模式整個對話方塊即選鍵、不需標籤。
            ModifiersPanel.Visibility = isCombo ? Visibility.Visible : Visibility.Collapsed;
            KeyLabel.Visibility = isCombo ? Visibility.Visible : Visibility.Collapsed;
            if (isCombo)
            {
                ModCtrl.IsChecked = (current.Mods & GamepadModifier.Ctrl) != 0;
                ModShift.IsChecked = (current.Mods & GamepadModifier.Shift) != 0;
                ModAlt.IsChecked = (current.Mods & GamepadModifier.Alt) != 0;
                ModWin.IsChecked = (current.Mods & GamepadModifier.Win) != 0;
            }

            BuildCategories();
            CategoryList.SelectionChanged += CategoryList_SelectionChanged;
            CategoryList.ItemClick += CategoryList_ItemClick;
            KeyList.SelectionChanged += KeyList_SelectionChanged;
            KeyList.ItemClick += KeyList_ItemClick;
            PrimaryButtonClick += OnPrimary;
            Opened += OnOpened;

            // 預設選到目前 VK 所屬分類（連帶填右欄、選中該鍵）；找不到則退第一個分類。
            var currentEntry = VirtualKeys.FindByVk(current.Vk);
            int catIdx = currentEntry != null ? IndexOfCategory(currentEntry.Group) : -1;
            _pendingVk = current.Vk;
            CategoryList.SelectedIndex = catIdx >= 0 ? catIdx : 0;
        }

        private readonly List<KeyCategoryRow> _categories = [];
        private List<KeyPickerRow> _keys = [];
        private KeyPickerRow? _selectedRow;
        /// <summary>建構期記住要在右欄預選的 VK，待對應分類填好後套用一次。</summary>
        private int _pendingVk;

        /// <summary>建左欄分類清單：依 VirtualKeys 出現順序去重；組合鍵模式跳過 Modifiers 分組。</summary>
        private void BuildCategories()
        {
            var seen = new HashSet<VirtualKeyGroup>();
            foreach (var entry in VirtualKeys.All)
            {
                if (_isCombo && entry.Group == VirtualKeyGroup.Modifiers) continue;
                if (seen.Add(entry.Group))
                    _categories.Add(new KeyCategoryRow(GroupName(entry.Group), entry.Group));
            }
            CategoryList.ItemsSource = _categories;
        }

        /// <summary>回傳指定分組在左欄分類清單中的索引；找不到回 -1。</summary>
        private int IndexOfCategory(VirtualKeyGroup group)
        {
            for (int i = 0; i < _categories.Count; i++)
                if (_categories[i].Group == group) return i;
            return -1;
        }

        /// <summary>左欄分類變動：用該類 VK 填滿右欄；若有待套用的 _pendingVk 落在此類則選中它、否則選第一鍵。</summary>
        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryList.SelectedItem is not KeyCategoryRow cat) return;

            _keys = new List<KeyPickerRow>();
            KeyPickerRow? selectRow = null;
            foreach (var entry in VirtualKeys.All)
            {
                if (entry.Group != cat.Group) continue;
                var row = new KeyPickerRow(KeyEntryName(entry), entry.Vk);
                _keys.Add(row);
                if (_pendingVk is int pv && entry.Vk == pv) selectRow = row;
            }

            KeyList.ItemsSource = _keys;
            _pendingVk = -1; // 只在初始套用一次；之後換分類選該類第一鍵
            var pick = selectRow ?? (_keys.Count > 0 ? _keys[0] : null);
            if (pick != null)
            {
                KeyList.SelectedItem = pick;
                _selectedRow = pick;
                ScrollIntoViewWhenReady(KeyList, _keys.IndexOf(pick)); // 捲動由基底類別處理
            }
        }

        /// <summary>左欄分類點選（A 鍵/滑鼠）：焦點跳進右欄目前選中的鍵（同檔案選擇器側邊欄點選後焦點進右側清單）。</summary>
        private void CategoryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_selectedRow != null)
                FocusKeyListItem(_selectedRow);
        }

        /// <summary>
        /// 將焦點設到右欄指定 VK 列的容器（捲動 + 聚焦由基底類別處理；
        /// 白框僅在手把/FSE 的 Keyboard 焦點狀態下顯示，桌面滑鼠為 Pointer 不顯屬正常）。
        /// </summary>
        private void FocusKeyListItem(KeyPickerRow row)
        {
            FocusListItemWhenReady(KeyList, _keys.IndexOf(row));
        }

        /// <summary>右欄選取變動：記住目前選取的 VK 列（給 OnPrimary 取值用）。</summary>
        private void KeyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (KeyList.SelectedItem is KeyPickerRow row)
                _selectedRow = row;
        }

        /// <summary>
        /// 右欄項目點選（A 鍵/滑鼠）：點到的鍵已是目前選取的鍵 → 確認退出（同檔案選擇器，
        /// 滑鼠兩下、手把 A 一下，靠事件時序自然成立，勿改動判斷）。
        /// </summary>
        private void KeyList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is KeyPickerRow row && row == KeyList.SelectedItem as KeyPickerRow)
                InvokePrimaryButton();
        }

        /// <summary>以自動化 Invoke 觸發對話方塊的確定按鈕（比照檔案選擇器：二次點選即確認關閉）。</summary>
        private void InvokePrimaryButton()
        {
            if (GetTemplateChild("PrimaryButton") is Button btn)
            {
                var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(btn);
                var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
                    peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                invoke.Invoke();
            }
        }

        /// <summary>確定按鈕：依 isCombo 收集修飾鍵 + 選到的 VK → 寫到 Result；未選到主鍵則取消提交。</summary>
        private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_selectedRow?.Vk is not int vk)
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

        /// <summary>開啟初期焦點是否已落在右欄選中項上（焦點守衛的達標判斷）。</summary>
        private bool IsFocusOnSelectedKey()
        {
            if (_selectedRow == null) return true; // 無選中項則無守衛目標，視為達標
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot);
            var target = KeyList.ContainerFromItem(_selectedRow);
            return target != null && ReferenceEquals(focused, target);
        }

        /// <summary>對話方塊開啟：初始焦點落右欄選中項（靠基底焦點守衛確保）。手把導航由 GamepadDialogBase 基底類別自動提供。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // 掛上焦點守衛：開啟初期焦點被框架搶走時，於守衛時窗內拉回右欄選中項。
            GuardInitialFocus(IsFocusOnSelectedKey, () =>
            {
                if (_selectedRow != null) FocusKeyListItem(_selectedRow);
            });

            DispatcherQueue.TryEnqueue(() =>
            {
                // 兩欄都用「等容器就緒再捲動」（捲動由基底類別處理）。
                if (CategoryList.SelectedItem is KeyCategoryRow cat)
                    ScrollIntoViewWhenReady(CategoryList, _categories.IndexOf(cat));

                // 首次嘗試聚焦右欄選中項（若被框架搶走，守衛會再拉回）。
                if (_selectedRow != null) FocusKeyListItem(_selectedRow);
                else KeyList.Focus(FocusStateHelper.Preferred);
            });
        }

        /// <summary>取 VK 條目的顯示名稱（resw → FallbackText 兩段回退）。</summary>
        private string KeyEntryName(VirtualKeyEntry e)
        {
            if (!string.IsNullOrEmpty(e.ReswKey))
            {
                string s = _resourceLoader.Loc(e.ReswKey);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            return e.FallbackText;
        }

        /// <summary>取分組名稱（resw → enum.ToString 兩段回退）。</summary>
        private string GroupName(VirtualKeyGroup g)
        {
            string key = "GamepadKeyGroup_" + g.ToString();
            string s = _resourceLoader.Loc(key);
            return string.IsNullOrEmpty(s) ? g.ToString() : s;
        }

    }
}
