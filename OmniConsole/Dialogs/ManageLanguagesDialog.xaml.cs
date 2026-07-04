using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   // SelectorItem（net10 Release 下清單容器的基底型別）
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
// 別名：把冗長的型別路徑縮成 LangStatus，讓 LangStatus.* 用法簡潔。
using LangStatus = OmniConsole.Services.TranslationRepoService.LanguageStatus;

namespace OmniConsole.Dialogs
{
    /// <summary>左欄一個語言列（顯示名 + 完整 repo 條目）。放 namespace 層級供 x:Bind 的 x:DataType 引用。</summary>
    internal sealed class LanguageRow
    {
        public string DisplayName { get; init; } = "";
        public TranslationRepoService.LanguageEntry Entry { get; init; } = null!;
    }

    /// <summary>
    /// 社群語言檔管理對話方塊：左欄列出 repo（目前 app 版本）可裝語言、右欄詳情 + 下載/更新/移除動作。
    /// 版面比照 ChangeKeyDialog 雙欄；async fetch index 故多載入/錯誤態。
    /// </summary>
    public sealed partial class ManageLanguagesDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>對話方塊關閉後，呼叫端據此決定是否需重掃語言資料夾 + 重新整理下拉選單（有任何下載/移除即 true）。</summary>
        public bool Changed { get; private set; }

        /// <summary>
        /// 更新了「目前使用中」語言 → 新譯文需重啟才生效；呼叫端據此在對話方塊關閉後彈重啟提示
        /// （兩個 ContentDialog 不能同時開，故不在本對話方塊內彈、交回呼叫端）。
        /// </summary>
        public bool NeedsRestartForCurrentLanguage { get; private set; }

        /// <summary>下載/移除進行中：鎖住對話方塊關閉（CloseButton/X/Esc/手把 B），避免操作中途被中斷。</summary>
        private bool _busy;

        /// <summary>建立社群語言管理對話方塊；設定標題/按鈕文字、掛事件（清單在 Opened 後才載入）。</summary>
        public ManageLanguagesDialog(XamlRoot xamlRoot)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            Title = _resourceLoader.Loc("ManageLanguages_Title");
            CloseButtonText = _resourceLoader.Loc("ManageLanguages_Close");

            LoadingText.Text = _resourceLoader.Loc("ManageLanguages_Loading");
            DetailEmptyHint.Text = _resourceLoader.Loc("ManageLanguages_SelectHint");

            LanguageList.SelectionChanged += LanguageList_SelectionChanged;

