using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using static OmniConsole.Services.GamepadProfileMappingHelper;

namespace OmniConsole.Pages.Settings.GamepadMapping
{
    /// <summary>
    /// Frame.Navigate 帶入編輯器的目標：要編輯的 AppId 與顯示名稱。
    /// <paramref name="IsCustomLayout"/> 為 true 時改編輯全域的自訂版面，此時 AppId 傳空值即可。
    /// </summary>
    internal sealed record GamepadProfileEditorParam(AppId AppId, string DisplayName, bool IsCustomLayout = false);

    /// <summary>
    /// 玩家各 App 的手把 profile 編輯器（16 個 XInput 輸入位 → 動作）。DPad 4 子列依主行選擇條件展開。
    /// 由 GamepadMappingHostView 透過內層 Frame.Navigate 載入（切走即銷毀）；目標 AppId/顯示名稱經 OnNavigatedTo 帶入。
    /// </summary>
    public sealed partial class GamepadProfileEditor : Page
    {
        // 玩家手動切到「自訂」後即使 4 子列湊巧等價於某預設組合，主行維持「自訂」
        private bool _dpadEditingCustom = false;

        private readonly ResourceLoader _resourceLoader = new();
        private Dictionary<GamepadInputId, (ComboBox combo, Button? keyBtn)> _rows = new();
        // 每個輸入位的「長按連發」ToggleButton；DPad 4 向共用主行那顆（key 統一記 DPadUp）
        private Dictionary<GamepadInputId, ToggleButton> _repeatBtns = new();
        private GamepadProfile? _editing;
        private bool _isNew;
        // 編輯全域自訂版面而非某個程式的 profile；分流只讀這個欄位，各 handler 一律不看
        private bool _isCustomLayout;

        /// <summary>編輯器存檔/取消後通知宿主關閉（CloseEditor）。</summary>
        public event EventHandler? Closed;

        /// <summary>使用者按底部 (X) 刪除確認後通知宿主關閉。</summary>
        public event EventHandler? Deleted;


        /// <summary>建構子：建立 row 對照表並對每個 ComboBox 填入合法動作選項。</summary>
        public GamepadProfileEditor()
        {
            InitializeComponent();
            BuildRows();
            // 向左撞牆時把焦點送回左側導覽漢堡按鈕
            FocusNavHelper.WireBackToPaneOnLeftWall(RootScrollViewer);
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

            // 行程優先權下拉選單：索引 0=標準、1=效能優先（對應 GamepadProcessPriority）
            ProcessPriorityCombo.Items.Clear();
            ProcessPriorityCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadProcessPriority_Standard") });
            ProcessPriorityCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadProcessPriority_Performance") });

            // 長按連發 ToggleButton 對照表：14 按鈕列 + 2 搖桿列 + DPad 主行（DPadUp 那顆套四向）
            _repeatBtns = new Dictionary<GamepadInputId, ToggleButton>
            {
                [GamepadInputId.A] = RepeatBtnA,
                [GamepadInputId.B] = RepeatBtnB,
                [GamepadInputId.X] = RepeatBtnX,
                [GamepadInputId.Y] = RepeatBtnY,
                [GamepadInputId.LB] = RepeatBtnLB,
                [GamepadInputId.RB] = RepeatBtnRB,
                [GamepadInputId.LT] = RepeatBtnLT,
                [GamepadInputId.RT] = RepeatBtnRT,
                [GamepadInputId.LS] = RepeatBtnLS,
                [GamepadInputId.RS] = RepeatBtnRS,
                [GamepadInputId.LStick] = RepeatBtnLStick,
                [GamepadInputId.RStick] = RepeatBtnRStick,
                [GamepadInputId.DPadUp] = RepeatBtnDPad,
            };
        }

