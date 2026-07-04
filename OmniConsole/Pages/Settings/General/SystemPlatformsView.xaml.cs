using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using OmniConsole.Models;
using OmniConsole.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings.General
{
    /// <summary>
    /// 系統內建平台子頁：GridView 列出 PlatformCatalog.All，選取即設為預設平台。
    /// 共用載入/尺寸/焦點邏輯在 <see cref="PlatformsViewBase"/>；本類只提供系統平台資料來源與系統索引特有的選取/手把語意。
    /// </summary>
    public sealed partial class SystemPlatformsView : PlatformsViewBase
    {
        /// <summary>建立系統平台子頁。</summary>
        public SystemPlatformsView()
        {
            InitializeComponent();
        }

        /// <summary>基底取用的卡片網格（本子頁 XAML 內的 PlatformGridView）。</summary>
        protected override GridView Grid => PlatformGridView;

        /// <summary>Frame 導覽進來時載入系統平台卡片。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _ = LoadCardsAsync();
        }

        /// <summary>系統內建平台資料來源（PlatformCatalog.All）。</summary>
        protected override Task<List<PlatformCardItem>> LoadCardItemsAsync()
        {
            var cards = PlatformCatalog.All
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                })
                .ToList();
            return Task.FromResult(cards);
        }

        // ── 平台卡片選取 ──────────────────────────────────────────────────────

        /// <summary>
        /// 處理 GridView 選取狀態變更。
        /// 若選取的平台不可用且尚有其他可用平台，還原至上一個有效選取；否則選取即儲存為預設並寫回宿主。
        /// </summary>
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is not PlatformCardItem selected) return;

            if (!selected.IsAvailable)
            {
                // 系統索引標籤：若有其他可用平台，還原為上一個有效選取。
                if (CardItems.Any(c => c.IsAvailable))
                {
                    var previous = CardItems.FirstOrDefault(c => c.Id == Host?.SelectedPlatformId);
                    PlatformGridView.SelectedItem = previous;
                    return;
                }
                // 所有系統平台都不可用：允許選取（啟動時會顯示錯誤訊息）。
            }

            // 選取即儲存：系統平台。
            var platform = PlatformCatalog.FindById(selected.Id) ?? PlatformCatalog.All[0];
            SettingsService.SetDefaultPlatform(platform);
            SettingsService.SaveCurrentVersion();
            Host?.SetSelectedPlatform(selected.Id);
        }

        // ── 手把命令（由宿主轉入）──────────────────────────────────────────────

        /// <summary>A 鍵：焦點在可用平台卡片時確認選取（更新預設平台）；回傳是否已處理。</summary>
        internal bool TryHandleAButtonInternal(object? focused)
        {
            if (focused is SelectorItem { Content: PlatformCardItem { IsAvailable: true } card })
            {
                PlatformGridView.SelectedItem = card;
                Host?.SetSelectedPlatform(card.Id);
                return true;
            }
            return false;
        }

        /// <summary>Menu 鍵：焦點卡片可用則確認選取，回傳是否有可啟動目標。</summary>
        internal bool TrySelectFocusedCardForLaunch()
        {
            if (FocusManager.GetFocusedElement(this.XamlRoot) is SelectorItem { Content: PlatformCardItem { IsAvailable: true } card })
            {
                PlatformGridView.SelectedItem = card;
                Host?.SetSelectedPlatform(card.Id);
            }
            return !string.IsNullOrEmpty(Host?.SelectedPlatformId);
        }
    }
}
