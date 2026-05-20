using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel.Resources;

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
    }

    /// <summary>手把映射「清單頁」UserControl：列出所有 per-App profile，提供編輯/刪除入口。</summary>
    public sealed partial class GamepadProfileListView : UserControl
    {
        private readonly ResourceLoader _resw = ResourceLoader.GetForViewIndependentUse();
        private readonly ObservableCollection<GamepadProfileRow> _items = new ObservableCollection<GamepadProfileRow>();

        /// <summary>使用者要編輯某 profile（A 鍵或滑鼠點列項）時觸發。</summary>
        public event EventHandler<AppId>? EditRequested;

        /// <summary>子對話方塊開啟前 true、關閉後 false（宿主據此 Stop/StartGamepadPolling）。</summary>
        public event EventHandler<bool>? DialogActiveChanged;

        /// <summary>綁定 ListView ItemsSource 為內部 ObservableCollection，並掛 ListViewItem 焦點事件以同步 SelectedItem。</summary>
        public GamepadProfileListView()
        {
            InitializeComponent();
            ProfileList.ItemsSource = _items;
            ProfileList.ContainerContentChanging += ProfileList_ContainerContentChanging;
        }

        /// <summary>每次 ListViewItem 容器產生或重用時，掛上 GotFocus 同步 SelectedItem 到目前焦點 row。</summary>
        private void ProfileList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is ListViewItem lvi)
            {
                lvi.GotFocus -= ListViewItem_GotFocus;
                lvi.GotFocus += ListViewItem_GotFocus;
            }
        }

        /// <summary>ListViewItem 拿到焦點時同步 SelectedItem，讓 selected background 與 selection indicator 跟隨焦點。</summary>
        private void ListViewItem_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem lvi && lvi.Content is GamepadProfileRow row)
                ProfileList.SelectedItem = row;
        }

        /// <summary>是否有任何 profile（給宿主控制底部 (X) 提示顯示）。</summary>
        public bool HasItems => _items.Count > 0;

        /// <summary>從 GamepadProfileStore 重抓並重新整理清單；空清單時顯示提示、隱藏卡片。</summary>
        public void Refresh()
        {
            _items.Clear();
            try
            {
                var profiles = GamepadProfileStore.Load();
                foreach (var p in profiles)
                {
                    _items.Add(new GamepadProfileRow
                    {
                        AppId = p.AppId,
                        DisplayName = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : p.AppId.Value,
                        SubText = AppIdSubtitle(p.AppId)
                    });
                }
            }
            catch { }

            bool hasItems = _items.Count > 0;
            ListCard.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            if (!hasItems) TryLoadMascot();
            if (hasItems && ProfileList.SelectedIndex < 0) ProfileList.SelectedIndex = 0;
        }

        /// <summary>EmptyState 區大小變動時呼叫 ResizeMascot 重算尺寸。</summary>
        private void EmptyState_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ResizeMascot();
        }

        /// <summary>依 UserControl 高度扣除外層兄弟元素後的剩餘空間設定 MascotBorder 尺寸，套 16:9 比例並以 UserControl 寬度為上限。</summary>
        private void ResizeMascot()
        {
            const double aspectW = 16;
            const double aspectH = 9;
            const double minH = 270;
            const double safetyMargin = 24;

            double ucH = this.ActualHeight;
            if (ucH <= 0) return;

            double siblingsH = 0;
            if (EmptyState.Parent is StackPanel outerPanel)
            {
                double outerSpacing = outerPanel.Spacing;
                int visibleOuter = 0;
                foreach (var child in outerPanel.Children)
                {
                    if (child is FrameworkElement fe && fe.Visibility == Visibility.Visible && fe != EmptyState)
                    {
                        siblingsH += fe.ActualHeight;
                        visibleOuter++;
                    }
                }
                int gapsOuter = visibleOuter;
                siblingsH += gapsOuter * outerSpacing;

                double innerSpacing = EmptyState.Spacing;
                siblingsH += EmptyHint.ActualHeight + innerSpacing;
            }

            double availH = ucH - siblingsH - safetyMargin;
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

            DebugLogger.Log($"[Mascot] resize: UC={ucH:F0}, siblings={siblingsH:F0}, target={targetW:F0}x{targetH:F0}");
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

        /// <summary>將焦點程式化設給 ListView（清單頁進入時呼叫）。</summary>
        public void FocusList() => ProfileList.Focus(FocusState.Programmatic);

        /// <summary>取目前作用中的 row：先看焦點 ListViewItem（D-pad 移動只動焦點不更新 SelectedItem），回退到 SelectedItem。</summary>
        private GamepadProfileRow? GetActiveRow()
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is ListViewItem lvi)
            {
                if (lvi.Content is GamepadProfileRow focusedRow) return focusedRow;
                if (lvi.DataContext is GamepadProfileRow ctxRow) return ctxRow;
            }
            return ProfileList.SelectedItem as GamepadProfileRow;
        }

        /// <summary>給宿主 A 鍵呼叫：發出 EditRequested 帶目前焦點 row 的 AppId。</summary>
        public void EditSelected()
        {
            var row = GetActiveRow();
            if (row?.AppId != null) EditRequested?.Invoke(this, row.AppId);
        }

        /// <summary>給宿主 X 鍵呼叫：刪除目前焦點 row（彈確認對話方塊）。</summary>
        public Task DeleteSelectedAsync()
        {
            var row = GetActiveRow();
            if (row?.AppId != null) return DeleteAsync(row.AppId);
            return Task.CompletedTask;
        }

        /// <summary>滑鼠／手把 A 點某列：發出 EditRequested。</summary>
        private void ProfileList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is GamepadProfileRow row && row.AppId != null)
                EditRequested?.Invoke(this, row.AppId);
        }

        /// <summary>每列垃圾桶 Button 點擊：刪除該列對應 profile。</summary>
        private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is AppId appId)
                _ = DeleteAsync(appId);
        }

        /// <summary>彈確認對話方塊，按下「是」才實際刪除並重新整理。期間透過 DialogActiveChanged 通知宿主 Stop/Start 手把輪詢。</summary>
        private async Task DeleteAsync(AppId appId)
        {
            DialogActiveChanged?.Invoke(this, true);
            try
            {
                var dlg = new GamepadMessageDialog(
                    XamlRoot,
                    Loc("GamepadMappingDeleteConfirmTitle"),
                    Loc("GamepadMappingDeleteConfirmBody"),
                    Loc("GamepadMappingDeleteConfirmYes"),
                    Loc("GamepadMappingDeleteConfirmNo"));
                await dlg.ShowAsync();
                if (dlg.Result)
                {
                    GamepadProfileStore.Delete(appId);
                    Refresh();
                }
            }
            finally
            {
                DialogActiveChanged?.Invoke(this, false);
                FocusList();
            }
        }

        /// <summary>產生 appId 的副文字(在地化 prefix + 識別值)。</summary>
        private string AppIdSubtitle(AppId appId)
        {
            if (appId == null) return string.Empty;
            string prefix = appId.Kind == IdKind.Aumid
                ? Loc("AppIdAumidPrefix")
                : Loc("AppIdProcessPrefix");
            return prefix + (appId.Value ?? string.Empty);
        }

        /// <summary>resw 查詢；plain / .Text / .Content 三候選回退到 key 本身。</summary>
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
