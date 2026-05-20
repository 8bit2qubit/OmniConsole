using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

namespace OmniConsole.Controls
{
    /// <summary>
    /// 玩家 per-App 手把 profile 編輯器（16 個 XInput 輸入位 → 動作）。DPad 4 子列依主行選擇條件展開。
    /// </summary>
    public sealed partial class GamepadProfileEditor : UserControl
    {
        // ── 動作選項（ComboBox.Tag）─────────────────────────────────────────
        private enum ActionOption
        {
            None,
            KeyTap,         // 鍵盤按鍵（KeyTap / KeyHold 走同一選項，依 VK 是否為修飾鍵自動分流）
            KeyCombo,       // 組合鍵（Ctrl/Shift/Alt/Win + 鍵）
            MouseLeft,
            MouseRight,
            MouseMiddle,
            WheelUp,
            WheelDown,
            WheelLeft,
            WheelRight,
            StickCursor,
            StickScroll,
            StickArrows,
            StickWasd,
            // DPad 主行專用選項（不對應單一 GamepadInputId，僅 ComboDPad 使用）
            DpadArrows,
            DpadWasd,
            DpadNumpad,
            DpadCustom
        }

        // DPad 預設組合 VK 表（up / down / left / right）
        private static readonly int[] kDpadArrowsVks = { 0x26, 0x28, 0x25, 0x27 }; // VK_UP / DOWN / LEFT / RIGHT
        private static readonly int[] kDpadWasdVks = { 'W', 'S', 'A', 'D' };
        private static readonly int[] kDpadNumpadVks = { 0x68, 0x62, 0x64, 0x66 }; // Numpad 8 / 2 / 4 / 6

        // DPad 4 個 KeyId（順序 up/down/left/right，與 VK 表逐位相符）
        private static readonly GamepadInputId[] kDpadKeys =
        {
            GamepadInputId.DPadUp, GamepadInputId.DPadDown,
            GamepadInputId.DPadLeft, GamepadInputId.DPadRight
        };

        // 玩家手動切到「自訂」後即使 4 子列湊巧等價於某預設組合，主行維持「自訂」
        private bool _dpadEditingCustom = false;

        private readonly ResourceLoader _resw = ResourceLoader.GetForViewIndependentUse();
        private Dictionary<GamepadInputId, (ComboBox combo, Button? keyBtn)> _rows = new();
        private GamepadProfile? _editing;
        private bool _isNew;

        /// <summary>編輯器存檔／取消後通知宿主關閉（CloseEditor）。</summary>
        public event EventHandler? Closed;

        /// <summary>使用者按底部 (X) 刪除確認後通知宿主關閉。</summary>
        public event EventHandler? Deleted;

        /// <summary>子對話方塊開啟前 true、關閉後 false（宿主據此 Stop/StartGamepadPolling）。</summary>
        public event EventHandler<bool>? DialogActiveChanged;

        /// <summary>建構子：建立 row 對照表並對每個 ComboBox 填入合法動作選項。</summary>
        public GamepadProfileEditor()
        {
            InitializeComponent();
            BuildRows();
        }