        /// <summary>該 ActionOption 是否「會送鍵盤鍵」（連發才有意義）：KeyTap / KeyCombo / 方向類。</summary>
        private static bool OptionSendsKey(ActionOption opt)
        {
            return opt == ActionOption.KeyTap || opt == ActionOption.KeyCombo
                || opt == ActionOption.StickArrows || opt == ActionOption.StickWasd
                || opt == ActionOption.DpadArrows || opt == ActionOption.DpadWasd
                || opt == ActionOption.DpadNumpad || opt == ActionOption.DpadCustom;
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
                    if (LicenseService.HasEntitlement(LicenseService.Entitlement.Pro))
                    {
                        Add(combo, ActionOption.GamepadKeyboard, "GamepadAction_GamepadKeyboard");
                        Add(combo, ActionOption.OnScreenKeyboard, "GamepadAction_OnScreenKeyboard");
                    }
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
                Content = _resourceLoader.Loc(reswKey),
                Tag = opt
            });
        }

        // ── 對外狀態 ────────────────────────────────────────────────────────

        /// <summary>是否可刪除（既有 profile = true；新建中與自訂版面 = false）。</summary>
        public bool CanDelete => !_isCustomLayout && _editing != null && !_isNew;

        // ── 載入 / 重新整理 ───────────────────────────────────────────────

        /// <summary>Frame 導覽進來時依帶入的 AppId/顯示名稱載入或新建 profile，並把白框焦點落在頁首卡片。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is GamepadProfileEditorParam p)
            {
                _isCustomLayout = p.IsCustomLayout;
                if (_isCustomLayout) LoadCustomLayout();
                else Load(p.AppId, p.DisplayName);
            }
            FocusHeaderCard();
        }

        /// <summary>程式化把白框焦點落在頁首卡片；版面尚未佈局完成導致首次 Focus 落空時掛 LayoutUpdated 延後重試。</summary>
        private void FocusHeaderCard()
        {
            if (HeaderCard.Focus(FocusStateHelper.Preferred)) return;

            EventHandler<object>? handler = null;
            handler = (s, e) =>
            {
                if (HeaderCard.Focus(FocusStateHelper.Preferred))
                    HeaderCard.LayoutUpdated -= handler;
            };
            HeaderCard.LayoutUpdated += handler;
        }

        /// <summary>
        /// 載入或新建某 App 的 profile：既有 → 拷貝；新建 → 依進階頁版面配置設定套用預設。
        /// 命中內建導覽清單（瀏覽器 / 檔案總管 / Discord 等）→ 套內建導覽套組（DPad 連發），
        /// 忠實反映內建當下行為，覆寫存 JSON 後不丟連發；其餘 → 套純版面樣板。
        /// </summary>
        private void Load(AppId appId, string displayName)
        {
            var existing = GamepadProfileStore.Find(appId);
            _isNew = existing == null;
            _editing = existing?.Clone() ?? new GamepadProfile
            {
                AppId = new AppId { Kind = appId.Kind, Value = appId.Value, FullPath = appId.FullPath, VersionAgnosticPath = appId.VersionAgnosticPath },
                DisplayName = displayName,
                Bindings = BuiltInLayoutFor(appId, SettingsService.GetMouseModeLayout())
            };
            if (!_isNew && !string.IsNullOrEmpty(displayName))
                _editing.DisplayName = displayName;

            // protocol / widget 帶進來的 path 與版本無關旗標寫入未繫結路徑的 _editing；已 path-bound profile 不被覆寫
            if (appId.Kind == IdKind.Process
                && !string.IsNullOrEmpty(appId.FullPath)
                && string.IsNullOrEmpty(_editing.AppId.FullPath))
            {
                _editing.AppId.FullPath = appId.FullPath;
                _editing.AppId.VersionAgnosticPath = appId.VersionAgnosticPath;
            }

            AppNameText.Text = !string.IsNullOrEmpty(_editing.DisplayName) ? _editing.DisplayName : (_editing.AppId.Value ?? string.Empty);
            AppIdText.Text = AppIdSubtitle(_editing.AppId);

            RefreshPathDisplay();

            // CopyFrom 只在有其他 profile 時啟用（撞名不同路徑視為不同 profile，仍可互相 CopyFrom）。
            // 只需判斷「有沒有」，用 Any 短路即可，不必 ToList 建出整個清單。
            try
            {
                CopyFromButton.IsEnabled = GamepadProfileStore.Load().Any(p => !IsSameProfileSlot(p.AppId, _editing.AppId));
            }
            catch
            {
                CopyFromButton.IsEnabled = false;
            }

            if (_rows.Count == 0) BuildRows();

            // 十字鍵有原生反應的 app：十字鍵一律不帶值（含既有 profile 殘留值），清成 None
            ClearNativeDpadIfNeeded();

            // 載入既存 profile：依 model 偵測主行 DPad 模式，Custom 則自動展開
            _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);

            RefreshAllRows();

            // 同步 BlockNativeGamepadInput 旗標到 ToggleSwitch（_suppress 旗標讓 Toggled handler 不寫回 _editing）
            _suppressBlockNativeGamepadInputToggled = true;
            BlockNativeGamepadInputSwitch.IsOn = _editing.BlockNativeGamepadInput;
            _suppressBlockNativeGamepadInputToggled = false;

            // 同步行程優先權到下拉選單（_suppress 旗標讓 SelectionChanged handler 不寫回 _editing）
            _suppressProcessPrioritySelection = true;
            ProcessPriorityCombo.SelectedIndex = _editing.ProcessPriority == GamepadProcessPriority.Performance ? 1 : 0;
            _suppressProcessPrioritySelection = false;

            ApplyBuiltInAppControlLocks(appId);
        }

        /// <summary>
        /// 載入全域自訂版面：檔案裡已有就取用，第一次建立則複製目前選的版面當底。
        /// _editing 掛一個空的 AppId，「內建導覽 app」「十字鍵有原生反應」「阻擋雙重輸入無效」
        /// 三個判斷因此自動全回 false，語意正好是「一般程式、無任何特殊規則」。
        /// </summary>
        private void LoadCustomLayout()
        {
            var existing = GamepadProfileStore.LoadCustomLayout();
            _isNew = existing == null;
            _editing = new GamepadProfile
            {
                AppId = new AppId(),
                DisplayName = string.Empty,
                Bindings = existing ?? BuiltInLayoutFor(null, SettingsService.GetMouseModeLayout())
            };

            AppNameText.Text = _resourceLoader.Loc("GamepadMappingCustomLayoutTitle");
            AppIdText.Text = _resourceLoader.Loc("GamepadMappingCustomLayoutSubtitle");
            RefreshPathDisplay();

            // 從某個程式的設定檔抄一份當自訂版面的起點是合理需求，故保留這個入口
            try
            {
                CopyFromButton.IsEnabled = GamepadProfileStore.Load().Count > 0;
            }
            catch
            {
                CopyFromButton.IsEnabled = false;
            }

            if (_rows.Count == 0) BuildRows();

            _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
            RefreshAllRows();

            ApplyCustomLayoutControlLocks();
        }

        /// <summary>目前編輯對象是不是十字鍵有原生反應的 app（檔案總管 / 檔案選擇器 / 工作管理員）。</summary>
        private bool IsEditingNativeDpadApp(AppId? appId)
        {
            return appId != null && GamepadBuiltInLayouts.HandlesDpadNatively(appId);
        }

        /// <summary>依編輯對象停用不適用的控制項（保留當下值）：阻擋雙重輸入對啟動器/導覽介面無效、十字鍵在有原生反應的 app 上會雙跳。</summary>
        private void ApplyBuiltInAppControlLocks(AppId appId)
        {
            // 阻擋雙重輸入：對 packaged app 與啟動器/導覽介面（檔案總管、檔案選擇器、桌面 Steam、Epic、桌面 Playnite 等）無效，一律停用
            bool blockInputEnabled = !GamepadBuiltInLayouts.IsBlockNativeInputIneffective(appId);
            BlockNativeGamepadInputSwitch.IsEnabled = blockInputEnabled;

            // 十字鍵：僅原生已有反應的 app（檔案總管 / 檔案選擇器 / 工作管理員）需停用（桌面 Steam 保留導覽版含連發）
            bool dpadEnabled = !IsEditingNativeDpadApp(appId);
            ComboDPad.IsEnabled = dpadEnabled;
            RepeatBtnDPad.IsEnabled = dpadEnabled;

            // 十字鍵自訂模式的 4 子列（下拉選單 + 改鍵鈕）一併鎖住
            foreach (var k in DpadKeys)
                if (_rows.TryGetValue(k, out var pair))
                {
                    pair.combo.IsEnabled = dpadEnabled;
                    if (pair.keyBtn != null) pair.keyBtn.IsEnabled = dpadEnabled;
                }

            PerAppOptionsPanel.Visibility = Visibility.Visible;
            // 副標此時是識別字（AUMID 可能很長），截斷比換行好看
            AppIdText.TextWrapping = TextWrapping.NoWrap;
            AppIdText.TextTrimming = TextTrimming.CharacterEllipsis;
            // 「重設為自訂版面」只在使用者已經建立過自訂版面時出現
            SetResetCustomButtonVisible(GamepadProfileStore.HasCustomLayout());

            WireVerticalFocusChain(showPerAppOptions: true, blockInputEnabled: blockInputEnabled);
        }

        /// <summary>自訂版面模式的控制項狀態：收起只對單一程式有意義的兩個選項，十字鍵全開。</summary>
        private void ApplyCustomLayoutControlLocks()
        {
            // 隱藏而非停用：這兩個選項對全域版面沒有意義，留兩排灰字會讓人以為是還沒解鎖
            PerAppOptionsPanel.Visibility = Visibility.Collapsed;
            // 副標此時是一整句說明，換行完整顯示；截斷會讓比英文長的語言（西語、土耳其語等）看不到後半
            AppIdText.TextTrimming = TextTrimming.None;
            AppIdText.TextWrapping = TextWrapping.Wrap;
            // 把自訂版面重設成自己沒有意義
            SetResetCustomButtonVisible(false);

            ComboDPad.IsEnabled = true;
            RepeatBtnDPad.IsEnabled = true;
            foreach (var k in DpadKeys)
                if (_rows.TryGetValue(k, out var pair))
                {
                    pair.combo.IsEnabled = true;
                    if (pair.keyBtn != null) pair.keyBtn.IsEnabled = true;
                }

            WireVerticalFocusChain(showPerAppOptions: false, blockInputEnabled: false);
        }

        /// <summary>
        /// 設定「重設為自訂版面」的顯隱。
        /// 收起時要連它那一欄的寬度一起歸零：五欄是等寬平分，只收按鈕的話右邊會留下一整格空白。
        /// </summary>
        private void SetResetCustomButtonVisible(bool visible)
        {
            ResetCustomButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ResetCustomColumn.Width = visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            // 收起後由「經典預設」變成最右邊那顆，右邊界不再需要留間距
            ResetClassicButton.Margin = visible ? new Thickness(4, 0, 4, 0) : new Thickness(4, 0, 0, 0);
        }

        /// <summary>
        /// 接好「頁首 → 單一程式選項 → 工具列 → 第一列」的上下焦點鏈，以及工具列的左右牆。
        /// 工具列五顆之中只有「重設為自訂版面」會被收起，其餘恆在。
        /// </summary>
        private void WireVerticalFocusChain(bool showPerAppOptions, bool blockInputEnabled)
        {
            Control lastToolButton = ResetCustomButton.Visibility == Visibility.Visible
                ? ResetCustomButton : ResetClassicButton;

            UIElement above = showPerAppOptions ? ProcessPriorityCombo : HeaderCard;
            CopyFromButton.XYFocusUp = above;
            ClearAllButton.XYFocusUp = above;
            ResetButton.XYFocusUp = above;
            ResetClassicButton.XYFocusUp = above;
            ResetCustomButton.XYFocusUp = above;

            if (showPerAppOptions)
            {
                // 頁首 → 阻擋雙重輸入開關 → 行程優先權，開關停用時上下避開它
                FocusNavHelper.ApplyDisablableMiddleChainFocusNav(HeaderCard, BlockNativeGamepadInputSwitch, ProcessPriorityCombo, blockInputEnabled);
                // 行程優先權下拉靠右對齊，往下落在這排最右邊那顆
                ProcessPriorityCombo.XYFocusDown = lastToolButton;
            }
            else
            {
                // 「從程式複製…」沒有設定檔可抄時會停用，而顯式指向停用的元素會讓向下導航靜默失效，故此時改指下一顆恆在的按鈕。
                HeaderCard.XYFocusDown = CopyFromButton.IsEnabled ? CopyFromButton : ClearAllButton;
            }

            // 兩端各自指向自己當左右牆，焦點才不會飛出這一排
            CopyFromButton.XYFocusLeft = CopyFromButton;
            lastToolButton.XYFocusRight = lastToolButton;

            ComboA.XYFocusUp = ClearAllButton;
        }

        private bool _suppressBlockNativeGamepadInputToggled = false;
        private bool _suppressProcessPrioritySelection = false;

        /// <summary>BlockNativeGamepadInput ToggleSwitch 切換 → 寫回 _editing。</summary>
        private void BlockNativeGamepadInputSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressBlockNativeGamepadInputToggled) return;
            if (_editing == null) return;
            _editing.BlockNativeGamepadInput = BlockNativeGamepadInputSwitch.IsOn;
        }

        /// <summary>行程優先權下拉選單切換 → 寫回 _editing（索引 1=效能優先，其餘標準）。</summary>
        private void ProcessPriorityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressProcessPrioritySelection) return;
            if (_editing == null) return;
            _editing.ProcessPriority = ProcessPriorityCombo.SelectedIndex == 1
                ? GamepadProcessPriority.Performance : GamepadProcessPriority.Standard;
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
                if (_editing.Get(DpadKeys[i]).Kind != GamepadActionKind.None) { allNone = false; break; }
            if (allNone) return ActionOption.None;

            // 比對三組預設：4 鍵都 KeyTap 且 vk 逐位相符
            if (MatchDpadVks(DpadArrowsVks)) return ActionOption.DpadArrows;
            if (MatchDpadVks(DpadWasdVks)) return ActionOption.DpadWasd;
            if (MatchDpadVks(DpadNumpadVks)) return ActionOption.DpadNumpad;

            return ActionOption.DpadCustom;
        }

        /// <summary>檢查 4 個 DPad KeyId 是否都是 KeyTap 且 VK 與傳入的預設值逐位相符。</summary>
        private bool MatchDpadVks(int[] expected)
        {
            if (_editing == null) return false;
            for (int i = 0; i < 4; i++)
            {
                var a = _editing.Get(DpadKeys[i]);
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
            UpdateRepeatButton(GamepadInputId.DPadUp, opt);
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
                case ActionOption.DpadArrows: ApplyDPadPresetVks(DpadArrowsVks); _dpadEditingCustom = false; break;
                case ActionOption.DpadWasd: ApplyDPadPresetVks(DpadWasdVks); _dpadEditingCustom = false; break;
                case ActionOption.DpadNumpad: ApplyDPadPresetVks(DpadNumpadVks); _dpadEditingCustom = false; break;
                case ActionOption.None: ApplyDPadAllNone(); _dpadEditingCustom = false; break;
                case ActionOption.DpadCustom: ApplyDPadAllNone(); _dpadEditingCustom = true; break;
            }

            // 4 子列 model 變了 → 同步 UI；主行自身依目前狀態切展開
            foreach (var k in DpadKeys)
                if (_rows.TryGetValue(k, out var pair))
                    SyncRowFromModel(k, pair.combo, pair.keyBtn);
            UpdateDPadExpandedVisibility(opt);
            UpdateRepeatButton(GamepadInputId.DPadUp, opt);
        }

        /// <summary>把 4 個 DPad KeyId 各自寫入 expected[] 對應 VK 的 KeyTap 動作。</summary>
        private void ApplyDPadPresetVks(int[] vks)
        {
            if (_editing == null) return;
            for (int i = 0; i < 4; i++)
                _editing.Bindings[DpadKeys[i]] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = vks[i] };
        }

        /// <summary>十字鍵有原生反應的 app：套用預設或重設後把十字鍵強制清為 None。</summary>
        private void ClearNativeDpadIfNeeded()
        {
            if (_editing == null || !IsEditingNativeDpadApp(_editing.AppId)) return;
            ApplyDPadAllNone();
            _dpadEditingCustom = false;
        }

        /// <summary>把 4 個 DPad KeyId 全部清為 None（自訂模式起點、主行選 None 時亦走此路徑）。</summary>
        private void ApplyDPadAllNone()
        {
            if (_editing == null) return;
            foreach (var k in DpadKeys)
                _editing.Bindings[k] = new GamepadAction { Kind = GamepadActionKind.None };
        }

        /// <summary>從 model 同步單列 UI（過程中暫時解除 SelectionChanged 事件處理器）。</summary>
        private void SyncRowFromModel(GamepadInputId id, ComboBox combo, Button? keyBtn)
        {
            if (_editing == null) return;
            var a = _editing.Get(id);
            var opt = ToOption(a, id);

            int idx = FindOptionIndex(combo, opt);
            if (idx < 0)
            {
                // 這一位存的是 Pro 專屬動作，未持有授權，選項不在下拉選單裡。
                // 視覺回退為「無」，玩家重新取得授權即自動復原；只有使用者真的動了下拉選單才會覆蓋它。
                opt = ActionOption.None;
                idx = FindOptionIndex(combo, opt);
                if (idx < 0) idx = 0;
            }

            combo.SelectionChanged -= ActionCombo_SelectionChanged;
            combo.SelectedIndex = idx;
            combo.SelectionChanged += ActionCombo_SelectionChanged;

            UpdateKeyButton(id, opt, keyBtn);
            UpdateRepeatButton(id, opt);
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
            string baseText = _resourceLoader.Loc("GamepadMappingChangeKeyButton").TrimEnd('…');
            if (string.IsNullOrEmpty(baseText) || baseText == "GamepadMappingChangeKeyButton") baseText = "Change key";
            string keyText = (opt == ActionOption.KeyCombo) ? ComboName(a) : KeyName(a.Vk);
            keyBtn.Content = baseText + ": " + keyText;
        }

        /// <summary>
        /// 更新某輸入位的連發 ToggleButton：opt 會送鍵盤鍵時顯示、勾選態同步 model；否則隱藏並清旗標。
        /// DPad 走主行那顆（id 傳 DPadUp），狀態取 DPadUp 的 RepeatOnHold。
        /// </summary>
        private void UpdateRepeatButton(GamepadInputId id, ActionOption opt)
        {
            if (!_repeatBtns.TryGetValue(id, out var btn)) return;
            bool show = OptionSendsKey(opt);
            btn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            btn.Click -= RepeatToggle_Click;
            btn.IsChecked = _editing!.Get(id).RepeatOnHold;
            btn.Click += RepeatToggle_Click;
        }

        /// <summary>連發 ToggleButton 切換 → 寫回 model。DPad（Tag=DPadUp）套用到 4 個十字鍵。</summary>
        private void RepeatToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            if (sender is not ToggleButton btn || btn.Tag is not string tagStr) return;
            if (!Enum.TryParse<GamepadInputId>(tagStr, out var id)) return;
            bool on = btn.IsChecked == true;
            if (id == GamepadInputId.DPadUp)
            {
                // DPad 主行連發套用到 4 向（各鍵 model 已由主行選擇建好，只改旗標）
                foreach (var dk in DpadKeys)
                    if (_editing.Bindings.TryGetValue(dk, out var da) && da != null) da.RepeatOnHold = on;
            }
            else
            {
                var a = _editing.Get(id);
                a.RepeatOnHold = on;
                _editing.Bindings[id] = a;
            }
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
            // 保留原連發旗標（切換動作型別時不無故清掉使用者設的連發）
            newAction.RepeatOnHold = prev.RepeatOnHold && OptionSendsKey(opt);
            _editing.Bindings[id] = newAction;

            // 同步該列的 KeyBtn 與連發鈕
            if (_rows.TryGetValue(id, out var pair))
                UpdateKeyButton(id, opt, pair.keyBtn);
            UpdateRepeatButton(id, opt);
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
            var dlg = new ChangeKeyDialog(XamlRoot, a, isCombo);
            await ShowDialogAsync(dlg);
            if (dlg.Result != null)
            {
                _editing.Bindings[id] = dlg.Result;
                if (_rows.TryGetValue(id, out var pair))
                    UpdateKeyButton(id, ToOption(dlg.Result, id), pair.keyBtn);
            }
            // 焦點回原「改鍵」按鈕（避免跳到漢堡按鈕）
            btn.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>「重設為 OmniNav 預設」：彈確認後把 16 列覆寫回 OmniNav。</summary>
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                _resourceLoader.Loc("GamepadMappingResetConfirmTitle"),
                _resourceLoader.Loc("GamepadMappingResetConfirmBody"),
                _resourceLoader.Loc("GamepadMappingResetConfirmYes"),
                _resourceLoader.Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = BuiltInLayoutForCurrentApp("OmniNav");
                ClearNativeDpadIfNeeded();
                _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                RefreshAllRows();
            }
            originBtn?.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>「重設為 Classic 預設」：彈確認後把 16 列覆寫回 Classic。</summary>
        private async void ResetClassicButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                _resourceLoader.Loc("GamepadMappingResetClassicConfirmTitle"),
                _resourceLoader.Loc("GamepadMappingResetClassicConfirmBody"),
                _resourceLoader.Loc("GamepadMappingResetClassicConfirmYes"),
                _resourceLoader.Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = BuiltInLayoutForCurrentApp("Classic");
                ClearNativeDpadIfNeeded();
                _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                RefreshAllRows();
            }
            originBtn?.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>
        /// 取指定 app 適用的內建 bindings：內建導覽 app（瀏覽器 / 檔案總管 / Discord 等）套導覽版
        /// （DPad 連發），忠實反映內建行為；一般 app 套純版面（DPad 不連發）。
        /// 自訂版面模式一律套導覽版：它唯一會生效的場景就是內建導覽清單命中。
        /// </summary>
        private Dictionary<GamepadInputId, GamepadAction> BuiltInLayoutFor(AppId? appId, string layout)
        {
            // 目前選的是自訂版面時，新建 profile 的底就是使用者那一套，這正是「從源頭客製化」的意義。
            // 自訂版面的內容住在 store 而不是 GamepadBuiltInLayouts，故要另外取。
            // 尚未建立或未持有 Pro 時 LoadCustomLayout 回 null，落回下方的內建版面，與 PhantomKey 的回退一致。
            if (!_isCustomLayout && layout == SettingsService.LayoutCustom)
            {
                var custom = GamepadProfileStore.LoadCustomLayout();
                if (custom != null) return custom;
            }

            return _isCustomLayout || (appId != null && GamepadBuiltInLayouts.IsBuiltInNavApp(appId))
                ? GamepadBuiltInLayouts.NavForLayout(layout)
                : GamepadBuiltInLayouts.ForLayout(layout);
        }

        /// <summary>取目前編輯對象適用的內建 bindings。</summary>
        private Dictionary<GamepadInputId, GamepadAction> BuiltInLayoutForCurrentApp(string layout)
            => BuiltInLayoutFor(_editing?.AppId, layout);

        /// <summary>
        /// 「重設為自訂版面」：彈確認後把 16 列覆寫成使用者的自訂版面。
        /// 與另外兩顆重設鈕不同，這裡不套導覽版的十字鍵連發覆寫：自訂版面已經是使用者的明確表態，替他改連發旗標是多事。
        /// </summary>
        private async void ResetCustomButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var custom = GamepadProfileStore.LoadCustomLayout();
            if (custom == null) { originBtn?.Focus(FocusStateHelper.Preferred); return; }

            var dlg = new GamepadMessageDialog(XamlRoot,
                _resourceLoader.Loc("GamepadMappingResetCustomConfirmTitle"),
                _resourceLoader.Loc("GamepadMappingResetCustomConfirmBody"),
                _resourceLoader.Loc("GamepadMappingResetCustomConfirmYes"),
                _resourceLoader.Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                var newBindings = new Dictionary<GamepadInputId, GamepadAction>();
                foreach (var kv in custom)
                    newBindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
                _editing.Bindings = newBindings;
                ClearNativeDpadIfNeeded();
                _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                RefreshAllRows();
            }
            originBtn?.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>「清除全部」：彈確認後把 16 個輸入位全設為 None。</summary>
        private async void ClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            Button? originBtn = sender as Button;
            if (_editing == null) return;
            var dlg = new GamepadMessageDialog(XamlRoot,
                _resourceLoader.Loc("GamepadMappingClearAllConfirmTitle"),
                _resourceLoader.Loc("GamepadMappingClearAllConfirmBody"),
                _resourceLoader.Loc("GamepadMappingClearAllConfirmYes"),
                _resourceLoader.Loc("GamepadKeyPickerCancel"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                _editing.Bindings = new Dictionary<GamepadInputId, GamepadAction>();
                _dpadEditingCustom = false;
                RefreshAllRows();
            }
            originBtn?.Focus(FocusStateHelper.Preferred);
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

            var dlg = new CopyFromProfileDialog(XamlRoot, others);
            await ShowDialogAsync(dlg);
            if (dlg.SelectedAppId != null)
            {
                var src = GamepadProfileStore.Find(dlg.SelectedAppId);
                if (src != null)
                {
                    var newBindings = new Dictionary<GamepadInputId, GamepadAction>();
                    foreach (var kv in src.Bindings)
                        newBindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
                    // 十字鍵有原生反應的 app 十字鍵被鎖定：讀入時保留當下值，不讓來源覆蓋
                    if (IsEditingNativeDpadApp(_editing.AppId))
                        foreach (var k in DpadKeys)
                            newBindings[k] = _editing.Get(k).Clone();
                    _editing.Bindings = newBindings;
                    _dpadEditingCustom = (DetectDPadModeFromModel() == ActionOption.DpadCustom);
                    RefreshAllRows();
                }
            }
            originBtn?.Focus(FocusStateHelper.Preferred);
        }

        // ── 對外操作 ────────────────────────────────────────────────────────

        /// <summary>給宿主 X 鍵呼叫：刪除目前編輯的 profile（彈確認 → GamepadProfileStore.Delete → Deleted）。</summary>
        public async void DeleteCurrent()
        {
            if (_editing == null || _isNew) { Closed?.Invoke(this, EventArgs.Empty); return; }
            var appId = _editing.AppId;
            if (appId == null) { Closed?.Invoke(this, EventArgs.Empty); return; }
            string appName = !string.IsNullOrEmpty(_editing.DisplayName) ? _editing.DisplayName : (appId.Value ?? string.Empty);
            var dlg = new GamepadMessageDialog(XamlRoot,
                _resourceLoader.Loc("GamepadMappingDeleteConfirmTitle"),
                string.Format(_resourceLoader.Loc("GamepadMappingDeleteConfirmBody"), appName),
                _resourceLoader.Loc("GamepadMappingDeleteConfirmYes"),
                _resourceLoader.Loc("GamepadMappingDeleteConfirmNo"));
            await ShowDialogAsync(dlg);
            if (dlg.Result)
            {
                GamepadProfileStore.Delete(appId);
                Deleted?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// 給宿主 B 鍵呼叫：存檔並關閉。
        /// 分支：
        ///   - 黑名單 → 顯提示不關閉
        ///   - 全 None：等同於沒有自訂設定檔 → 彈「移除此程式的自訂設定檔？」確認；確認後既有 profile 走 Delete + Deleted 事件、新建中直接 Closed（不寫入 store）
        ///   - 其他 → Upsert + Closed
        /// </summary>
        public async void Save()
        {
            if (_editing == null) { Closed?.Invoke(this, EventArgs.Empty); return; }
            if (_isCustomLayout) { await SaveCustomLayoutAsync(); return; }

            if (GamepadProfileStore.IsBlacklisted(_editing.AppId))
            {
                var dlg = new GamepadMessageDialog(XamlRoot,
                    _resourceLoader.Loc("GamepadMappingBlacklistedTitle"),
                    _resourceLoader.Loc("GamepadMappingBlacklistedBody"),
                    _resourceLoader.Loc("GamepadKeyPickerOk"),
                    null);
                await ShowDialogAsync(dlg);
                return;
            }

            if (_editing.IsEffectivelyEmpty())
            {
                var dlg = new GamepadMessageDialog(XamlRoot,
                    _resourceLoader.Loc("GamepadMappingEmptyRemoveTitle"),
                    _resourceLoader.Loc("GamepadMappingEmptyRemoveBody"),
                    _resourceLoader.Loc("GamepadMappingEmptyRemoveYes"),
                    _resourceLoader.Loc("GamepadKeyPickerCancel"));
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

        /// <summary>
        /// 儲存自訂版面。全 None 是合法設定（使用者就是要讓手把在內建導覽程式裡完全不介入），
        /// 但很容易被當成故障，故先確認一次；確認後照存不刪檔，去留交給使用者。
        /// </summary>
        private async Task SaveCustomLayoutAsync()
        {
            if (_editing == null) { Closed?.Invoke(this, EventArgs.Empty); return; }

            if (_editing.IsEffectivelyEmpty())
            {
                var dlg = new GamepadMessageDialog(XamlRoot,
                    _resourceLoader.Loc("GamepadMappingCustomLayoutEmptyTitle"),
                    _resourceLoader.Loc("GamepadMappingCustomLayoutEmptyBody"),
                    _resourceLoader.Loc("GamepadMappingCustomLayoutEmptyYes"),
                    _resourceLoader.Loc("GamepadKeyPickerCancel"));
                await ShowDialogAsync(dlg);
                if (!dlg.Result) return;
            }

            GamepadProfileStore.SaveCustomLayout(_editing.Bindings);
            Closed?.Invoke(this, EventArgs.Empty);
        }

        // ── 輔助方法 ──────────────────────────────────────────────────────────

        /// <summary>顯示子對話方塊。直接 ShowAsync（與其他對話方塊一致；DialogActiveChanged 舊機制已淘汰、無訂閱端）。</summary>
        private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dlg)
        {
            return await dlg.ShowAsync();
        }

        /// <summary>VK 顯示名稱（resw 經 VirtualKeys 表查；無對應回 VK# 字面）。</summary>
        private string KeyName(int vk)
        {
            var entry = VirtualKeys.FindByVk(vk);
            if (entry != null)
            {
                if (!string.IsNullOrEmpty(entry.ReswKey))
                {
                    string s = _resourceLoader.Loc(entry.ReswKey);
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
            if ((a.Mods & GamepadModifier.Ctrl) != 0) parts.Add(_resourceLoader.Loc("GamepadModifier_Ctrl"));
            if ((a.Mods & GamepadModifier.Shift) != 0) parts.Add(_resourceLoader.Loc("GamepadModifier_Shift"));
            if ((a.Mods & GamepadModifier.Alt) != 0) parts.Add(_resourceLoader.Loc("GamepadModifier_Alt"));
            if ((a.Mods & GamepadModifier.Win) != 0) parts.Add(_resourceLoader.Loc("GamepadModifier_Win"));
            parts.Add(KeyName(a.Vk));
            return string.Join("+", parts);
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

        /// <summary>appId 副文字（在地化 prefix + 完整識別值；process 類 → 「行程: <name>」、aumid 類 → 「AUMID: <full>」）。</summary>
        private string AppIdSubtitle(AppId appId)
        {
            if (appId == null) return string.Empty;
            string prefix = appId.Kind == IdKind.Aumid
                ? _resourceLoader.Loc("AppIdAumidPrefix")
                : _resourceLoader.Loc("AppIdProcessPrefix");
            return prefix + (appId.Value ?? string.Empty);
        }
    }
}
