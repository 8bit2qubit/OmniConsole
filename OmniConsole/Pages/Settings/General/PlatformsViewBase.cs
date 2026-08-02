using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings.General
{
    /// <summary>
    /// System/User 平台子頁的共用基底：持有卡片清單與 GridView 共通邏輯（增量同步、尺寸、焦點、選取還原），
    /// 子頁覆寫卡片網格（Grid）、資料來源（LoadCardItemsAsync）與導覽前置（OnNavigatedTo），並實作各自特有的選取/手把語意。
    /// 跨子頁共用的選取狀態（SelectedPlatformId）由 GeneralHostView 持有，子頁透過 Host 參照讀寫。
    /// 由 GeneralHostView 透過內層 Frame.Navigate 載入（切走即銷毀）；宿主自身當 e.Parameter 傳入。
    /// 衍生子頁的 XAML 須提供名為 PlatformGridView 的 GridView 與 PlatformWrapGrid 的 ItemsWrapGrid。
    /// </summary>
    public partial class PlatformsViewBase : Page
    {
        /// <summary>宿主參照（共用選取狀態 + Hwnd 來源）。</summary>
        protected GeneralHostView? Host { get; private set; }

        // 平台卡片清單，固定實例、ItemsSource 僅首次繫結（內容變更走增量 ReplaceCards）。
        protected readonly ObservableCollection<PlatformCardItem> CardItems = [];
        // ItemsWrapGrid 自身 Loaded 事件以 sender 取得並快取的面板實例。
        private ItemsWrapGrid? _platformWrapGrid;

        /// <summary>子頁覆寫提供卡片網格（衍生類 XAML 內 x:Name="PlatformGridView"）；base 不直接持有故由子類給。</summary>
        protected virtual GridView Grid => throw new System.NotImplementedException("衍生子頁須覆寫 Grid");

        /// <summary>Frame 導覽進來時取宿主參照。子頁覆寫做前置（如 consent 檢查）+ 載入後呼叫 base。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Host = e.Parameter as GeneralHostView;
        }

        /// <summary>子頁覆寫：回傳本分類的平台卡片清單（尚未算可用性）。</summary>
        protected virtual Task<List<PlatformCardItem>> LoadCardItemsAsync()
            => Task.FromResult(new List<PlatformCardItem>());

        /// <summary>
        /// 載入卡片的共用流程：綁卡前評估解碼寬、取子頁資料、算可用性、不可用選取降級、增量同步、還原選取底色、焦點落第一張。
        /// 與原始 GeneralView.LoadPlatformCards 的系統/使用者共通部分一致。
        /// </summary>
        protected async Task LoadCardsAsync()
        {
            // 綁卡前先用視窗寬評估保守初始解碼寬（視窗寬佈局前即可取得，不像 GridView ActualWidth 要等佈局）。
            CardIconCache.SeedDecodeWidthFromWindow(Host?.Hwnd ?? IntPtr.Zero);

            var newCards = await LoadCardItemsAsync();

            // ── 第一階段：先綁卡 + 聚焦（在可用性檢查 await 之前）──
            // 目的：導覽切 tab 當下焦點會被打到漢堡按鈕，若等到可用性檢查算完才聚焦，這段空窗焦點停在漢堡按鈕＝閃動。
            // 故綁卡聚焦提前到可用性 await 前、零空窗。此時 newCards.IsAvailable 尚未算（皆預設值），
            // ReplaceCards 傳 compareAvailability:false，內容比對略過 IsAvailable。
            string selectedId = Host?.SelectedPlatformId ?? "";
            ReplaceCards(newCards, compareAvailability: false);
            if (Grid.ItemsSource is null)
                Grid.ItemsSource = CardItems;

            var selectedCard = CardItems.FirstOrDefault(c => c.Id == selectedId);
            if (selectedCard != null)
                Grid.SelectedItem = selectedCard;

            FocusPrimaryContent();

            // ── 第二階段：算可用性（較慢的可用性檢查）→ 原地更新既有卡片實例 IsAvailable（INPC 刷 Opacity、不換實例、不影響焦點）──
            // 註：可用性提到聚焦後算，是為消除「導覽切 tab 焦點先飄漢堡按鈕、等檢查期間停在那」的閃動（焦點全程落內容區）。
            bool[] available = await Task.WhenAll(
                newCards.Select(c => ProcessLauncherService.CheckPlatformAvailableAsync(c.Platform)));
            for (int i = 0; i < newCards.Count; i++)
            {
                var card = CardItems.FirstOrDefault(c => c.Id == newCards[i].Id);
                if (card != null) card.IsAvailable = available[i];
            }

            // 可用性算完：若選取的平台不可用，把選取底色改落第一張可用卡（僅影響底色、不改預設平台；同原始降級行為）。
            var sel = CardItems.FirstOrDefault(c => c.Id == selectedId);
            if (sel is { IsAvailable: false })
            {
                var fallback = CardItems.FirstOrDefault(c => c.IsAvailable);
                if (fallback != null) Grid.SelectedItem = fallback;
            }
        }


        /// <summary>
        /// 以 Id 為鍵做增量比對，把 CardItems 同步成 newCards（不換 ItemsSource、不發重設通知，少重建）。
        /// <paramref name="compareAvailability"/>=false 時內容比對不含 IsAvailable：用於「綁卡聚焦先行、可用性稍後 INPC 補」階段（此時 IsAvailable 尚未算）。
        /// </summary>
        protected void ReplaceCards(IReadOnlyList<PlatformCardItem> newCards, bool compareAvailability = true)
        {
            ObservableCollectionDiff.Apply(
                CardItems,
                newCards,
                static (a, b) => a.Id == b.Id,
                (a, b) => a.DisplayName == b.DisplayName
                       && a.IconAsset == b.IconAsset
                       && (!compareAvailability || a.IsAvailable == b.IsAvailable));
        }

        /// <summary>回傳指定平台 Id 在卡片清單中的索引；找不到回 -1。</summary>
        protected int IndexOfCard(string id)
        {
            for (int i = 0; i < CardItems.Count; i++)
                if (CardItems[i].Id == id) return i;
            return -1;
        }

        // ── 焦點 ──────────────────────────────────────────────────────────────

        /// <summary>切頁進來時把白框焦點落在主內容（第一張平台卡）。</summary>
        protected void FocusPrimaryContent()
        {
            if (CardItems.Count == 0) return;
            FocusPlatformCard(0);
        }

        /// <summary>程式化將焦點設給指定索引的卡片容器。</summary>
        protected void FocusPlatformCard(int index)
        {
            if (index < 0 || index >= CardItems.Count) return;
            FocusNavHelper.FocusGridItemImmediate(Grid, index);
        }

        /// <summary>新增卡片後平滑捲動到尾端新卡並聚焦（新卡恆在尾端、需大幅捲動）。</summary>
        protected void SmoothScrollToNewCardAndFocus(int index)
        {
            if (index < 0 || index >= CardItems.Count) return;
            FocusNavHelper.SmoothScrollToGridItemAndFocus(Grid, index);
        }

        // ── GridView 尺寸 ─────────────────────────────────────────────────────

        /// <summary>ItemsWrapGrid 載入時快取面板實例並套用初始卡片尺寸。子頁 XAML 的 ItemsWrapGrid Loaded 綁此。</summary>
        protected void PlatformWrapGrid_Loaded(object sender, RoutedEventArgs e)
        {
            _platformWrapGrid = sender as ItemsWrapGrid;
            ApplyPlatformItemSize(Grid.ActualWidth);
        }

        /// <summary>GridView 大小變動時重算卡片尺寸。子頁 XAML 的 GridView SizeChanged 綁此。</summary>
        protected void PlatformGridView_SizeChanged(object sender, SizeChangedEventArgs e)
            => ApplyPlatformItemSize(e.NewSize.Width);

        /// <summary>依可用寬度計算每張平台卡片尺寸，使卡片填滿整列；GridView 真實寬就緒後把圖解碼寬縮到精準值。</summary>
        private void ApplyPlatformItemSize(double availableWidth)
        {
            if (_platformWrapGrid is null || availableWidth <= 0) return;
            double itemWidth = CardIconCache.ComputeItemWidth(availableWidth);
            _platformWrapGrid.ItemWidth = itemWidth;
            _platformWrapGrid.ItemHeight = Math.Floor(itemWidth * 0.7); // 維持約 7:10 的高寬比
            if (XamlRoot is not null)
                CardIconCache.UpdateDecodeWidth(availableWidth, XamlRoot.RasterizationScale);
        }
    }
}
