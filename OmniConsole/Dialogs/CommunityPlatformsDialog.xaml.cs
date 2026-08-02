using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;   // SelectorItem（net10 Release 下清單容器的基底型別）
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace OmniConsole.Dialogs
{
    /// <summary>左欄一個平台列（縮圖 + 名稱 + 提交者）。放 namespace 層級供 x:Bind 的 x:DataType 引用。</summary>
    internal sealed class CommunityPlatformRow
    {
        public PlatformRepoService.CatalogEntry Entry { get; init; } = null!;
        public string DisplayName => Entry.DisplayName;
        public string SubmitterText { get; init; } = "";

        /// <summary>縮圖（先建空實例、封面快取路徑就緒後才設 UriSource；同實例換源、x:Bind 不需 INPC）。</summary>
        public BitmapImage Thumb { get; } = new() { DecodePixelWidth = 160 };

        /// <summary>封面本機快取路徑（縮圖/詳情預覽/新增共用同一個 task、同 id 只下載一次）。</summary>
        public Task<string> CoverPathTask { get; init; } = null!;
    }

    /// <summary>
    /// 社群平台瀏覽對話方塊：左欄列出 repo 上的社群平台（搜尋/排序）、右欄詳情 + 新增動作。
    /// Executable 條目本機路徑不存在時 Hide 自己請呼叫端開 FilePickerDialog 讓使用者指定位置（比照 PlatformEditDialog 的協調迴圈）。
    /// </summary>
    public sealed partial class CommunityPlatformsDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>清單排序模式。</summary>
        private enum SortMode { NewestFirst, OldestFirst, NameAsc, NameDesc }

        /// <summary>全部平台列（repo 目錄）；_items 是它經搜尋過濾與排序後的子集。</summary>
        private readonly List<CommunityPlatformRow> _allRows = [];

        /// <summary>
        /// ListView 繫結的固定實例集合：過濾/排序走 ObservableCollectionDiff 增量套用。
        ///（與貓又清單一致：容器回收、卡片有抽離/插入動態、不整批重建）。
        /// </summary>
        private readonly System.Collections.ObjectModel.ObservableCollection<CommunityPlatformRow> _items = [];
        private SortMode _sortMode = SortMode.NewestFirst;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchDebounceTimer;

        /// <summary>縮圖載入失敗時的預設底圖（與使用者平台無自訂圖時同一張、共用單一實例）</summary>
        private readonly BitmapImage _fallbackCover = new(new Uri(UserPlatformEntry.DefaultIconAsset));

        /// <summary>下載/新增進行中：鎖住對話方塊關閉（CloseButton/X/Esc/手把 B），避免操作中途被中斷。</summary>
        private bool _busy;

        /// <summary>對話方塊已載入過目錄（Hide/重開協調迴圈中不重新載入）。</summary>
        private bool _loadedOnce;

        // ── 指定位置（FilePickerDialog 協調）狀態 ────────────────────────────────

        /// <summary>要求呼叫端開啟檔案選擇器（比照 PlatformEditDialog；true 時本對話方塊已 Hide）。</summary>
        public bool RequestFilePicker { get; private set; }

        /// <summary>檔案選擇器的選項（RequestFilePicker=true 時有值）。</summary>
        public FilePickerOptions? FilePickerRequest { get; private set; }

        /// <summary>等待指定位置的平台列與選定結果（重開後於 Opened 續跑新增）</summary>
        private CommunityPlatformRow? _pendingLocateRow;
        private string? _pendingLocatePath;

        // ── 呼叫端讀取的結果 ────────────────────────────────────────────────────

        /// <summary>對話方塊關閉後，呼叫端據此決定是否需更新平台卡片（有任何新增即 true）。</summary>
        public bool Changed { get; private set; }

        /// <summary>本次對話方塊新增的所有 UserPlatformEntry Id（呼叫端逐一 UpdateOrInsertCard）。</summary>
        public List<string> AddedEntryIds { get; } = [];

        /// <summary>含 TextBox（搜尋），啟用螢幕鍵盤閃避。</summary>
        protected override bool EnableKeyboardAvoidanceOnOpen => true;

        /// <summary>建立社群平台對話方塊；設定標題/按鈕/工具列文字、掛事件（清單在 Opened 後才載入）。</summary>
        public CommunityPlatformsDialog(XamlRoot xamlRoot)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            Title = _resourceLoader.Loc("CommunityPlatforms_Title");
            CloseButtonText = _resourceLoader.Loc("ManageLanguages_Close");

            LoadingText.Text = _resourceLoader.Loc("CommunityPlatforms_Loading");
            DetailEmptyHint.Text = _resourceLoader.Loc("CommunityPlatforms_SelectHint");
            NoResultsHint.Text = _resourceLoader.Loc("CommunityPlatforms_NoResults");
            SearchBox.PlaceholderText = _resourceLoader.Loc("CommunityPlatforms_SearchPlaceholder");
            RefreshBarText.Text = _resourceLoader.Loc("CommunityPlatforms_CheckingUpdates");
            OfflineBarText.Text = _resourceLoader.Loc("CommunityPlatforms_OfflineNotice");
            AddButton.Content = _resourceLoader.Loc("CommunityPlatforms_Action_Add");

            PlatformList.ItemsSource = _items;   // 固定實例只綁一次（diff 增量更新的前提）

            // 排序下拉選單（code-behind 填、Content 走 .Loc 已在地化）。索引對應 SortCombo_SelectionChanged。
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("CommunityPlatforms_SortNewest") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("CommunityPlatforms_SortOldest") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortNameAsc") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortNameDesc") });
            SortCombo.SelectedIndex = 0;

            PlatformList.SelectionChanged += PlatformList_SelectionChanged;

            Opened += OnOpened;
        }

        // ── 手把（A/B/Y 語意） ─────────────────────────────────────────────────

        /// <summary>
        /// 手把 A 鍵：焦點在左欄平台項目時 → 先選中該項（換右欄）、再跳右欄 AddButton（方便選完直接按新增）；
        /// 已在右欄按鈕上則走基底＝觸發按鈕。進行中（按鈕隱藏）不跳。
        /// SingleSelectionFollowsFocus=False：D-pad 移到的項目是「焦點」非「選中」，必須顯式 SelectedItem 才換右欄。
        /// </summary>
        public override void OnA()
        {
            if (!_busy
                && DetailPanel.Visibility == Visibility.Visible
                && AddButton.Visibility == Visibility.Visible
                && IsFocusWithin(PlatformList))
            {
                if (FocusedRow() is CommunityPlatformRow row && !ReferenceEquals(PlatformList.SelectedItem, row))
                    PlatformList.SelectedItem = row;   // 觸發 SelectionChanged → ShowDetail
                AddButton.Focus(FocusStateHelper.Preferred);
                return;
            }
            base.OnA();
        }

        /// <summary>手把 Y 鍵：搜尋方塊與清單之間切換焦點（與貓又設定檔清單一致）。進行中不動作。</summary>
        public override void OnY()
        {
            if (_busy || ContentRoot.Visibility != Visibility.Visible) return;
            if (IsSearchBoxFocused) FocusList();
            else SearchBox.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>搜尋方塊是否擁有焦點（Y 鍵判斷焦點該往哪邊移動）。</summary>
        private bool IsSearchBoxFocused => SearchBox.FocusState != FocusState.Unfocused;

        /// <summary>把焦點設回清單（選中項優先、否則第一項）。</summary>
        private void FocusList()
        {
            int index = PlatformList.SelectedIndex >= 0 ? PlatformList.SelectedIndex : 0;
            if (PlatformList.Items.Count > 0)
                FocusListItemWhenReady(PlatformList, index);
        }

        /// <summary>
        /// 搜尋方塊鍵盤輸入：D-pad 向下進入清單第一項（標記 Handled）。
        /// 手把 B 不在此攔（走 pull 模型 OnB＝退出對話方塊）；Escape 交基底＝關對話方塊，與 B 一致。
        /// </summary>
        private void SearchBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.GamepadLeftThumbstickDown)
            {
                if (PlatformList.Items.Count > 0)
                {
                    PlatformList.SelectedIndex = 0;
                    FocusListItemWhenReady(PlatformList, 0);
                    e.Handled = true;
                }
            }
        }

        /// <summary>從目前焦點元素沿 visual tree 上溯找到左欄項容器（SelectorItem），反查對應平台列；找不到回 null。</summary>
        private CommunityPlatformRow? FocusedRow()
        {
            var node = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            while (node != null)
            {
                if (node is SelectorItem container)
                    return PlatformList.ItemFromContainer(container) as CommunityPlatformRow;
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

        // ── 載入 ──────────────────────────────────────────────────────────────

        /// <summary>對話方塊開啟後：首次載入目錄；指定位置流程重開時改續跑新增。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _ = LoadAsync();
                return;
            }
            _ = ResumeAfterLocateAsync();
        }

        /// <summary>抓 repo 目錄、建平台列（縮圖 BitmapImage 直吃 https、失敗換預設底圖）、填左欄並設初始焦點。</summary>
        private async Task LoadAsync()
        {
            ShowPanel(loading: true, message: false, content: false);

            // 背景保鮮結束時收合檢查提示、有新 index 則當場套用（免等第二次開）；回呼在背景執行緒、marshal 回 UI。
            var catalog = await PlatformRepoService.GetCatalogAsync(
                onRefreshCompleted: (status, updated) =>
                    DispatcherQueue.TryEnqueue(() => OnRefreshCompleted(status, updated)));

            if (!catalog.Fetched)
            {
                ShowMessage(_resourceLoader.Loc("CommunityPlatforms_Error"));
                return;
            }
            if (catalog.SchemaTooNew)
            {
                ShowMessage(_resourceLoader.Loc("CommunityPlatforms_SchemaTooNew"));
                return;
            }
            if (catalog.Entries.Count == 0)
            {
                ShowMessage(_resourceLoader.Loc("CommunityPlatforms_Empty"));
                return;
            }

            PopulateRows(catalog.Entries);

            ShowPanel(loading: false, message: false, content: true);
            if (catalog.FromCache) ShowRefreshBar();   // 背景保鮮進行中：顯示檢查提示（結束時 OnRefreshCompleted 收合）
            ApplyFilterAndSort();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (PlatformList.Items.Count == 0) return;
                PlatformList.SelectedIndex = 0;
                // 焦點落左欄第一項：用容器就緒聚焦（Focus ListView 本身會落容器非項目、白框不顯）；
                // 併用守衛對抗「ContentDialog 框架開啟時把焦點搶到樹中第一個可聚焦元素（搜尋方塊）」。
                GuardInitialFocus(
                    () => IsFocusWithin(PlatformList),
                    () => FocusListItemWhenReady(PlatformList, 0));
                FocusListItemWhenReady(PlatformList, 0);
            });
        }

        /// <summary>用目錄條目建平台列填 _allRows（縮圖走本機封面快取、命中即立即）。</summary>
        private void PopulateRows(IReadOnlyList<PlatformRepoService.CatalogEntry> entries)
        {
            string submitterFormat = _resourceLoader.Loc("CommunityPlatforms_Submitter");
            foreach (var entry in entries)
            {
                var row = new CommunityPlatformRow
                {
                    Entry = entry,
                    SubmitterText = string.Format(submitterFormat, entry.Submitter),
                    CoverPathTask = PlatformRepoService.GetCoverPathAsync(entry),
                };
                _allRows.Add(row);
                _ = LoadThumbAsync(row);
            }
        }

        /// <summary>
        /// 背景保鮮結束：收合檢查提示，依結果狀態分流：有新 index 當場套用、
        /// 已是最新則安靜收合、失敗（離線）則亮離線條並保留快取清單（仍可新增）。
        /// </summary>
        private void OnRefreshCompleted(PlatformRepoService.RefreshStatus status, PlatformRepoService.Catalog? updated)
        {
            HideRefreshBar();
            switch (status)
            {
                case PlatformRepoService.RefreshStatus.Updated:
                    HideOfflineBar();   // 重新連上且有更新
                    if (updated is { } catalog) OnCatalogRefreshed(catalog);
                    break;
                case PlatformRepoService.RefreshStatus.UpToDate:
                    HideOfflineBar();   // 重新連上、目錄已是最新
                    break;
                case PlatformRepoService.RefreshStatus.Failed:
                    ShowOfflineBar();   // 離線：保留快取清單、提示可能非最新（新增仍可用）
                    break;
            }
        }

        /// <summary>顯示離線提示條（背景保鮮失敗＝離線；清單留快取版、仍可新增）。</summary>
        private void ShowOfflineBar() => OfflineBar.Visibility = Visibility.Visible;

        /// <summary>收合離線提示條（重新連上）。</summary>
        private void HideOfflineBar() => OfflineBar.Visibility = Visibility.Collapsed;

        /// <summary>顯示「正在檢查平台目錄更新」提示條。</summary>
        private void ShowRefreshBar()
        {
            RefreshBarRing.IsActive = true;
            RefreshBar.Visibility = Visibility.Visible;
        }

        /// <summary>收合檢查更新提示條。</summary>
        private void HideRefreshBar()
        {
            RefreshBarRing.IsActive = false;
            RefreshBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 背景保鮮發現 repo index 更新時當場套用：schema 過新/清單變空即時切訊息態（不留到下次開啟）、
        /// 有清單則重建平台列並保留搜尋/排序與目前選中平台。進行中（新增中）不打斷、此輪更新下次開啟生效。
        /// </summary>
        private void OnCatalogRefreshed(PlatformRepoService.Catalog catalog)
        {
            if (_busy) return;

            if (catalog.SchemaTooNew)
            {
                ShowMessage(_resourceLoader.Loc("CommunityPlatforms_SchemaTooNew"));
                return;
            }
            if (catalog.Entries.Count == 0)
            {
                ShowMessage(_resourceLoader.Loc("CommunityPlatforms_Empty"));
                return;
            }

            string? selectedId = (PlatformList.SelectedItem as CommunityPlatformRow)?.Entry.Id;

            _allRows.Clear();
            PopulateRows(catalog.Entries);
            ShowPanel(loading: false, message: false, content: true);   // 原在訊息態（空清單→有平台）也切回內容
            ApplyFilterAndSort();

            // 還原選中：原選中平台仍在 → 選回它；否則清單首項（SelectionChanged 換右欄）。
            if (selectedId != null && _items.FirstOrDefault(r => r.Entry.Id == selectedId) is { } keep)
                PlatformList.SelectedItem = keep;
            else if (_items.Count > 0)
                PlatformList.SelectedIndex = 0;
        }

        // ── 搜尋 / 排序 ────────────────────────────────────────────────────────

        /// <summary>搜尋方塊文字變動：150ms debounce 後重過濾清單。</summary>
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = this.DispatcherQueue.CreateTimer();
                _searchDebounceTimer.Tick += (s, args) =>
                {
                    _searchDebounceTimer!.Stop();
                    ApplyFilterAndSort();
                };
            }
            _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(150);
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        /// <summary>排序下拉選單變動：依目前 SelectedIndex 對應排序模式重排清單。</summary>
        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _sortMode = SortCombo.SelectedIndex switch
            {
                0 => SortMode.NewestFirst,
                1 => SortMode.OldestFirst,
                2 => SortMode.NameAsc,
                3 => SortMode.NameDesc,
                _ => SortMode.NewestFirst,
            };
            ApplyFilterAndSort();
        }

        /// <summary>依目前搜尋字串（名稱/提交者）與排序模式重填清單；保留仍在清單中的選中項、切換「無結果」提示。</summary>
        private void ApplyFilterAndSort()
        {
            string query = SearchBox.Text?.Trim() ?? "";
            IEnumerable<CommunityPlatformRow> filtered = string.IsNullOrEmpty(query)
                ? _allRows
                : _allRows.Where(r =>
                    r.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || r.Entry.Submitter.Contains(query, StringComparison.CurrentCultureIgnoreCase));

            // 時間排序用 added 加入時戳（ISO 8601 含時分秒、字典序即時間序、同日不並列）；同時戳以 id 打破平手
            var sorted = _sortMode switch
            {
                SortMode.NewestFirst => filtered.OrderByDescending(r => r.Entry.Added, StringComparer.Ordinal).ThenBy(r => r.Entry.Id, StringComparer.Ordinal),
                SortMode.OldestFirst => filtered.OrderBy(r => r.Entry.Added, StringComparer.Ordinal).ThenBy(r => r.Entry.Id, StringComparer.Ordinal),
                SortMode.NameAsc => filtered.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                SortMode.NameDesc => filtered.OrderByDescending(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                _ => filtered.OrderByDescending(r => r.Entry.Added, StringComparer.Ordinal),
            };

            // 增量 diff 套進固定實例集合（與貓又清單一致）：容器回收、搜尋時卡片有抽離/插入動態、
            // 仍在清單中的選中項自動保留（項目被過濾掉時 ListView 清選取 → SelectionChanged 收右欄）。
            // 內容比對用實例同一性：搜尋/排序路徑的目標清單與現有清單是同實例（不 Replace）；
            // 背景保鮮重建 _allRows 後是新實例 → 同位 Replace 讓容器重新繫結（改名/換封面即時反映）。
            ObservableCollectionDiff.Apply(
                _items,
                sorted.ToList(),
                static (a, b) => a.Entry.Id == b.Entry.Id,
                static (a, b) => ReferenceEquals(a, b));

            NoResultsHint.Visibility = (_items.Count == 0 && !string.IsNullOrEmpty(query))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── 詳情 ──────────────────────────────────────────────────────────────

        /// <summary>左欄選取變更：有選中平台則更新右欄詳情，否則收合右欄。</summary>
        private void PlatformList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformList.SelectedItem is CommunityPlatformRow row)
                ShowDetail(row);
            else
            {
                DetailPanel.Visibility = Visibility.Collapsed;
                DetailEmptyHint.Visibility = Visibility.Visible;
            }
        }

        /// <summary>更新右欄詳情面板（封面預覽/名稱/啟動目標全文/提交者/未偵測提示）。</summary>
        private void ShowDetail(CommunityPlatformRow row)
        {
            DetailEmptyHint.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;
            HideResultBars();   // 切換平台時收上次結果提示條

            var entry = row.Entry;
            DetailName.Text = entry.DisplayName;

            // 封面預覽獨立解碼（280px 預覽 ×2 DPI 上限），來源同縮圖走本機快取
            var cover = new BitmapImage { DecodePixelWidth = 560 };
            DetailCover.Source = cover;
            _ = SetCoverWhenReadyAsync(cover, row);

            // 啟動目標全文顯示（使用者新增前有權看清楚要跑什麼）
            DetailTarget.Text = entry.LaunchType switch
            {
                "Executable" => string.IsNullOrEmpty(entry.Arguments) ? entry.LaunchTarget : $"{entry.LaunchTarget} {entry.Arguments}",
                "PackagedApp" => entry.PackageFamilyName,
                _ => entry.LaunchTarget,
            };

            DetailSubmitter.Text = string.IsNullOrEmpty(entry.Added)
                ? row.SubmitterText
                : string.Format(_resourceLoader.Loc("CommunityPlatforms_SubmitterDate"), entry.Submitter, entry.AddedDate);

            // 本機偵測提示（不阻擋）：Executable 路徑不存在（新增時走指定位置分支）；
            // PackagedApp 未安裝或 ProtocolUri 無已登錄的 handler（新增時被驗證閘門擋下、與提示同訊息）。
            switch (entry.LaunchType)
            {
                case "Executable" when !File.Exists(EnvironmentPathHelper.Expand(entry.LaunchTarget)):
                    DetailWarningText.Text = _resourceLoader.Loc("CommunityPlatforms_NotDetected");
                    DetailWarning.Visibility = Visibility.Visible;
                    break;
                case "PackagedApp" when !PlatformFieldValidator.IsPackageFamilyNameInstalled(entry.PackageFamilyName):
                    DetailWarningText.Text = _resourceLoader.Loc("Import_Error_PackageNotInstalled");
                    DetailWarning.Visibility = Visibility.Visible;
                    break;
                case "ProtocolUri":
                    DetailWarning.Visibility = Visibility.Collapsed;   // 先收、查詢是非同步、回來仍選同列才顯示
                    _ = ShowUriWarningWhenReadyAsync(row);
                    break;
                default:
                    DetailWarning.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>非同步查 URI 是否有已登錄 handler，沒有且使用者仍選著同一列時顯示提示（與新增時的錯誤同訊息）。</summary>
        private async Task ShowUriWarningWhenReadyAsync(CommunityPlatformRow row)
        {
            bool supported;
            try { supported = await ProcessLauncherService.IsUriSupportedAsync(row.Entry.LaunchTarget); }
            catch { supported = true; }   // 查詢失敗視為支援、不誤報警告，把關交給新增時的驗證閘門
            if (!supported && ReferenceEquals(PlatformList.SelectedItem, row))
            {
                DetailWarningText.Text = _resourceLoader.Loc("Import_Error_UriNotSupported");
                DetailWarning.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 等封面快取路徑就緒後把目標 BitmapImage 指向本機檔；失敗吃預設底圖。
        /// 選取快速切換時舊的詳情預覽實例已被換掉、遲到的賦值無害。
        /// </summary>
        private async Task SetCoverWhenReadyAsync(BitmapImage target, CommunityPlatformRow row)
        {
            string path;
            try { path = await row.CoverPathTask; }
            catch { path = ""; }
            target.UriSource = path.Length > 0 ? new Uri(path) : new Uri(UserPlatformEntry.DefaultIconAsset);
        }

        /// <summary>等封面快取路徑就緒後補上縮圖來源（同實例換 UriSource、x:Bind 不需 INPC）。</summary>
        private async Task LoadThumbAsync(CommunityPlatformRow row)
            => await SetCoverWhenReadyAsync(row.Thumb, row);

        /// <summary>縮圖/預覽載入失敗（離線、repo 圖 404）：換預設底圖。</summary>
        private void CoverImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            if (sender is Image image) image.Source = _fallbackCover;
        }

        // ── 新增流程 ──────────────────────────────────────────────────────────

        /// <summary>新增按鈕：Executable 且本機找不到目標時走指定位置分支，否則直接驗證 + 新增。</summary>
        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlatformList.SelectedItem is not CommunityPlatformRow row) return;

            if (row.Entry.LaunchType == "Executable" && !File.Exists(EnvironmentPathHelper.Expand(row.Entry.LaunchTarget)))
            {
                RequestLocate(row);
                return;
            }
            await DoAddAsync(row, overrideLaunchTarget: null);
        }

        /// <summary>
        /// 請求呼叫端開檔案選擇器讓使用者指定 exe 位置：Hide 自己、由呼叫端協調 FilePickerDialog。
        /// 列全部 .exe、不按目標檔名篩（與系統選擇器後備同語意、兩條路一致不混淆）。
        /// </summary>
        private void RequestLocate(CommunityPlatformRow row)
        {
            _pendingLocateRow = row;
            _pendingLocatePath = null;
            FilePickerRequest = new FilePickerOptions
            {
                FileTypeFilters = [".exe"],
                InitialDirectory = null,
                ShowImagePreview = false,
                FilterDisplayName = _resourceLoader.Loc("FilePickerDialog_FilterExe"),
            };
            RequestFilePicker = true;
            Hide();
        }

        /// <summary>
        /// 接收檔案選擇器的結果（由呼叫端在重開前呼叫）。
        /// 所選檔案一律接受（自製選擇器已按目標檔名篩選；系統選擇器後備或改名的 portable 版選了不同檔名
        /// 屬使用者的明確選擇，重點是把社群條目的啟動參數帶上）。
        /// null（取消選擇）＝取消本次新增（防 FSE Loop、不寫入指向不存在目標的壞資料）。
        /// </summary>
        public void ApplyFilePickerResult(string? filePath)
        {
            RequestFilePicker = false;
            FilePickerRequest = null;

            if (filePath == null)
            {
                _pendingLocateRow = null;   // 取消＝取消本次新增
                return;
            }
            _pendingLocatePath = filePath;
        }

        /// <summary>指定位置流程重開後續跑：有選檔→以使用者路徑新增；取消→不動作。</summary>
        private async Task ResumeAfterLocateAsync()
        {
            var row = _pendingLocateRow;
            _pendingLocateRow = null;

            if (row == null)
            {
                FocusList();
                return;
            }

            // 還原選中與焦點到操作中的平台（Hide/重開後焦點懸空）
            PlatformList.SelectedItem = row;
            AddButton.Focus(FocusStateHelper.Preferred);

            string? path = _pendingLocatePath;
            _pendingLocatePath = null;
            if (path != null)
                await DoAddAsync(row, path);
        }

        /// <summary>驗證（與貼 JSON 匯入同一道閘門）→ 下載封面（容錯）→ 寫入 UserPlatformStore → 顯示結果。</summary>
        private async Task DoAddAsync(CommunityPlatformRow row, string? overrideLaunchTarget)
        {
            SetActionBusy(true);
            try
            {
                var (entry, errorKey) = await PlatformRepoService.ValidateAndCreateEntryAsync(row.Entry, overrideLaunchTarget);
                if (errorKey is not null || entry is null)
                {
                    ShowActionResult(_resourceLoader.Loc(errorKey ?? "Import_Error_InvalidJson"), InfoBarSeverity.Error);
                    return;
                }

                // 封面容錯：下載/匯入失敗回空字串、平台照樣新增（吃預設底圖、事後可自行編輯換圖）
                entry.IconFileName = await PlatformRepoService.DownloadCoverAsync(row.Entry);

                UserPlatformStore.Add(entry);   // entry.Id 若空會在此被填上
                AddedEntryIds.Add(entry.Id);
                Changed = true;
                ShowActionResult(string.Format(_resourceLoader.Loc("CommunityPlatforms_Result_Added"), row.Entry.DisplayName), InfoBarSeverity.Success);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[CommunityPlat] add FAIL {row.Entry.Id}: {ex.Message}");
                ShowActionResult(_resourceLoader.Loc("ManageLanguages_Result_Failed"), InfoBarSeverity.Error);
            }
            finally
            {
                SetActionBusy(false);
            }
        }

        /// <summary>切換動作進行中狀態：顯示進度圈、隱藏按鈕、停用清單與工具列，並鎖住對話方塊關閉。</summary>
        private void SetActionBusy(bool busy)
        {
            _busy = busy;
            // 進行中鎖住所有關閉路徑（關閉按鈕/X/Esc/手把 B 都由基底依此攔下）
            DismissPolicy = busy ? DialogDismissPolicy.Block : DialogDismissPolicy.Allow;
            AddButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
            ActionProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            ActionProgress.IsActive = busy;
            PlatformList.IsEnabled = !busy;
            SearchBox.IsEnabled = !busy;
            SortCombo.IsEnabled = !busy;
            // busy 不收結果提示條：結束時 ShowActionResult 會原地更新內容。
        }

        /// <summary>
        /// 在頂部提示條顯示動作結果：三態（成功/警告/失敗），
        /// 此處只切三選一顯隱與設文字。同一條已顯示時原地更新（不收合重現＝不抖動）；
        /// 每次顯示播放透明度脈衝（重複操作也有「又完成一次」的視覺回饋）。
        /// </summary>
        private void ShowActionResult(string text, InfoBarSeverity severity)
        {
            HideRefreshBar();   // 結果優先：檢查提示讓位（保鮮結束時的收合對已隱藏者為 no-op）
            var (bar, textBlock) = severity switch
            {
                InfoBarSeverity.Success => (ResultBarSuccess, ResultTextSuccess),
                InfoBarSeverity.Error => (ResultBarError, ResultTextError),
                _ => (ResultBarWarning, ResultTextWarning),
            };
            if (bar.Visibility != Visibility.Visible)
            {
                HideResultBars();   // 換不同態才收其他條
                bar.Visibility = Visibility.Visible;
            }
            textBlock.Text = text;
            PulseResultBar(bar);
        }

        /// <summary>對提示條播放透明度脈衝（0.3→1、250ms）：首次顯示＝淡入感、重複結果＝明確的完成回饋，不位移不收合。</summary>
        private static void PulseResultBar(Border bar)
        {
            var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.3,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, bar);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        /// <summary>收合三態結果提示條（切換平台、開新操作、顯示新結果前呼叫）。</summary>
        private void HideResultBars()
        {
            ResultBarSuccess.Visibility = Visibility.Collapsed;
            ResultBarWarning.Visibility = Visibility.Collapsed;
            ResultBarError.Visibility = Visibility.Collapsed;
        }

        /// <summary>顯示訊息態（離線/無平台/需更新 App），文字置於訊息面板。</summary>
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
            ContentRoot.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