        /// <summary>建 _rows 對照表：14 個按鈕類列 + 2 個搖桿列 = 16 個（DPad 4 子列各帶 KeyBtn）；DPad 主行 ComboDPad 另外處理。</summary>
        private void BuildRows()
        {
            _rows = new Dictionary<GamepadInputId, (ComboBox, Button?)>
            {
                [GamepadInputId.A] = (ComboA, KeyBtnA),
                [GamepadInputId.B] = (ComboB, KeyBtnB),
                [GamepadInputId.X] = (ComboX, KeyBtnX),
                [GamepadInputId.Y] = (ComboY, KeyBtnY),
                [GamepadInputId.LB] = (ComboLB, KeyBtnLB),
                [GamepadInputId.RB] = (ComboRB, KeyBtnRB),
                [GamepadInputId.LT] = (ComboLT, KeyBtnLT),
                [GamepadInputId.RT] = (ComboRT, KeyBtnRT),
                [GamepadInputId.LS] = (ComboLS, KeyBtnLS),
                [GamepadInputId.RS] = (ComboRS, KeyBtnRS),
                [GamepadInputId.DPadUp] = (ComboDPadUp, KeyBtnDPadUp),
                [GamepadInputId.DPadDown] = (ComboDPadDown, KeyBtnDPadDown),
                [GamepadInputId.DPadLeft] = (ComboDPadLeft, KeyBtnDPadLeft),
                [GamepadInputId.DPadRight] = (ComboDPadRight, KeyBtnDPadRight),
                [GamepadInputId.LStick] = (ComboLStick, null),
                [GamepadInputId.RStick] = (ComboRStick, null),
            };
            foreach (var kv in _rows)
                PopulateActionCombo(kv.Value.combo, kv.Key);

            // DPad 主行 ComboBox 另外填預設組合選項
            PopulateDPadMainCombo();
        }

        /// <summary>對某輸入位填入合法動作選項（按鈕類含 DPad 4 子列；搖桿類獨立）。</summary>
        private void PopulateActionCombo(ComboBox combo, GamepadInputId id)
        {
            combo.Items.Clear();
            switch (id)
            {
                case GamepadInputId.LStick:
                case GamepadInputId.RStick:
                    Add(combo, ActionOption.StickCursor, "GamepadAction_StickCursor");
                    Add(combo, ActionOption.StickScroll, "GamepadAction_StickScroll");
                    Add(combo, ActionOption.StickArrows, "GamepadAction_StickArrows");
                    Add(combo, ActionOption.StickWasd, "GamepadAction_StickWasd");
                    Add(combo, ActionOption.None, "GamepadAction_None");
                    break;
                default:
                    // 按鈕類（A..RS + DPad 4 子列）：KeyTap / KeyCombo / Mouse（三種）/ Wheel（四向）/ None
                    Add(combo, ActionOption.KeyTap, "GamepadAction_KeyTap");
                    Add(combo, ActionOption.KeyCombo, "GamepadAction_KeyCombo");
                    Add(combo, ActionOption.MouseLeft, "GamepadAction_MouseLeft");
                    Add(combo, ActionOption.MouseRight, "GamepadAction_MouseRight");
                    Add(combo, ActionOption.MouseMiddle, "GamepadAction_MouseMiddle");
                    Add(combo, ActionOption.WheelUp, "GamepadAction_WheelUp");
                    Add(combo, ActionOption.WheelDown, "GamepadAction_WheelDown");
                    Add(combo, ActionOption.WheelLeft, "GamepadAction_WheelLeft");
                    Add(combo, ActionOption.WheelRight, "GamepadAction_WheelRight");
                    Add(combo, ActionOption.None, "GamepadAction_None");
                    break;
            }
        }

        /// <summary>填入 DPad 主行 ComboBox 的 5 個選項（Arrows / WASD / Numpad / Custom / None）。</summary>
        private void PopulateDPadMainCombo()
        {
            ComboDPad.Items.Clear();
            Add(ComboDPad, ActionOption.DpadArrows, "GamepadAction_DpadArrows");
            Add(ComboDPad, ActionOption.DpadWasd, "GamepadAction_DpadWasd");
            Add(ComboDPad, ActionOption.DpadNumpad, "GamepadAction_DpadNumpad");
            Add(ComboDPad, ActionOption.DpadCustom, "GamepadAction_DpadCustom");
            Add(ComboDPad, ActionOption.None, "GamepadAction_None");
        }

