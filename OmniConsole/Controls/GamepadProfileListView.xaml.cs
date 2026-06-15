using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.System;

namespace OmniConsole.Controls
{
    /// <summary>x:Bind 用的清單列項目（顯示名稱 + appId 副文字）。</summary>
    public sealed class GamepadProfileRow
    {
        /// <summary>App 識別。</summary>
        public AppId AppId { get; set; } = new AppId();
        /// <summary>第一行顯示名稱。</summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>第二行副文字（process: xxx 或 aumid: xxx）。</summary>
        public string SubText { get; set; } = string.Empty;
        /// <summary>FullPath 取出的資料夾名（搜尋比對用，process 類才有）。</summary>
        public string FolderName { get; set; } = string.Empty;
    }

    /// <summary>清單排序模式。</summary>
    public enum GamepadProfileSortMode
    {
        /// <summary>新增順序：新→舊（_allItems 倒序）。</summary>
        AddOrderDesc,
        /// <summary>新增順序：舊→新（_allItems 原序）。</summary>
        AddOrderAsc,
        /// <summary>名稱：A→Z。</summary>
        NameAsc,
        /// <summary>名稱：Z→A。</summary>
        NameDesc
    }

    /// <summary>手把映射「清單頁」UserControl：列出所有 per-App profile，提供搜尋／排序／編輯／刪除入口。</summary>
    public sealed partial class GamepadProfileListView : UserControl
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 原始清單，依 GamepadProfileStore.Load() 自然順序載入
        private readonly List<GamepadProfileRow> _allItems = new List<GamepadProfileRow>();
        // ListView 繫結的可見集合，是 _allItems 經搜尋過濾與排序後的子集
        private readonly ObservableCollection<GamepadProfileRow> _items = new ObservableCollection<GamepadProfileRow>();

        private GamepadProfileSortMode _sortMode = GamepadProfileSortMode.AddOrderDesc;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _searchDebounceTimer;
        // 編輯器入口呼叫前記錄目標 AppId，CloseEditor 回來時用以將焦點還原到對應 row
        private AppId? _lastEditedAppId;

        /// <summary>使用者要編輯某 profile（A 鍵或滑鼠點列項）時觸發。</summary>
        public event EventHandler<AppId>? EditRequested;

        /// <summary>清單內容變動（Refresh 後）時觸發，宿主據此重評底部手把提示可見性。</summary>
        public event EventHandler? ItemsChanged;

