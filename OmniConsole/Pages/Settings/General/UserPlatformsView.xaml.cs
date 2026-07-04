using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OmniConsole.Pages.Settings.General
{
    /// <summary>
    /// 使用者自訂平台子頁：GridView 列出 UserPlatformStore 平台，提供新增/編輯/刪除/匯入/匯出，含免責聲明同意流程。
    /// 共用載入/尺寸/焦點邏輯在 <see cref="PlatformsViewBase"/>；本類提供使用者平台資料來源與其特有的選取/對話/手把語意。
    /// </summary>
    public sealed partial class UserPlatformsView : PlatformsViewBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 匯出成功提示的自動關閉計時器（2 秒後關閉 TeachingTip）
        private readonly DispatcherTimer _exportTipTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        /// <summary>建立使用者平台子頁。</summary>
        public UserPlatformsView()
        {
            InitializeComponent();
            _exportTipTimer.Tick += (_, _) =>
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;
            };
        }

        /// <summary>基底取用的卡片網格（本子頁 XAML 內的 PlatformGridView）。</summary>
        protected override GridView Grid => PlatformGridView;

        /// <summary>Frame 導覽進來時依同意狀態載入卡片或顯示免責聲明。</summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!SettingsService.GetCustomPlatformConsentAccepted())
            {
                // 未同意：只顯示免責聲明，不載入卡片。
                CustomConsentPanel.Visibility = Visibility.Visible;
                PlatformGridView.Visibility = Visibility.Collapsed;
                return;
            }
            CustomConsentPanel.Visibility = Visibility.Collapsed;
            PlatformGridView.Visibility = Visibility.Visible;
            _ = LoadCardsAsync();
        }

        /// <summary>使用者自訂平台資料來源（UserPlatformStore）。</summary>
        protected override Task<List<PlatformCardItem>> LoadCardItemsAsync()
        {
            var cards = UserPlatformStore.GetAllDefinitions()
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = UserPlatformStore.FindEntryById(p.Id)?.DisplayName ?? p.Id,
                })
                .ToList();
            return Task.FromResult(cards);
        }

        // ── 平台卡片選取 ──────────────────────────────────────────────────────

        /// <summary>
        /// 處理 GridView 選取狀態變更。
        /// 使用者索引標籤允許選取不可用的平台（供 X 編輯修正路徑、不存預設）；可用則選取即儲存為預設並寫回宿主。
        /// </summary>
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is not PlatformCardItem selected) return;

            if (!selected.IsAvailable)
            {
                // 使用者索引標籤：允許選取不可用的平台（以便透過 X 編輯修正路徑），但不儲存為預設。
                return;
            }

            var platform = UserPlatformStore.FindById(selected.Id) ?? PlatformCatalog.All[0];
            SettingsService.SetDefaultPlatform(platform);
            SettingsService.SaveCurrentVersion();
            Host?.SetSelectedPlatform(selected.Id);
        }

        // ── 手把命令（由宿主轉入）──────────────────────────────────────────────

        /// <summary>是否有任何使用者平台（殼層提示列計算用）。</summary>
        internal bool HasItems => CardItems.Count > 0;

        /// <summary>Y 鍵：新增平台（開 PlatformEditDialog 新增模式）。</summary>
        internal Task AddPlatformInternal() => ShowPlatformEditDialogAsync(null);

        /// <summary>匯入鈕：開 ImportPlatformDialog（貼 JSON 匯入），驗證通過後寫入並局部更新單張卡。</summary>
        internal async Task ImportInternal()
        {
            // 若提示仍開著，先強制關閉再顯示 Dialog，避免 TeachingTip 與 ContentDialog.ShowAsync() 同時存在導致崩潰。
            _exportTipTimer.Stop();
            ExportSuccessTeachingTip.IsOpen = false;

            var dialog = new ImportPlatformDialog(this.XamlRoot);
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary || dialog.ResultEntry is null) return;

            UserPlatformStore.Add(dialog.ResultEntry);   // entry.Id 若空會在此被填上
            await UpdateOrInsertCard(dialog.ResultEntry.Id);
        }

        /// <summary>提示列 X 鈕（滑鼠）：編輯目前選取的使用者平台。</summary>
        internal Task EditSelectedPlatformInternal()
        {
            if (PlatformGridView.SelectedItem is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null) return ShowPlatformEditDialogAsync(entry);
            }
            return Task.CompletedTask;
        }

        /// <summary>X 鍵（手把）：編輯焦點所在的使用者平台（焦點 ≠ 選取）。</summary>
        internal Task EditFocusedPlatformInternal(object? focused)
        {
            if (focused is SelectorItem item && item.Content is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null) return ShowPlatformEditDialogAsync(entry);
            }
            return Task.CompletedTask;
        }

        /// <summary>A 鍵：焦點在平台卡片時確認選取（不可用卡片亦可選、供編輯）；回傳是否已處理。</summary>
        internal bool TryHandleAButtonInternal(object? focused)
        {
            if (focused is SelectorItem { Content: PlatformCardItem card })
            {
                PlatformGridView.SelectedItem = card;
                if (card.IsAvailable) Host?.SetSelectedPlatform(card.Id);
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

        // ── 平台匯出 ──────────────────────────────────────────────────────────

        /// <summary>卡片右鍵選單開啟前：設匯出項在地化文字。</summary>
        private void CardContextMenu_Opening(object sender, object e)
        {
            if (sender is not MenuFlyout flyout) return;
            if (flyout.Items.Count > 0 && flyout.Items[0] is MenuFlyoutItem exportItem)
                exportItem.Text = _resourceLoader.Loc("CardMenu_Export");
        }

        /// <summary>卡片右鍵「匯出」：序列化平台設定為 JSON 複製到剪貼簿並顯示提示。</summary>
        private void CardExport_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not PlatformCardItem card) return;

            var entry = UserPlatformStore.FindEntryById(card.Id);
            if (entry is null) return;

            var dp = new DataPackage();
            dp.SetText(UserPlatformShareService.Export(entry));
            Clipboard.SetContent(dp);

            ExportSuccessTeachingTip.IsOpen = true;
            _exportTipTimer.Stop();
            _exportTipTimer.Start();
        }

        // ── 平台編輯對話方塊 ──────────────────────────────────────────────────

        /// <summary>開啟系統 FileOpenPicker 作為舊式後備，回傳選取的檔案路徑或 null。</summary>
        private async Task<string?> ShowLegacyFilePickerAsync(FilePickerOptions options)
        {
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Host?.Hwnd ?? System.IntPtr.Zero);
                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = options.ShowImagePreview
                    ? PickerLocationId.PicturesLibrary
                    : PickerLocationId.ComputerFolder;
                foreach (var filter in options.FileTypeFilters)
                    picker.FileTypeFilter.Add(filter);

                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 顯示新增/編輯使用者平台的 PlatformEditDialog。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        private async Task ShowPlatformEditDialogAsync(UserPlatformEntry? existingEntry)
        {
            _exportTipTimer.Stop();
            ExportSuccessTeachingTip.IsOpen = false;

            bool isEdit = existingEntry != null;
            var dialog = new PlatformEditDialog(
                this.XamlRoot, existingEntry);

            ContentDialogResult result;

            // Hide/reopen 迴圈：PlatformEditDialog 的瀏覽按鈕會 Hide() 自己，
            // 由此處協調顯示 FilePickerDialog 後重新開啟 PlatformEditDialog。
            while (true)
            {
                result = await dialog.ShowAsync();

                if (!dialog.RequestFilePicker) break;

                // 顯示自製檔案選擇器
                var pickerDialog = new FilePickerDialog(
                    this.XamlRoot, dialog.FilePickerRequest!);
                var pickerResult = await pickerDialog.ShowAsync();

                string? selectedPath = null;
                if (pickerResult == ContentDialogResult.Primary)
                {
                    selectedPath = pickerDialog.SelectedFilePath;
                }
                else if (pickerDialog.RequestLegacyPicker)
                {
                    // 使用者要求系統 FileOpenPicker
                    selectedPath = await ShowLegacyFilePickerAsync(dialog.FilePickerRequest!);
                }
                dialog.ApplyFilePickerResult(selectedPath);
                // 迴圈回去重新開啟 PlatformEditDialog
            }

            if (result == ContentDialogResult.Primary && dialog.ResultEntry != null)
            {
                var entry = dialog.ResultEntry;

                // 匯入卡片背景圖（縮放至 800x560）
                if (dialog.PendingIconPath != null)
                {
                    if (!string.IsNullOrEmpty(entry.IconFileName))
                        UserPlatformStore.DeleteIconFile(entry.IconFileName);
                    var storageFile = await StorageFile.GetFileFromPathAsync(dialog.PendingIconPath);
                    entry.IconFileName = await UserPlatformStore.ImportIconAsync(storageFile);
                }

                if (isEdit)
                    UserPlatformStore.Update(entry);
                else
                    UserPlatformStore.Add(entry);   // entry.Id 若空會在此被填上

                // 局部更新單張卡（不整批重新載入）：編輯原地改既有卡片欄位、新增一筆。
                // 既有卡走 INPC 原地更新、容器不重建。
                // 須 await：FocusPlatformCard 依賴 CardItems 已被更新/插入完成。
                bool inserted = await UpdateOrInsertCard(entry.Id);

                // 焦點（白框）落到剛儲存的那張卡片。新增（新卡在尾端、需大幅捲動）走平滑捲動；
                // 編輯（原卡通常已在視野內）維持原 FocusPlatformCard。
                int savedIndex = IndexOfCard(entry.Id);
                if (savedIndex >= 0)
                {
                    if (inserted) SmoothScrollToNewCardAndFocus(savedIndex);
                    else FocusPlatformCard(savedIndex);
                }
            }
            else if (result == ContentDialogResult.Secondary && isEdit && existingEntry != null)
            {
                // 刪除平台：從 Store 移除後，視剩餘數量決定留在使用者索引標籤或切回系統索引標籤
                // 刪除前先記下被刪卡片索引、以及被刪的是否正是目前預設平台（底色那張）。
                int prevIndex = IndexOfCard(existingEntry.Id);
                bool deletedDefault = Host?.SelectedPlatformId == existingEntry.Id;

                UserPlatformStore.Delete(existingEntry.Id);

                var remainingUser = UserPlatformStore.GetAllDefinitions();
                if (remainingUser.Count > 0)
                {
                    // 焦點（白框）一律落回被刪項原索引（或最後一項），與貓又清單刪除行為一致。
                    int target = prevIndex < 0 ? 0 : Math.Min(prevIndex, remainingUser.Count - 1);

                    // 選取（底色/預設平台）：刪非預設平台時維持不變；刪到預設平台時不能讓預設遺失，
                    // 改選 target 那張並即儲存為新預設（不可用時由後續載入流程自動改選）。
                    if (deletedDefault)
                    {
                        var newDefaultId = remainingUser[target].Id;
                        var newDefault = UserPlatformStore.FindById(newDefaultId) ?? PlatformCatalog.All[0];
                        SettingsService.SetDefaultPlatform(newDefault);
                        SettingsService.SaveCurrentVersion();
                        Host?.SetSelectedPlatform(newDefaultId);
                    }

                    await LoadCardsAsync();   // await：FocusPlatformCard 依賴 CardItems 已填好
                    FocusPlatformCard(target);
                }
                else
                {
                    // 使用者索引標籤已無平台，切換至系統索引標籤。
                    // 僅在刪到的正是預設平台時才補新預設（選系統第一張並即儲存），避免預設遺失。
                    if (deletedDefault)
                    {
                        SettingsService.SetDefaultPlatform(PlatformCatalog.All[0]);
                        SettingsService.SaveCurrentVersion();
                        Host?.SetSelectedPlatform(PlatformCatalog.All[0].Id);
                    }
                    Host?.NavigateToSystem();
                }
            }
            else
            {
                // 取消（或未套用任何變更）：焦點（白框）還原回操作前那張卡片。
                // 編輯模式是被編輯的卡片、新增模式落回目前選取的卡片，避免關閉後白框懸空。
                var anchorId = existingEntry?.Id ?? Host?.SelectedPlatformId ?? "";
                int anchorIndex = IndexOfCard(anchorId);
                if (anchorIndex >= 0) FocusPlatformCard(anchorIndex);
            }
        }

        /// <summary>
        /// 編輯/新增單張使用者平台後的「局部更新」路徑：只動該張卡，不重新載入整個清單。
        /// 既有卡片 → 原地更新欄位（INPC、不換實例）；新卡片 → 新增一筆。可用性只查這一張。
        /// </summary>
        /// <returns>true 表示新插入了卡片；false 表示原地更新既有卡片。</returns>
        private async Task<bool> UpdateOrInsertCard(string id)
        {
            var entry = UserPlatformStore.FindEntryById(id);
            if (entry is null) return false;

            var platform = entry.ToPlatformDefinition();
            string displayName = entry.DisplayName;
            bool available = await ProcessLauncherService.CheckPlatformAvailableAsync(platform);

            var existing = CardItems.FirstOrDefault(c => c.Id == id);
            if (existing != null)
            {
                existing.Platform = platform;
                existing.DisplayName = displayName;
                existing.IsAvailable = available;
                return false;
            }
            CardItems.Add(new PlatformCardItem
            {
                Platform = platform,
                DisplayName = displayName,
                IsAvailable = available,
            });
            return true;
        }

        // ── 免責聲明同意 ──────────────────────────────────────────────────────

        /// <summary>同意自訂平台免責聲明後：儲存同意、切顯卡片、載入、通知宿主重新整理匯入鈕/提示列。</summary>
        private void CustomConsentAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.SetCustomPlatformConsentAccepted(true);
            CustomConsentPanel.Visibility = Visibility.Collapsed;
            PlatformGridView.Visibility = Visibility.Visible;
            _ = LoadCardsAsync();
            Host?.NotifyStateChanged();
        }
    }
}