            Opened += OnOpened;
            Closing += OnClosing;
        }

        /// <summary>
        /// 焦點落指定目標並守衛：對抗「框架開啟時把預設焦點搶到樹中第一個可聚焦元素（左欄第一項）」。
        /// 目標達標判斷含其虛擬化清單項容器（右欄按鈕 或 左欄第一項，視目前語言而定）。
        /// </summary>
        private void GuardFocusOn(Microsoft.UI.Xaml.Controls.Control target)
        {
            GuardInitialFocus(() => IsFocusWithin(target), () => target.Focus(FocusStateHelper.Preferred));
            target.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>進行中時攔截所有關閉路徑（CloseButton/X/Esc）。手把 B 另由 OnB 覆寫攔截。</summary>
        private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (_busy) args.Cancel = true;
        }

        /// <summary>手把 B 鍵：進行中時擋掉（不關閉），否則走基底預設關閉。</summary>
        public override bool OnB()
        {
            if (_busy) return true;   // 吞掉、不關閉
            return base.OnB();
        }

        /// <summary>
        /// 手把 A 鍵：焦點在左欄語言項目時 → 先選中該項（換右欄內容）、再跳右欄 ActionButton（方便選完直接按動作）；
        /// 已在右欄按鈕上則走基底＝觸發按鈕。進行中（按鈕隱藏）不跳。
        /// SingleSelectionFollowsFocus=False：D-pad 移到的項目是「焦點」非「選中」，必須在跳右欄前顯式 SelectedItem
        /// 才會觸發 SelectionChanged→ShowDetail 換右欄（否則右欄停在舊選中項；滑鼠點選本就同時選中故無此問題）。
        /// </summary>
        public override void OnA()
        {
            if (!_busy
                && DetailPanel.Visibility == Visibility.Visible
                && ActionButton.Visibility == Visibility.Visible
                && IsFocusWithin(LanguageList))
            {
                // 取目前焦點所在的左欄項容器 → 反查 row → 設為選中（換右欄內容）。
                if (FocusedLanguageRow() is LanguageRow row && !ReferenceEquals(LanguageList.SelectedItem, row))
                    LanguageList.SelectedItem = row;   // 觸發 SelectionChanged → ShowDetail
                ActionButton.Focus(FocusStateHelper.Preferred);
                return;
            }
            base.OnA();
        }

        /// <summary>從目前焦點元素沿 visual tree 上溯找到左欄項容器（SelectorItem），反查對應 LanguageRow；找不到回 null。</summary>
        private LanguageRow? FocusedLanguageRow()
        {
            var node = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (node != null)
            {
                if (node is SelectorItem container)
                    return LanguageList.ItemFromContainer(container) as LanguageRow;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        /// <summary>目前焦點元素是否在指定容器內（含其虛擬化清單項容器）。</summary>
        private bool IsFocusWithin(DependencyObject container)
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (focused != null)
            {
                if (focused == container) return true;
                focused = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(focused);
            }
            return false;
        }

        /// <summary>對話方塊開啟後開始載入可裝語言清單。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _ = LoadAsync();
        }

        /// <summary>抓 index、填左欄；無語言/失敗顯訊息態。</summary>
        private async Task LoadAsync()
        {
            ShowPanel(loading: true, message: false, content: false);
            IReadOnlyList<TranslationRepoService.LanguageEntry> entries;
            try
            {
                entries = await TranslationRepoService.GetAvailableForCurrentVersionAsync();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ManageLang] load FAIL: {ex.Message}");
                ShowMessage(_resourceLoader.Loc("ManageLanguages_Error"));
                return;
            }

            if (entries.Count == 0)
            {
                ShowMessage(_resourceLoader.Loc("ManageLanguages_Empty"));
                return;
            }

            var rows = entries
                .Select(e => new LanguageRow { DisplayName = DescribeLanguage(e.Code), Entry = e })
                .OrderBy(r => r.DisplayName, StringComparer.CurrentCulture)
                .ToList();
            LanguageList.ItemsSource = rows;

            ShowPanel(loading: false, message: false, content: true);

            // 初始選取 + 焦點：
            // 1. 目前 UI 語言正是某個「已裝社群語言」→ 左欄選中該語言、焦點落右欄 ActionButton（方便直接按更新）。
            //    避開系統內建官方語言＝rows 只含社群語言（repo 清單）、官方/跟隨系統一律 FindIndex 回 -1 自動走第 2 點。
            // 2. 否則（全新 App / 目前是官方語言 / 沒裝社群）→ 左欄選第一項、焦點落左欄第一項。
            string current = SettingsService.GetUiLanguage();
            int sel = string.IsNullOrEmpty(current)
                ? -1
                : rows.FindIndex(r => string.Equals(r.Entry.Code, current, StringComparison.OrdinalIgnoreCase));

            DispatcherQueue.TryEnqueue(() =>
            {
                if (rows.Count == 0) return;
                if (sel >= 0)
                {
                    LanguageList.SelectedIndex = sel;        // 左欄選中目前語言（SelectionChanged → ShowDetail 填右欄）
                    ScrollIntoViewWhenReady(LanguageList, sel);        // 選中項捲進視野（焦點雖在右欄、左欄選中項仍可見）
                    // 焦點落右欄按鈕：用守衛對抗「框架開啟時搶左欄 index 0」（單純 Focus() 會被框架隨後奪走）。
                    GuardFocusOn(ActionButton);
                }
                else
                {
                    LanguageList.SelectedIndex = 0;
                    // 焦點落左欄第一項：用容器就緒聚焦（Focus ListView 本身會落容器非項目、白框不顯）。
                    FocusListItemWhenReady(LanguageList, 0);
                }
            });
        }

        /// <summary>左欄選取變更：有選中語言則更新右欄詳情，否則收合右欄。</summary>
        private void LanguageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LanguageList.SelectedItem is LanguageRow row)
                ShowDetail(row);
            else
                DetailPanel.Visibility = Visibility.Collapsed;
        }

        /// <summary>更新右欄詳情面板（語言名/譯者/狀態/動作鈕文字）。</summary>
        private void ShowDetail(LanguageRow row)
        {
            DetailEmptyHint.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            ActionMessage.Visibility = Visibility.Collapsed;   // 切換語言時清上次結果訊息（ActionButton_Click finally 之後會再重設）

            DetailLangName.Text = row.DisplayName;

            // 譯者：完整名單（右欄詳情不截斷；逗號相接）。無譯者則隱藏該行。
            if (row.Entry.Translators.Count > 0)
            {
                DetailTranslators.Text = string.Format(
                    _resourceLoader.Loc("ManageLanguages_Translators"),
                    string.Join(_resourceLoader.Loc("ManageLanguages_TranslatorSeparator"), row.Entry.Translators));
                DetailTranslators.Visibility = Visibility.Visible;
            }
            else
            {
                DetailTranslators.Visibility = Visibility.Collapsed;
            }

            var status = TranslationRepoService.GetStatus(row.Entry);
            DetailStatus.Text = status switch
            {
                LangStatus.Installed => _resourceLoader.Loc("ManageLanguages_Status_Installed"),
                LangStatus.UpdateAvailable => _resourceLoader.Loc("ManageLanguages_Status_UpdateAvailable"),
                _ => _resourceLoader.Loc("ManageLanguages_Status_NotInstalled"),
            };

            // 版本號：依狀態顯示 repo revision，已裝則帶本機已裝 revision 對比。
            int repoRev = row.Entry.Revision;
            int installedRev = TranslationProfileStore.GetInstalledRevision(row.Entry.Code);
            DetailRevision.Text = status switch
            {
                // 有更新：repo X（已安裝 Y），一眼看出差距
                LangStatus.UpdateAvailable => string.Format(_resourceLoader.Loc("ManageLanguages_Revision_UpdateAvailable"), repoRev, installedRev),
                // 已安裝且最新：repo X（已安裝）
                LangStatus.Installed => string.Format(_resourceLoader.Loc("ManageLanguages_Revision_Installed"), repoRev),
                // 未安裝：只顯 repo X
                _ => string.Format(_resourceLoader.Loc("ManageLanguages_Revision"), repoRev),
            };

            ActionButton.Content = status switch
            {
                LangStatus.Installed => _resourceLoader.Loc("ManageLanguages_Action_Uninstall"),
                LangStatus.UpdateAvailable => _resourceLoader.Loc("ManageLanguages_Action_Update"),
                _ => _resourceLoader.Loc("ManageLanguages_Action_Install"),
            };
            ActionButton.Tag = row;
            ActionButton.Visibility = Visibility.Visible;
        }

        /// <summary>下載 / 更新 / 移除（依目前狀態）。</summary>
        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActionButton.Tag is not LanguageRow row) return;
            var entry = row.Entry;
            var status = TranslationRepoService.GetStatus(entry);

            SetActionBusy(true);
            bool ok = false;
            bool wasRemove = status == LangStatus.Installed;
            try
            {
                if (wasRemove)
                {
                    // 已安裝 → 移除
                    if (TranslationRepoService.RemoveLanguage(entry.Code))
                    {
                        ok = true;
                        Changed = true;
                        // 移除善後：移除的是目前語言時，偏好寫空＝跟隨系統，防卡在指向已刪語言的錯誤狀態。
                        TranslationRepoService.OnLanguageRemoved(entry.Code);
                    }
                }
                else
                {
                    // 未安裝 / 有新版 → 下載（覆蓋）
                    LoadingText.Text = _resourceLoader.Loc("ManageLanguages_Downloading");
                    ok = await TranslationRepoService.DownloadLanguageAsync(
                        entry.Code, entry.Revision, entry.Translators);
                    if (ok)
                    {
                        Changed = true;
                        // 更新的是目前使用中語言時需重啟才生效；據此設旗標，呼叫端關閉對話方塊後彈重啟提示。
                        if (TranslationRepoService.NeedsRestartAfterUpdate(entry.Code, status == LangStatus.UpdateAvailable))
                            NeedsRestartForCurrentLanguage = true;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ManageLang] action FAIL {entry.Code}: {ex.Message}");
                ok = false;
            }
            finally
            {
                SetActionBusy(false);
                ShowDetail(row); // 重算狀態、重新整理按鈕文字
                // 結果訊息顯在按鈕下方（成功/失敗都顯；風格比照 CertificateDetailsDialog「已複製」提示）。
                ShowActionResult(ok
                    ? (wasRemove ? "ManageLanguages_Result_Removed" : "ManageLanguages_Result_Downloaded")
                    : "ManageLanguages_Result_Failed");
            }
        }

        /// <summary>切換動作進行中狀態：顯示進度圈、隱藏按鈕、停用左欄清單，並鎖住對話方塊關閉。</summary>
        private void SetActionBusy(bool busy)
        {
            _busy = busy;   // 鎖/解鎖對話方塊關閉（Closing 攔截 + OnB 攔 B 鍵）
            ActionButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            ActionProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ActionProgress.IsActive = busy;
            LanguageList.IsEnabled = !busy;
            if (busy) ActionMessage.Visibility = Visibility.Collapsed;   // 開新操作先清上次結果訊息
        }

        /// <summary>在按鈕下方顯示動作結果訊息（成功/失敗）。</summary>
        private void ShowActionResult(string resKey)
        {
            ActionMessage.Text = _resourceLoader.Loc(resKey);
            ActionMessage.Visibility = Visibility.Visible;
        }

        /// <summary>顯示訊息態（無語言/載入失敗），文字置於訊息面板。</summary>
        private void ShowMessage(string text)
        {
            MessageText.Text = text;
            ShowPanel(loading: false, message: true, content: false);
        }

        /// <summary>切換三種面板（載入中/訊息/內容）的顯示，三選一。</summary>
        private void ShowPanel(bool loading, bool message, bool content)
        {
            LoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            MessagePanel.Visibility = message ? Visibility.Visible : Visibility.Collapsed;
            ContentBorder.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>語言原名 + 代碼，如「日本語 (ja-JP)」；取不到原名（無效碼等）時 try/catch 回退純 BCP47 碼。</summary>
        private static string DescribeLanguage(string bcp47)
        {
            try
            {
                string native = CultureInfo.GetCultureInfo(bcp47).NativeName;
                if (!string.IsNullOrWhiteSpace(native)) return $"{native} ({bcp47})";
            }
            catch { /* 無效碼等：回退 BCP47 */ }
            return bcp47;
        }
    }
}