        /// <summary>對 ComboBox 加一個動作選項（Tag=ActionOption、Content=resw 顯示名稱）。</summary>
        private void Add(ComboBox combo, ActionOption opt, string reswKey)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = Loc(reswKey),
                Tag = opt
            });
        }

        // ── 對外狀態 ────────────────────────────────────────────────────────

        /// <summary>是否可刪除（既有 profile = true；新建中 = false）。</summary>
        public bool CanDelete => _editing != null && !_isNew;

        // ── 載入 / 重新整理 ───────────────────────────────────────────────

        /// <summary>載入或新建某 App 的 profile：既有 → 拷貝；新建 → 套 OmniNav 當底。</summary>
        public void Load(AppId appId, string displayName)
        {
            var existing = GamepadProfileStore.Find(appId);
            _isNew = existing == null;
            _editing = existing?.Clone() ?? new GamepadProfile
            {
                AppId = new AppId { Kind = appId.Kind, Value = appId.Value, FullPath = appId.FullPath },
                DisplayName = displayName,
                Bindings = GamepadBuiltInLayouts.OmniNav()
            };
            if (!_isNew && !string.IsNullOrEmpty(displayName))
                _editing.DisplayName = displayName;

            // protocol / widget 帶進來的 path 寫入未綁定路徑的 _editing；已 path-bound profile 不被覆寫
            if (appId.Kind == IdKind.Process
                && !string.IsNullOrEmpty(appId.FullPath)
                && string.IsNullOrEmpty(_editing.AppId.FullPath))
            {
                _editing.AppId.FullPath = appId.FullPath;
            }

            AppNameText.Text = !string.IsNullOrEmpty(_editing.DisplayName) ? _editing.DisplayName : (_editing.AppId.Value ?? string.Empty);
            AppIdText.Text = AppIdSubtitle(_editing.AppId);

            RefreshPathDisplay();

            // CopyFrom 只在有其他 profile 時 enable（撞名不同 path 視為不同 profile，仍可互相 CopyFrom）
            try
            {
                var others = GamepadProfileStore.Load().Where(p => !IsSameProfileSlot(p.AppId, _editing.AppId)).ToList();
                CopyFromButton.IsEnabled = others.Count > 0;
            }
            catch
            {
                CopyFromButton.IsEnabled = false;
            }

            if (_rows.Count == 0) BuildRows();

            // 載入既存 profile：依 model 偵測主行 DPad 模式，Custom 則自動展開
            _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);

            RefreshAllRows();
        }

        /// <summary>把 _editing 的所有 binding 同步回 UI（ComboBox 選項 + KeyBtn 顯示）。</summary>
        private void RefreshAllRows()
        {
            if (_editing == null) return;
            foreach (var kv in _rows)
                SyncRowFromModel(kv.Key, kv.Value.combo, kv.Value.keyBtn);
            SyncDPadMainFromModel();
        }

        /// <summary>偵測 4 個 DPad KeyId 的 VK 是否等價於某預設組合，回對應主行 ActionOption。</summary>
        private ActionOption DetectDPadModeFromModel()
        {
            if (_editing == null) return ActionOption.DpadArrows;

            // 全部 None → 主行顯示 None
            bool allNone = true;
            for (int i = 0; i < 4; i++)
                if (_editing.Get(kDpadKeys[i]).Kind != GamepadActionKind.None) { allNone = false; break; }
            if (allNone) return ActionOption.None;

            // 比對三組預設：4 鍵都 KeyTap 且 vk 逐位相符
            if (MatchDpadVks(kDpadArrowsVks)) return ActionOption.DpadArrows;
            if (MatchDpadVks(kDpadWasdVks)) return ActionOption.DpadWasd;
            if (MatchDpadVks(kDpadNumpadVks)) return ActionOption.DpadNumpad;

            return ActionOption.DpadCustom;
        }

        /// <summary>檢查 4 個 DPad KeyId 是否都是 KeyTap 且 VK 與 expected 逐位相符。</summary>
        private bool MatchDpadVks(int[] expected)
        {
            if (_editing == null) return false;
            for (int i = 0; i < 4; i++)
            {
                var a = _editing.Get(kDpadKeys[i]);
                if (a.Kind != GamepadActionKind.KeyTap) return false;
                if (a.Vk != expected[i]) return false;
            }
            return true;
        }

        /// <summary>把 model 內 DPad 4 鍵的目前狀態同步到主行 ComboBox + 子列 Visibility（過程中暫時解除 SelectionChanged 事件處理器）。</summary>
        private void SyncDPadMainFromModel()
        {
            if (_editing == null) return;
            var detected = DetectDPadModeFromModel();

            ActionOption opt = _dpadEditingCustom ? ActionOption.DpadCustom : detected;

            int idx = FindOptionIndex(ComboDPad, opt);
            if (idx < 0) idx = 0;

            ComboDPad.SelectionChanged -= ComboDPad_SelectionChanged;
            ComboDPad.SelectedIndex = idx;
            ComboDPad.SelectionChanged += ComboDPad_SelectionChanged;

            UpdateDPadExpandedVisibility(opt);
        }

        /// <summary>主行為 Custom 時展開 4 子列，否則收起。</summary>
        private void UpdateDPadExpandedVisibility(ActionOption mainOpt)
        {
            var v = (mainOpt == ActionOption.DpadCustom) ? Visibility.Visible : Visibility.Collapsed;
            DPadCustomRowUp.Visibility = v;
            DPadCustomRowDown.Visibility = v;
            DPadCustomRowLeft.Visibility = v;
            DPadCustomRowRight.Visibility = v;
        }

        /// <summary>主行 ComboBox 變動：依新選擇覆寫 4 個 DPad KeyId、切換展開區。</summary>
        private void ComboDPad_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_editing == null) return;
            if (ComboDPad.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not ActionOption opt) return;

            switch (opt)
            {
                case ActionOption.DpadArrows: ApplyDPadPresetVks(kDpadArrowsVks); _dpadEditingCustom = false; break;
                case ActionOption.DpadWasd: ApplyDPadPresetVks(kDpadWasdVks); _dpadEditingCustom = false; break;
                case ActionOption.DpadNumpad: ApplyDPadPresetVks(kDpadNumpadVks); _dpadEditingCustom = false; break;
                case ActionOption.None: ApplyDPadAllNone(); _dpadEditingCustom = false; break;
                case ActionOption.DpadCustom: ApplyDPadAllNone(); _dpadEditingCustom = true; break;
            }

            // 4 子列 model 變了 → 同步 UI；主行自身依目前狀態切展開
            foreach (var k in kDpadKeys)
                if (_rows.TryGetValue(k, out var pair))
                    SyncRowFromModel(k, pair.combo, pair.keyBtn);
            UpdateDPadExpandedVisibility(opt);
        }

        /// <summary>把 4 個 DPad KeyId 各自寫入 expected[] 對應 VK 的 KeyTap 動作。</summary>
        private void ApplyDPadPresetVks(int[] vks)
        {
            if (_editing == null) return;
            for (int i = 0; i < 4; i++)
                _editing.Bindings[kDpadKeys[i]] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = vks[i] };
        }

        /// <summary>把 4 個 DPad KeyId 全部清為 None（自訂模式起點、主行選 None 時亦走此路徑）。</summary>
        private void ApplyDPadAllNone()
        {
            if (_editing == null) return;
            foreach (var k in kDpadKeys)
                _editing.Bindings[k] = new GamepadAction { Kind = GamepadActionKind.None };
        }

        /// <summary>從 model 同步單列 UI（過程中暫時解除 SelectionChanged 事件處理器）。</summary>
        private void SyncRowFromModel(GamepadInputId id, ComboBox combo, Button? keyBtn)
        {
            if (_editing == null) return;
            var a = _editing.Get(id);
            var opt = ToOption(a, id);

            int idx = FindOptionIndex(combo, opt);
            if (idx < 0) idx = 0;

            combo.SelectionChanged -= ActionCombo_SelectionChanged;
            combo.SelectedIndex = idx;
            combo.SelectionChanged += ActionCombo_SelectionChanged;

            UpdateKeyButton(id, opt, keyBtn);
        }

        /// <summary>找出 ComboBox 內 Tag 為 opt 的 index；未找到回 -1。</summary>
        private int FindOptionIndex(ComboBox combo, ActionOption opt)
        {
            for (int i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i] is ComboBoxItem item && item.Tag is ActionOption o && o == opt) return i;
            return -1;
        }

        /// <summary>更新「改鍵」按鈕：KeyTap / KeyCombo 顯示目前鍵名、其他隱藏。</summary>
        private void UpdateKeyButton(GamepadInputId id, ActionOption opt, Button? keyBtn)
        {
            if (keyBtn == null) return;
            bool show = (opt == ActionOption.KeyTap || opt == ActionOption.KeyCombo);
            keyBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            var a = _editing!.Get(id);
            string baseText = Loc("GamepadMappingChangeKeyButton").TrimEnd('…');
            if (string.IsNullOrEmpty(baseText) || baseText == "GamepadMappingChangeKeyButton") baseText = "改鍵";
            string keyText = (opt == ActionOption.KeyCombo) ? ComboName(a) : KeyName(a.Vk);
            keyBtn.Content = baseText + ": " + keyText;
        }

        // ── ComboBox 選項變動 → 寫回 model ───────────────────────────────────

        /// <summary>ComboBox 選項變動：依新選的 ActionOption 更新 _editing 並重新整理對應 KeyBtn。</summary>
        private void ActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_editing == null) return;
            if (sender is not ComboBox combo) return;
            if (combo.Tag is not string tagStr) return;
            if (!Enum.TryParse<GamepadInputId>(tagStr, out var id)) return;
            if (combo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not ActionOption opt) return;

            var prev = _editing.Get(id);
            var newAction = OptionToAction(opt, prev);
            _editing.Bindings[id] = newAction;

            // 同步該列的 KeyBtn
            if (_rows.TryGetValue(id, out var pair))
                UpdateKeyButton(id, opt, pair.keyBtn);
        }

        /// <summary>由 ActionOption 推 Action：KeyTap 保留 prev.Vk（若是 KeyTap/KeyHold），其他依 opt 直接建。</summary>
        private static GamepadAction OptionToAction(ActionOption opt, GamepadAction prev)
        {
            const int VK_RETURN = 0x0D, VK_TAB = 0x09;
            switch (opt)
            {
                case ActionOption.None:
                    return new GamepadAction { Kind = GamepadActionKind.None };
                case ActionOption.KeyTap:
                    {
                        // 保留 KeyTap / KeyHold 的 Kind / Vk；其他切過來時補 VK_RETURN
                        if (prev.Kind == GamepadActionKind.KeyTap || prev.Kind == GamepadActionKind.KeyHold)
                            return new GamepadAction { Kind = prev.Kind, Vk = prev.Vk == 0 ? VK_RETURN : prev.Vk };
                        return new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN };
                    }
                case ActionOption.KeyCombo:
                    {
                        if (prev.Kind == GamepadActionKind.KeyCombo)
                            return new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = prev.Vk == 0 ? VK_TAB : prev.Vk, Mods = prev.Mods };
                        return new GamepadAction { Kind = GamepadActionKind.KeyCombo, Vk = VK_TAB, Mods = GamepadModifier.Ctrl };
                    }
                case ActionOption.MouseLeft: return new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left };
                case ActionOption.MouseRight: return new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right };
                case ActionOption.MouseMiddle: return new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Middle };
                case ActionOption.WheelUp: return new GamepadAction { Kind = GamepadActionKind.MouseWheel, Dir = GamepadWheelDir.Up };
                case ActionOption.WheelDown: return new GamepadAction { Kind = GamepadActionKind.MouseWheel, Dir = GamepadWheelDir.Down };
                case ActionOption.WheelLeft: return new GamepadAction { Kind = GamepadActionKind.MouseWheel, Dir = GamepadWheelDir.Left };
                case ActionOption.WheelRight: return new GamepadAction { Kind = GamepadActionKind.MouseWheel, Dir = GamepadWheelDir.Right };
                case ActionOption.StickCursor: return new GamepadAction { Kind = GamepadActionKind.StickCursor };
                case ActionOption.StickScroll: return new GamepadAction { Kind = GamepadActionKind.StickScroll };
                case ActionOption.StickArrows: return new GamepadAction { Kind = GamepadActionKind.StickArrows };
                case ActionOption.StickWasd: return new GamepadAction { Kind = GamepadActionKind.StickWasd };
                default: return new GamepadAction { Kind = GamepadActionKind.None };
            }
        }

        /// <summary>由 Action 推 ActionOption（給 ComboBox 預選用）。</summary>
        private static ActionOption ToOption(GamepadAction a, GamepadInputId id)
        {
            switch (a.Kind)
            {
                case GamepadActionKind.None: return ActionOption.None;
                case GamepadActionKind.KeyTap:
                case GamepadActionKind.KeyHold: return ActionOption.KeyTap;
                case GamepadActionKind.KeyCombo: return ActionOption.KeyCombo;
                case GamepadActionKind.MouseButton:
                    return a.Which switch
                    {
                        GamepadMouseWhich.Right => ActionOption.MouseRight,
                        GamepadMouseWhich.Middle => ActionOption.MouseMiddle,
                        _ => ActionOption.MouseLeft,
                    };
                case GamepadActionKind.MouseWheel:
                    return a.Dir switch
                    {
                        GamepadWheelDir.Down => ActionOption.WheelDown,
                        GamepadWheelDir.Left => ActionOption.WheelLeft,
                        GamepadWheelDir.Right => ActionOption.WheelRight,
                        _ => ActionOption.WheelUp,
                    };
                case GamepadActionKind.StickCursor: return ActionOption.StickCursor;
                case GamepadActionKind.StickScroll: return ActionOption.StickScroll;
                case GamepadActionKind.StickArrows: return ActionOption.StickArrows;
                case GamepadActionKind.StickWasd: return ActionOption.StickWasd;
                default: return ActionOption.None;
            }
        }

        // ── 改鍵 / 重設 / 從其他程式讀入 ────────────────────────────────────

        /// <summary>「改鍵」按鈕：開 ChangeKeyDialog，回來後寫回 _editing 並重新整理該列。</summary>
        private async void ChangeKey_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            if (sender is not Button btn || btn.Tag is not string tagStr) return;
            if (!Enum.TryParse<GamepadInputId>(tagStr, out var id)) return;
            var a = _editing.Get(id);
            bool isCombo = a.Kind == GamepadActionKind.KeyCombo;
            var dlg = new ChangeKeyDialog(XamlRoot, _resw, a, isCombo);
            await ShowDialogAsync(dlg);
            if (dlg.Result != null)
            {
                _editing.Bindings[id] = dlg.Result;
                if (_rows.TryGetValue(id, out var pair))
                    UpdateKeyButton(id, ToOption(dlg.Result, id), pair.keyBtn);
            }
            // 焦點回原「改鍵」按鈕（避免跳到漢堡按鈕）
            btn.Focus(FocusState.Programmatic);
        }

        /// <summary>「重設為 OmniNav 預設」：彈確認後把 16 列覆寫回 OmniNav。</summary>
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                Loc("GamepadMappingResetConfirmTitle"),
                Loc("GamepadMappingResetConfirmBody"),
                Loc("GamepadMappingResetConfirmYes"),
                Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = GamepadBuiltInLayouts.OmniNav();
                _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                RefreshAllRows();
            }
            originBtn?.Focus(FocusState.Programmatic);
        }

        /// <summary>「重設為 Classic 預設」：彈確認後把 16 列覆寫回 Classic。</summary>
        private async void ResetClassicButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                Loc("GamepadMappingResetClassicConfirmTitle"),
                Loc("GamepadMappingResetClassicConfirmBody"),
                Loc("GamepadMappingResetClassicConfirmYes"),
                Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = GamepadBuiltInLayouts.Classic();
                _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                RefreshAllRows();
            }
            originBtn?.Focus(FocusState.Programmatic);
        }

        /// <summary>「清除全部」：彈確認後把 16 個輸入位全設為 None。</summary>
        private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                Loc("GamepadMappingClearAllConfirmTitle"),
                Loc("GamepadMappingClearAllConfirmBody"),
                Loc("GamepadMappingClearAllConfirmYes"),
                Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = new Dictionary<GamepadInputId, GamepadAction>();
                _dpadEditingCustom = false;
                RefreshAllRows();
            }
            originBtn?.Focus(FocusState.Programmatic);
        }

        /// <summary>「從其他程式讀入」：開 CopyFromProfileDialog，回來後 deep clone 其 bindings。</summary>
        private async void CopyFromButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            List<GamepadProfile> others;
            try
            {
                others = GamepadProfileStore.Load().Where(p => !IsSameProfileSlot(p.AppId, _editing.AppId)).ToList();
            }
            catch { return; }
            if (others.Count == 0) return;

            var dlg = new CopyFromProfileDialog(XamlRoot, _resw, others);
            await ShowDialogAsync(dlg);
            if (dlg.SelectedAppId != null)
            {
                var src = GamepadProfileStore.Find(dlg.SelectedAppId);
                if (src != null)
                {
                    var newBindings = new Dictionary<GamepadInputId, GamepadAction>();
                    foreach (var kv in src.Bindings)
                        newBindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
                    _editing.Bindings = newBindings;
                    _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                    RefreshAllRows();
                }
            }
            originBtn?.Focus(FocusState.Programmatic);
        }

        // ── 對外操作 ────────────────────────────────────────────────────────

        /// <summary>給宿主 X 鍵呼叫：刪除目前編輯的 profile（彈確認 → GamepadProfileStore.Delete → Deleted）。</summary>
        public async void DeleteCurrent()
        {
            if (_editing == null || _isNew) { Closed?.Invoke(this, EventArgs.Empty); return; }
            var dlg = new GamepadMessageDialog(XamlRoot,
                Loc("GamepadMappingDeleteConfirmTitle"),
                Loc("GamepadMappingDeleteConfirmBody"),
                Loc("GamepadMappingDeleteConfirmYes"),
                Loc("GamepadMappingDeleteConfirmNo"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                GamepadProfileStore.Delete(_editing.AppId);
                Deleted?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 給宿主 B 鍵呼叫：存檔並關閉。
        /// 分支：
        ///   - 黑名單 → 顯提示不關閉
        ///   - 全 None：等同於沒有自訂配置 → 彈「移除此程式的自訂配置？」確認；確認後既有 profile 走 Delete + Deleted 事件、新建中直接 Closed（不寫入 store）
        ///   - 其他 → Upsert + Closed
        /// </summary>
        public async void Save()
        {
            if (_editing == null) { Closed?.Invoke(this, EventArgs.Empty); return; }

            if (GamepadProfileStore.IsBlacklisted(_editing.AppId))
            {
                var dlg = new GamepadMessageDialog(XamlRoot,
                    Loc("GamepadMappingBlacklistedTitle"),
                    Loc("GamepadMappingBlacklistedBody"),
                    Loc("GamepadKeyPickerOk"),
                    null);
                await ShowDialogAsync(dlg);
                return;
            }

            if (_editing.IsEffectivelyEmpty())
            {
                var dlg = new GamepadMessageDialog(XamlRoot,
                    Loc("GamepadMappingEmptyRemoveTitle"),
                    Loc("GamepadMappingEmptyRemoveBody"),
                    Loc("GamepadMappingEmptyRemoveYes"),
                    Loc("GamepadKeyPickerCancel"));
                await ShowDialogAsync(dlg);
                if (!dlg.Result) return;

                // 確認後：既有 profile 從 store 刪掉、新建中直接放棄
                if (!_isNew)
                {
                    GamepadProfileStore.Delete(_editing.AppId);
                    Deleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    Closed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            GamepadProfileStore.Upsert(_editing);
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // ── helper ──────────────────────────────────────────────────────────

        /// <summary>顯示子對話方塊（包裹 DialogActiveChanged 開關以便宿主切換手把輪詢）。</summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dlg)
        {
            DialogActiveChanged?.Invoke(this, true);
            try { return await dlg.ShowAsync(); }
            finally { DialogActiveChanged?.Invoke(this, false); }
        }

        /// <summary>VK 顯示名稱（resw 經 VirtualKeys 表查；無對應回 VK# 字面）。</summary>
        private string KeyName(int vk)
        {
            var entry = VirtualKeys.FindByVk(vk);
            if (entry != null)
            {
                if (!string.IsNullOrEmpty(entry.ReswKey))
                {
                    string s = Loc(entry.ReswKey);
                    if (!string.IsNullOrEmpty(s) && s != entry.ReswKey) return s;
                }
                return entry.FallbackText;
            }
            return "VK" + vk.ToString("X");
        }

        /// <summary>組合鍵顯示名稱（依序 Ctrl+Shift+Alt+Win + 主鍵）。</summary>
        private string ComboName(GamepadAction a)
        {
            var parts = new List<string>();
            if ((a.Mods & GamepadModifier.Ctrl) != 0) parts.Add(Loc("GamepadModifier_Ctrl"));
            if ((a.Mods & GamepadModifier.Shift) != 0) parts.Add(Loc("GamepadModifier_Shift"));
            if ((a.Mods & GamepadModifier.Alt) != 0) parts.Add(Loc("GamepadModifier_Alt"));
            if ((a.Mods & GamepadModifier.Win) != 0) parts.Add(Loc("GamepadModifier_Win"));
            parts.Add(KeyName(a.Vk));
            return string.Join("+", parts);
        }

        /// <summary>
        /// 判定兩個 AppId 是否指向同一個 profile 槽（Editor CopyFrom 排除自己用）。
        /// Aumid 類：Kind + Value 相同（PFN 已唯一）。
        /// Process 類：Kind + Value + FullPath 正規化後相同（含兩邊都 null）。同名不同 path 視為不同 profile。
        /// </summary>
        private static bool IsSameProfileSlot(AppId a, AppId b)
        {
            if (a == null || b == null) return false;
            if (!a.Matches(b)) return false;
            if (a.Kind == IdKind.Aumid) return true;
            string? pa = AppId.NormalizePath(a.FullPath);
            string? pb = AppId.NormalizePath(b.FullPath);
            return string.Equals(pa, pb, StringComparison.Ordinal);
        }

        /// <summary>顯示 path-bound profile 的路徑副標；Aumid 類或 FullPath 空時整區隱藏。</summary>
        private void RefreshPathDisplay()
        {
            if (_editing == null) { PathDisplayPanel.Visibility = Visibility.Collapsed; return; }
            if (_editing.AppId.Kind != IdKind.Process || string.IsNullOrEmpty(_editing.AppId.FullPath))
            {
                PathDisplayPanel.Visibility = Visibility.Collapsed;
                return;
            }
            PathDisplayPanel.Visibility = Visibility.Visible;
            PathHintText.Text = _editing.AppId.FullPath ?? string.Empty;
        }

        /// <summary>appId 副文字（在地化 prefix + 完整識別值；Win32 → 「行程: <name>」、packaged → 「AUMID: <full>」）。</summary>
        private string AppIdSubtitle(AppId appId)
        {
            if (appId == null) return string.Empty;
            string prefix = appId.Kind == IdKind.Aumid
                ? Loc("AppIdAumidPrefix")
                : Loc("AppIdProcessPrefix");
            return prefix + (appId.Value ?? string.Empty);
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