        /// <summary>繫結 ListView ItemsSource 為內部 ObservableCollection，並掛 SelectorItem 焦點事件以同步 SelectedItem。</summary>
        public GamepadProfileListView()
        {
            InitializeComponent();
            ProfileList.ItemsSource = _items;
            ProfileList.ContainerContentChanging += ProfileList_ContainerContentChanging;
            ProfileList.GotFocus += ProfileList_GotFocus;

            // 填充排序下拉（code-behind 填、Content 走 .Loc 已在地化；不用 XAML 宣告 x:Uid 項目）。
            // 索引對應 SortCombo_SelectionChanged：0=AddOrderDesc 1=AddOrderAsc 2=NameAsc 3=NameDesc。
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortAddOrderDesc") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortAddOrderAsc") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortNameAsc") });
            SortCombo.Items.Add(new ComboBoxItem { Content = _resourceLoader.Loc("GamepadMappingSortNameDesc") });
            SortCombo.SelectedIndex = 0;
            Unloaded += GamepadProfileListView_Unloaded;
        }

        /// <summary>卸載時停止 debounce 計時器。</summary>
        private void GamepadProfileListView_Unloaded(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer?.Stop();
        }

        /// <summary>每次容器產生或重用時，只在 index 0 容器設 XYFocusUp 指向搜尋方塊，其餘讓 ListView 自己處理往上跳前一項。</summary>
        private void ProfileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is SelectorItem item)
                item.XYFocusUp = args.ItemIndex == 0 ? (DependencyObject)SearchBox : null;
        }

        /// <summary>
        /// 清單項取得焦點時同步 SelectedItem，讓 selected background 跟 selection indicator 走。
        /// </summary>
        private void ProfileList_GotFocus(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is SelectorItem item && item.Content is GamepadProfileRow row)
                ProfileList.SelectedItem = row;
        }

        /// <summary>是否有任何 profile（給宿主控制底部 (X) 提示顯示）。</summary>
        public bool HasItems => _items.Count > 0;

        /// <summary>搜尋方塊是否擁有焦點（給宿主 Y 鍵判斷焦點該往哪邊移動）。</summary>
        public bool IsSearchBoxFocused => SearchBox.FocusState != FocusState.Unfocused;

        /// <summary>從 GamepadProfileStore 重抓並重新整理清單；空清單時顯示提示、隱藏卡片。</summary>
        public void Refresh()
        {
            _allItems.Clear();
            try
            {
                var profiles = GamepadProfileStore.Load();
                foreach (var p in profiles)
                {
                    _allItems.Add(new GamepadProfileRow
                    {
                        AppId = p.AppId,
                        DisplayName = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : p.AppId.Value,
                        SubText = AppIdSubtitle(p.AppId),
                        FolderName = p.AppId.Kind == IdKind.Process ? AppId.ExtractFolderName(p.AppId.FullPath) : string.Empty
                    });
                }
            }
            catch { }

            ApplyFilterAndSort();

            bool hasItems = _allItems.Count > 0;
            ToolbarCard.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            ListCard.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            if (!hasItems) TryLoadMascot();
            ItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>依目前搜尋字串與排序模式重填 _items；同時切換「無搜尋結果」提示可見性。</summary>
        private void ApplyFilterAndSort()
        {
            string query = SearchBox.Text?.Trim() ?? string.Empty;
            IEnumerable<GamepadProfileRow> filtered = _allItems;

            if (!string.IsNullOrEmpty(query))
            {
                filtered = _allItems.Where(r =>
                    r.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (r.AppId.Value ?? string.Empty).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || r.FolderName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
            }

            IEnumerable<GamepadProfileRow> sorted = _sortMode switch
            {
                GamepadProfileSortMode.AddOrderDesc => filtered.Reverse(),
                GamepadProfileSortMode.AddOrderAsc => filtered,
                GamepadProfileSortMode.NameAsc => filtered.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                GamepadProfileSortMode.NameDesc => filtered.OrderByDescending(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase),
                _ => filtered
            };

            DiffApply(sorted);

            NoSearchResultHint.Visibility = (_items.Count == 0 && !string.IsNullOrEmpty(query))
                ? Visibility.Visible : Visibility.Collapsed;

            if (_items.Count > 0 && ProfileList.SelectedIndex < 0)
                ProfileList.SelectedIndex = 0;
        }

        /// <summary>
        /// 以 Id 為鍵（AppId.MatchesExact）做增量比對，把 <paramref name="newRows"/> 套進固定實例 _items，
        /// 取代過去 _items.Clear()+逐項 Add（Clear 發 Reset 讓 ListView 整批重建容器 → 刪中間會閃）。
        /// 增量演算法在 <see cref="ObservableCollectionDiff.Apply{T}"/>，此處只提供身分／內容比對。
        /// </summary>
        private void DiffApply(IEnumerable<GamepadProfileRow> newRows)
        {
            var target = newRows as IReadOnlyList<GamepadProfileRow> ?? newRows.ToList();
            ObservableCollectionDiff.Apply(
                _items,
                target,
                static (a, b) => a.AppId.MatchesExact(b.AppId),
                static (a, b) => a.DisplayName == b.DisplayName
                              && a.SubText == b.SubText
                              && a.FolderName == b.FolderName);
        }

        /// <summary>搜尋方塊文字變動：150ms debounce 後呼叫 ApplyFilterAndSort。</summary>
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

        /// <summary>搜尋方塊鍵盤輸入：B / Escape 跳回清單，D-pad 向下進入清單第一項，皆標記 Handled。</summary>
        private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.GamepadB)
            {
                FocusList();
                e.Handled = true;
                return;
            }
            if (e.Key == VirtualKey.GamepadDPadDown || e.Key == VirtualKey.GamepadLeftThumbstickDown)
            {
                if (_items.Count > 0)
                {
                    FocusFirstListItem();
                    e.Handled = true;
                }
            }
        }

        /// <summary>聚焦到 index 0 的 SelectorItem container；container 未實體化時掛 LayoutUpdated 延後聚焦。</summary>
        private void FocusFirstListItem()
        {
            if (_items.Count == 0) return;
            ProfileList.SelectedIndex = 0;
            ProfileList.ScrollIntoView(_items[0]);
            if (ProfileList.ContainerFromIndex(0) is SelectorItem lvi)
            {
                lvi.Focus(FocusStateHelper.Preferred);
            }
            else
            {
                EventHandler<object>? handler = null;
                handler = (s, e) =>
                {
                    if (ProfileList.ContainerFromIndex(0) is SelectorItem deferred)
                    {
                        deferred.Focus(FocusStateHelper.Preferred);
                        ProfileList.LayoutUpdated -= handler;
                    }
                };
                ProfileList.LayoutUpdated += handler;
            }
        }

        /// <summary>排序下拉選單變動：依目前 SelectedIndex 對應 SortMode 重排清單。</summary>
        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _sortMode = SortCombo.SelectedIndex switch
            {
                0 => GamepadProfileSortMode.AddOrderDesc,
                1 => GamepadProfileSortMode.AddOrderAsc,
                2 => GamepadProfileSortMode.NameAsc,
                3 => GamepadProfileSortMode.NameDesc,
                _ => GamepadProfileSortMode.AddOrderDesc
            };
            ApplyFilterAndSort();
        }

        /// <summary>EmptyState 區大小變動時呼叫 ResizeMascot 重算尺寸。</summary>
        private void EmptyState_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ResizeMascot();
        }

        /// <summary>
        /// 以 EmptyState 容器（撐滿 Grid Row 2）的可用高度為基準設定 MascotBorder 尺寸，
        /// 扣掉 EmptyHint 文字與內距後套 16:9 比例，並以 UserControl 寬度為上限。
        /// EmptyState 是 Stretch 的 Grid（非內容驅動高度），ActualHeight 即 Row 2 真實可用空間，
        /// 立繪才不會反向撐出容器高度造成循環縮小。內層 StackPanel 負責把立繪+提示置中。
        /// </summary>
        private void ResizeMascot()
        {
            const double aspectW = 16;
            const double aspectH = 9;
            const double minH = 270;
            const double safetyMargin = 24;
            // 立繪與提示文字之間的間距（內層 StackPanel.Spacing）
            const double innerSpacing = 16;

            double cellH = EmptyState.ActualHeight;
            if (cellH <= 0) cellH = this.ActualHeight;
            if (cellH <= 0) return;

            // 扣掉提示文字與其上方間距，剩下才是立繪可用高度
            double reserved = EmptyHint.ActualHeight + innerSpacing + safetyMargin;
            double availH = cellH - reserved;
            if (availH < minH) availH = minH;

            double targetH = availH;
            double targetW = targetH * aspectW / aspectH;

            double ucW = this.ActualWidth - 40;
            if (ucW > 0 && targetW > ucW)
            {
                targetW = ucW;
                targetH = targetW * aspectH / aspectW;
            }

            MascotBorder.Height = targetH;
            MascotBorder.Width = targetW;

            DebugLogger.Log($"[Mascot] resize: cell={cellH:F0}, reserved={reserved:F0}, target={targetW:F0}x{targetH:F0}");
        }

        /// <summary>從 embedded resource 載入 mascot 立繪到 MascotImage；資源不存在或載入失敗時將 MascotBorder 設為 Collapsed。</summary>
        private async void TryLoadMascot()
        {
            if (MascotImage.Source != null) return;
            try
            {
                var asm = typeof(GamepadProfileListView).Assembly;
                using var stream = asm.GetManifestResourceStream("OmniConsole.Embedded.Nekomata.jpg");
                if (stream == null)
                {
                    DebugLogger.Log("[Mascot] embedded resource not found");
                    MascotBorder.Visibility = Visibility.Collapsed;
                    return;
                }

                using var memStream = new System.IO.MemoryStream();
                await stream.CopyToAsync(memStream);
                memStream.Position = 0;

                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                bmp.ImageFailed += (s, e) =>
                {
                    DebugLogger.Log($"[Mascot] load failed: {e.ErrorMessage}");
                    MascotBorder.Visibility = Visibility.Collapsed;
                };
                bmp.ImageOpened += (s, e) =>
                {
                    DebugLogger.Log($"[Mascot] loaded {bmp.PixelWidth}x{bmp.PixelHeight}");
                    MascotBorder.Visibility = Visibility.Visible;
                    ResizeMascot();
                };
                MascotImage.Source = bmp;
                await bmp.SetSourceAsync(memStream.AsRandomAccessStream());
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[Mascot] exception: {ex.Message}");
                MascotBorder.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>程式化聚焦到 SelectedIndex 對應的 SelectorItem；清單為空時回退到 ListView 容器，container 未實體化時掛 LayoutUpdated 延後聚焦。</summary>
        public void FocusList()
        {
            if (_items.Count == 0)
            {
                ProfileList.Focus(FocusStateHelper.Preferred);
                return;
            }
            int idx = ProfileList.SelectedIndex;
            if (idx < 0 || idx >= _items.Count) idx = 0;
            ProfileList.SelectedIndex = idx;
            ProfileList.ScrollIntoView(_items[idx]);
            if (ProfileList.ContainerFromIndex(idx) is SelectorItem lvi)
            {
                lvi.Focus(FocusStateHelper.Preferred);
            }
            else
            {
                int targetIdx = idx;
                EventHandler<object>? handler = null;
                handler = (s, e) =>
                {
                    if (ProfileList.ContainerFromIndex(targetIdx) is SelectorItem deferred)
                    {
                        deferred.Focus(FocusStateHelper.Preferred);
                        ProfileList.LayoutUpdated -= handler;
                    }
                };
                ProfileList.LayoutUpdated += handler;
            }
        }

        /// <summary>將焦點程式化設給搜尋方塊（宿主 Y 鍵從清單移動焦點時呼叫）。</summary>
        public void FocusSearchBox() => SearchBox.Focus(FocusStateHelper.Preferred);

        /// <summary>編輯器入口處先呼叫，記錄即將編輯的 AppId 供 CloseEditor 還原焦點使用。</summary>
        public void SetLastEditedHint(AppId appId) => _lastEditedAppId = appId;

        /// <summary>聚焦回 _lastEditedAppId 對應 row；無紀錄或該 row 不在 _items 內時回退到 FocusList。</summary>
        public void FocusLastEdited()
        {
            if (_lastEditedAppId != null && FocusListItem(_lastEditedAppId)) return;
            FocusList();
        }

        /// <summary>依 AppId 在 _items 中找對應 row，命中則 SelectedIndex + ScrollIntoView + 容器取得後 Focus；找不到回 false。</summary>
        private bool FocusListItem(AppId appId)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].AppId != null && _items[i].AppId.MatchesExact(appId))
                {
                    ProfileList.SelectedIndex = i;
                    ProfileList.ScrollIntoView(_items[i]);
                    if (ProfileList.ContainerFromIndex(i) is SelectorItem lvi)
                    {
                        lvi.Focus(FocusStateHelper.Preferred);
                        return true;
                    }
                    // 容器尚未實體化（虛擬化清單捲動到目標前不會建 container）；
                    // 用 LayoutUpdated 等實體化完成後再聚焦
                    int targetIdx = i;
                    EventHandler<object>? handler = null;
                    handler = (s, e) =>
                    {
                        if (ProfileList.ContainerFromIndex(targetIdx) is SelectorItem deferred)
                        {
                            deferred.Focus(FocusStateHelper.Preferred);
                            ProfileList.LayoutUpdated -= handler;
                        }
                    };
                    ProfileList.LayoutUpdated += handler;
                    return true;
                }
            }
            return false;
        }

        /// <summary>LB 鍵：依當下可見項數往上跳一頁。</summary>
        public void PageUp() => PageBy(-1);

        /// <summary>RB 鍵：依當下可見項數往下跳一頁。</summary>
        public void PageDown() => PageBy(+1);

        // WinAppSDK 2.x 的 ListView.ItemsPanelRoot 不可靠（常讀回 null），改在面板自身 Loaded 取 sender 快取。
        private ItemsStackPanel? _itemsPanel;

        /// <summary>ItemsStackPanel 載入時快取參考，供 GetPageSize 算每頁可見項數（取代不可靠的 ItemsPanelRoot）。</summary>
        private void ItemsPanel_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ItemsStackPanel panel) _itemsPanel = panel;
        }

        /// <summary>
        /// 當下「每頁可見項數」：取快取 ItemsStackPanel 即時回報的 LastVisibleIndex-FirstVisibleIndex+1。
        /// 此值隨視窗高度（解析度）動態變化，作為 PageBy 跳頁步距。
        /// 面板尚未實體化（清單剛載入）時回報無效值，回傳 0 由呼叫端做回退處理。
        /// </summary>
        private int GetPageSize()
        {
            if (_itemsPanel is ItemsStackPanel panel)
            {
                int visible = panel.LastVisibleIndex - panel.FirstVisibleIndex + 1;
                if (visible > 0) return visible;
            }
            return 0;
        }

        /// <summary>跳頁實作：step = 可見項數 × direction（±1），clamp 到 [0, _items.Count-1] 後重設 SelectedIndex 與焦點。</summary>
        private void PageBy(int direction)
        {
            if (_items.Count == 0) return;

            int step = GetPageSize();
            if (step <= 0) step = 10;

            int currentIdx = ProfileList.SelectedIndex;
            if (currentIdx < 0)
            {
                var focused = FocusManager.GetFocusedElement(XamlRoot) as SelectorItem;
                if (focused != null) currentIdx = ProfileList.IndexFromContainer(focused);
            }
            if (currentIdx < 0) currentIdx = 0;

            int newIdx = currentIdx + direction * step;
            if (newIdx < 0) newIdx = 0;
            if (newIdx > _items.Count - 1) newIdx = _items.Count - 1;
            if (newIdx == currentIdx) return;

            ProfileList.SelectedIndex = newIdx;
            ProfileList.ScrollIntoView(_items[newIdx]);
            if (ProfileList.ContainerFromIndex(newIdx) is SelectorItem lvi)
            {
                lvi.Focus(FocusStateHelper.Preferred);
            }
            else
            {
                int targetIdx = newIdx;
                EventHandler<object>? handler = null;
                handler = (s, e) =>
                {
                    if (ProfileList.ContainerFromIndex(targetIdx) is SelectorItem deferred)
                    {
                        deferred.Focus(FocusStateHelper.Preferred);
                        ProfileList.LayoutUpdated -= handler;
                    }
                };
                ProfileList.LayoutUpdated += handler;
            }
        }

        /// <summary>取目前作用中的 row：先看焦點 SelectorItem（D-pad 移動只動焦點不更新 SelectedItem），回退到 SelectedItem。</summary>
        private GamepadProfileRow? GetActiveRow()
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is SelectorItem item)
            {
                if (item.Content is GamepadProfileRow focusedRow) return focusedRow;
                if (item.DataContext is GamepadProfileRow ctxRow) return ctxRow;
            }
            return ProfileList.SelectedItem as GamepadProfileRow;
        }

        /// <summary>給宿主 A 鍵呼叫：記下目前 row 的 AppId（供 CloseEditor 還原焦點），並發出 EditRequested。</summary>
        public void EditSelected()
        {
            var row = GetActiveRow();
            if (row?.AppId != null)
            {
                _lastEditedAppId = row.AppId;
                EditRequested?.Invoke(this, row.AppId);
            }
        }

        /// <summary>給宿主 X 鍵呼叫：刪除目前焦點 row（彈確認對話方塊）。</summary>
        public Task DeleteSelectedAsync()
        {
            var row = GetActiveRow();
            if (row?.AppId != null) return DeleteAsync(row.AppId);
            return Task.CompletedTask;
        }

        /// <summary>滑鼠／手把 A 點某列：記下目標 AppId 並發出 EditRequested。</summary>
        private void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GamepadProfileRow row && row.AppId != null)
            {
                _lastEditedAppId = row.AppId;
                EditRequested?.Invoke(this, row.AppId);
            }
        }

        /// <summary>每列垃圾桶 Button 點選：刪除該列對應 profile。</summary>
        private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is AppId appId)
                _ = DeleteAsync(appId);
        }

        /// <summary>
        /// 彈確認對話方塊，按下「是」才實際刪除並重新整理；刪除後焦點落回原索引（或鄰近）的項目。
        /// </summary>
        private async Task DeleteAsync(AppId appId)
        {
            int prevIndex = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                if (_items[i].AppId != null && _items[i].AppId.MatchesExact(appId))
                {
                    prevIndex = i;
                    break;
                }
            }

            string appName = prevIndex >= 0 ? _items[prevIndex].DisplayName : appId.Value;
            var dlg = new GamepadMessageDialog(
                XamlRoot,
                _resourceLoader.Loc("GamepadMappingDeleteConfirmTitle"),
                string.Format(_resourceLoader.Loc("GamepadMappingDeleteConfirmBody"), appName),
                _resourceLoader.Loc("GamepadMappingDeleteConfirmYes"),
                _resourceLoader.Loc("GamepadMappingDeleteConfirmNo"));
            await dlg.ShowAsync();
            if (dlg.Result)
            {
                GamepadProfileStore.Delete(appId);
                Refresh();
            }

            // 刪除後焦點還原到鄰近項目
            {
                if (_items.Count > 0 && prevIndex >= 0)
                {
                    int target = Math.Min(prevIndex, _items.Count - 1);
                    ProfileList.SelectedIndex = target;
                    ProfileList.ScrollIntoView(_items[target]);
                    if (ProfileList.ContainerFromIndex(target) is SelectorItem lvi)
                    {
                        lvi.Focus(FocusStateHelper.Preferred);
                    }
                    else
                    {
                        int targetIdx = target;
                        EventHandler<object>? handler = null;
                        handler = (s, e) =>
                        {
                            if (ProfileList.ContainerFromIndex(targetIdx) is SelectorItem deferred)
                            {
                                deferred.Focus(FocusStateHelper.Preferred);
                                ProfileList.LayoutUpdated -= handler;
                            }
                        };
                        ProfileList.LayoutUpdated += handler;
                    }
                }
                else
                {
                    FocusList();
                }
            }
        }

        /// <summary>產生 appId 的副文字（在地化 prefix + 識別值；path-bound profile 後綴接資料夾名稱以區分撞名）。</summary>
        private string AppIdSubtitle(AppId appId)
        {
            if (appId == null) return string.Empty;
            string prefix = appId.Kind == IdKind.Aumid
                ? _resourceLoader.Loc("AppIdAumidPrefix")
                : _resourceLoader.Loc("AppIdProcessPrefix");
            string baseText = prefix + (appId.Value ?? string.Empty);
            if (appId.Kind == IdKind.Process && !string.IsNullOrEmpty(appId.FullPath))
            {
                string folder = AppId.ExtractFolderName(appId.FullPath);
                if (!string.IsNullOrEmpty(folder))
                    baseText = baseText + " · " + folder;
            }
            return baseText;
        }
    }
}
