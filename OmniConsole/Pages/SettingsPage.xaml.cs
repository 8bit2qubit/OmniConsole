using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace OmniConsole.Pages
{
    /// <summary>
    /// 設定介面 UserControl。
    /// 負責平台卡片管理、NavigationView 頁面切換、自訂平台對話方塊及設定手把輪詢。
    /// </summary>
    public sealed partial class SettingsPage : UserControl
    {
        // ── 對外事件 ──────────────────────────────────────────────────────────

        /// <summary>手把 B 鍵（導覽未展開時）或「退出」按鈕點選時，通知 MainWindow 執行退出流程。</summary>
        public event EventHandler? ExitApplicationRequested;

        /// <summary>手把 Menu 鍵觸發，通知 MainWindow 直接啟動目前選取的平台（跳過手動 FSE 切換流程）。</summary>
        public event EventHandler? LaunchPlatformDirectlyRequested;

        // ── 對外屬性 ──────────────────────────────────────────────────────────

        /// <summary>由 MainWindow 在 Activated 事件後注入，供 PlatformEditDialog 使用。</summary>
        public IntPtr Hwnd { get; set; }

        // ── 內部狀態 ──────────────────────────────────────────────────────────

        private readonly ResourceLoader _resourceLoader = new();
        private GamepadNavigationService? _gamepadNavigationService;

        // 設定介面的平台卡片清單與目前選取的平台 Id
        private List<PlatformCardItem> _cardItems = [];
        private string _selectedPlatformId = "";

        // 目前顯示的平台分類索引標籤（System / User）
        private string _currentCategoryTag = "System";

        // 目前顯示的設定導覽頁面（General / Advanced / Troubleshoot）
        private string _currentNavTag = "General";

        // 匯出成功提示的自動關閉計時器（2 秒後關閉 TeachingTip）
        private readonly DispatcherTimer _exportTipTimer = new() { Interval = TimeSpan.FromSeconds(2) };

        // ContentDialog 重入防護：平板互動模式下 Dialog 關閉動畫較慢，
        // 手把快速按 A 可能在前一個 Dialog 尚未完全移除時觸發第二次 ShowAsync() 導致崩潰
        private bool _isDialogOpen;

        public SettingsPage()
        {
            InitializeComponent();
            _exportTipTimer.Tick += (_, _) =>
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;
            };
        }

        // ── 設定介面初始化 ────────────────────────────────────────────────────

        /// <summary>
        /// 初始化設定介面各控制項狀態，並啟動手把輪詢與平台可用性查詢。
        /// 可見性切換由 <see cref="OmniConsole.MainWindow.ShowSettings"/> 負責，本方法於其後呼叫。
        /// </summary>
        public void ShowSettings()
        {
            // 初始化 NavigationView，預設選取第一個「一般」項目
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            _currentNavTag = "General";
            VisualStateManager.GoToState(this, "General", false);

            // 若目前選取的平台是使用者自訂的，自動切換到「使用者」索引標籤
            var currentPlatform = SettingsService.GetDefaultPlatform();
            bool isUserPlatform = PlatformCatalog.FindById(currentPlatform.Id) == null
                && UserPlatformStore.FindById(currentPlatform.Id) != null;
            _currentCategoryTag = isUserPlatform ? "User" : "System";
            PlatformCategoryNav.SelectedItem = isUserPlatform
                ? PlatformCategoryNav.MenuItems[1]
                : PlatformCategoryNav.MenuItems[0];
            LoadPlatformCards();

            // 顯示版本號
            VersionText.Text = $"v{SettingsService.GetAppVersion()}";

            // FSE 不可用時反灰按鈕而非隱藏
            ResetGameBarButton.IsEnabled = FseService.CanActivate();

            // 還原上次儲存的選取狀態
            var current = SettingsService.GetDefaultPlatform();
            _selectedPlatformId = current.Id;

            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }

            // 還原 Game Bar 媒體櫃的開關狀態
            UseGameBarLibrarySwitch.IsOn = SettingsService.GetUseGameBarLibraryForSettings();

            // 還原 Passthrough 開關狀態
            EnablePassthroughSwitch.IsOn = SettingsService.GetEnablePassthrough();

            StartGamepadPolling();
        }

        // ── VSM 狀態輔助 ─────────────────────────────────────────────────────

        /// <summary>
        /// 依目前導覽頁面、分類索引標籤及免責聲明同意狀態，更新底部手把提示列的按鍵圖示。
        /// 應於 <see cref="_currentNavTag"/> 或 <see cref="_currentCategoryTag"/> 變更後呼叫。
        /// </summary>
        private void UpdateGamepadHints()
        {
            if (_currentNavTag != "General")
            {
                VisualStateManager.GoToState(this, "NonGeneralPage", false);
                return;
            }
            bool showYX = _currentCategoryTag == "User" && SettingsService.GetCustomPlatformConsentAccepted();
            string state = showYX ? "UserTabWithConsent"
                : _currentCategoryTag == "User" ? "UserTabNoConsent"
                : "SystemTab";
            VisualStateManager.GoToState(this, state, false);

            // Menu 提示不依賴 VSM 結果，直接根據條件計算：非 UserTabNoConsent 且在 FSE 中才顯示
            GamepadHintMenu.Visibility = (state != "UserTabNoConsent" && FseService.IsActive())
                ? Visibility.Visible : Visibility.Collapsed;
        }


        // ── NavigationView 事件 ───────────────────────────────────────────────

        /// <summary>
        /// 處理 NavigationView 選項變更，切換內容頁面。
        /// </summary>
        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem selectedItem)
            {
                if (selectedItem.Tag?.ToString() is not string tag) return;

                // 切換頁面並更新提示列
                _currentNavTag = tag;
                VisualStateManager.GoToState(this, tag, false);
                UpdateGamepadHints();
            }
        }

        // ── 平台可用性 ────────────────────────────────────────────────────────

        /// <summary>
        /// 非同步查詢所有平台的安裝狀態，更新 IsAvailable 後重新指定 ItemsSource 重新整理 OneTime 繫結。
        /// 若目前選取的平台不可用，自動切換至第一個可用的平台。
        /// </summary>
        private async Task LoadPlatformAvailabilityAsync()
        {
            bool[] available = await Task.WhenAll(
                _cardItems.Select(c => ProcessLauncherService.CheckPlatformAvailableAsync(c.Platform)));

            for (int i = 0; i < _cardItems.Count; i++)
            {
                _cardItems[i].IsAvailable = available[i];
            }

            // 若目前選取的平台已停用，先調整選取的 Id
            var currentSelected = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (currentSelected is { IsAvailable: false })
            {
                var firstAvailable = _cardItems.FirstOrDefault(c => c.IsAvailable);
                if (firstAvailable != null)
                {
                    _selectedPlatformId = firstAvailable.Id;
                }
                else
                {
                    // 所有平台都不可用，清除選取 Id
                    _selectedPlatformId = "";
                }
            }

            // 重新指定 ItemsSource 讓 OneTime 繫結重新求值（CardOpacity 依最新 IsAvailable 更新）
            PlatformGridView.ItemsSource = null;
            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }
        }

        // ── 平台卡片事件 ──────────────────────────────────────────────────────

        /// <summary>
        /// 處理 GridView 選取狀態變更。
        /// 若選取的平台不可用，則還原至上一個有效選取。
        /// </summary>
        private void PlatformGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PlatformGridView.SelectedItem is PlatformCardItem selected)
            {
                if (!selected.IsAvailable)
                {
                    if (_currentCategoryTag == "User")
                    {
                        // 使用者索引標籤：允許選取不可用的平台（以便透過 X 編輯修正路徑），但不儲存為預設
                        return;
                    }

                    // 系統索引標籤：若有其他可用平台，還原為上一個有效選取
                    if (_cardItems.Any(c => c.IsAvailable))
                    {
                        var previous = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
                        PlatformGridView.SelectedItem = previous;
                        return;
                    }
                    // 所有系統平台都不可用：允許選取（啟動時會顯示錯誤訊息）
                }

                _selectedPlatformId = selected.Id;

                // 選取即儲存：先查系統平台，再查使用者平台
                var platform = PlatformCatalog.FindById(_selectedPlatformId)
                    ?? UserPlatformStore.FindById(_selectedPlatformId)
                    ?? PlatformCatalog.All[0];
                SettingsService.SetDefaultPlatform(platform);
                SettingsService.SaveCurrentVersion();
            }
        }

        /// <summary>
        /// GridView 大小變更時，依可用寬度計算每張卡片的尺寸，使卡片填滿整列。
        /// </summary>
        private void PlatformGridView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (PlatformGridView.ItemsPanelRoot is ItemsWrapGrid wrapGrid)
            {
                double availableWidth = e.NewSize.Width;
                // 根據可用寬度決定欄數：寬螢幕 4 欄，中等 3 欄，窄螢幕 2 欄
                int columns = availableWidth >= 1100 ? 4 : availableWidth >= 700 ? 3 : 2;
                double itemWidth = Math.Floor(availableWidth / columns);
                wrapGrid.ItemWidth = itemWidth;
                wrapGrid.ItemHeight = Math.Floor(itemWidth * 0.7); // 維持約 7:10 的高寬比
            }
        }

        // ── 設定控制項事件 ────────────────────────────────────────────────────

        /// <summary>
        /// 強制結束 GameBar.exe，透過 URI 重新啟動後再觸發 FSE。
        /// 當 FSE 進入對話方塊卡住時，透過此方法可重置環境並達成「殺死後重發」的備援路徑。
        /// </summary>
        private async void ResetGameBarButton_Click(object sender, RoutedEventArgs e)
        {
            ResetGameBarButton.IsEnabled = false;

            // 1. 殺掉 GameBar（先 GameBar 再 GameBarFTServer），稍待讓行程完全終止
            FseService.KillGameBar();
            await Task.Delay(500);

            // 2. 透過 URI 重新啟動 GameBar，固定等待讓其穩定初始化
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-gamebar://"));
            await Task.Delay(500);

            // 3. 再次殺掉以繞過 FSE 進入對話方塊（「殺死後重發」機制），稍待讓系統狀態穩定
            FseService.KillGameBar();
            await Task.Delay(500);

            if (FseService.TryActivate())
            {
                // 此應用程式會被重新啟動在 FSE 環境
                Application.Current.Exit();
            }

            ResetGameBarButton.IsEnabled = true;
        }

        /// <summary>
        /// Game Bar 媒體櫃開關切換時立即儲存。
        /// 開啟時 Game Bar 的「媒體櫃」按鈕將開啟 OmniConsole 設定介面；關閉時開啟預設遊戲平台。
        /// </summary>
        private void UseGameBarLibrarySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetUseGameBarLibraryForSettings(UseGameBarLibrarySwitch.IsOn);
        }

        /// <summary>
        /// Passthrough 開關切換時立即儲存。
        /// 開啟時 Game Bar 的「首頁」與「媒體櫃」按鈕將直接導向預設平台，跳過 OmniConsole。
        /// </summary>
        private void EnablePassthroughSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetEnablePassthrough(EnablePassthroughSwitch.IsOn);
        }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 使用者接受自訂平台免責聲明後，儲存同意狀態並載入自訂平台卡片。
        /// </summary>
        private void CustomConsentAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.SetCustomPlatformConsentAccepted(true);
            LoadPlatformCards();
        }

        // ── 平台分類索引標籤切換 ──────────────────────────────────────────────

        /// <summary>
        /// 處理分類 NavigationView（系統/使用者）的選項變更。
        /// </summary>
        private void PlatformCategoryNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                SwitchCategoryTab(tag);
            }
        }

        /// <summary>
        /// 切換至指定的分類索引標籤並重新載入卡片。
        /// </summary>
        private void SwitchCategoryTab(string tag)
        {
            if (_currentCategoryTag == tag) return;
            _currentCategoryTag = tag;

            // 同步 NavigationView 選取狀態（LB/RB 肩鍵觸發時需要）
            foreach (NavigationViewItem navItem in PlatformCategoryNav.MenuItems.Cast<NavigationViewItem>())
            {
                if (navItem.Tag is string t && t == tag)
                {
                    PlatformCategoryNav.SelectedItem = navItem;
                    break;
                }
            }

            LoadPlatformCards();
        }

        /// <summary>
        /// 根據目前分類索引標籤載入對應的平台卡片清單。
        /// 使用者索引標籤需先通過免責聲明同意檢查。
        /// </summary>
        private void LoadPlatformCards()
        {
            bool isUserTab = _currentCategoryTag == "User";
            bool isConsented = SettingsService.GetCustomPlatformConsentAccepted();

            // 使用者索引標籤未同意時：顯示免責聲明，隱藏卡片和手把提示
            VisualStateManager.GoToState(this, (isUserTab && !isConsented) ? "ConsentVisible" : "GridViewVisible", false);
            UpdateGamepadHints();

            if (isUserTab)
            {
                // 使用者自訂平台
                var userDefinitions = UserPlatformStore.GetAllDefinitions();
                _cardItems = userDefinitions
                    .Select(p => new PlatformCardItem
                    {
                        Platform = p,
                        DisplayName = UserPlatformStore.FindEntryById(p.Id)?.DisplayName ?? p.Id,
                    })
                    .ToList();
            }
            else
            {
                // 系統內建平台
                _cardItems = PlatformCatalog.All
                    .Select(p => new PlatformCardItem
                    {
                        Platform = p,
                        DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                    })
                    .ToList();
            }

            PlatformGridView.ItemsSource = _cardItems;

            // 還原選取狀態
            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }

            // 非同步查詢可用性
            _ = LoadPlatformAvailabilityAsync();
        }

        // ── 平台匯出 / 匯入 ───────────────────────────────────────────────────

        /// <summary>
        /// 卡片右鍵選單開啟前呼叫：非使用者索引標籤時直接關閉 flyout，不顯示選單。
        /// </summary>
        private void CardContextMenu_Opening(object sender, object e)
        {
            if (_currentCategoryTag != "User")
                (sender as MenuFlyout)?.Hide();
        }

        /// <summary>
        /// 卡片右鍵選單「匯出」點選時，將平台設定序列化為 JSON 並複製到剪貼簿。
        /// </summary>
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

        /// <summary>
        /// 使用者索引標籤右側「匯入」按鈕點選時，顯示 ImportPlatformDialog。
        /// 驗證通過後寫入 UserPlatformStore 並重新載入卡片。
        /// </summary>
        private async void ImportPlatformButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                // 若匯出成功提示仍開著，先強制關閉再顯示 Dialog，
                // 避免 TeachingTip 與 ContentDialog.ShowAsync() 同時存在導致崩潰。
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                var dialog = new ImportPlatformDialog(this.XamlRoot, _resourceLoader);
                StopGamepadPolling();
                var result = await dialog.ShowAsync();
                StartGamepadPolling();
                if (result != ContentDialogResult.Primary || dialog.ResultEntry is null) return;

                UserPlatformStore.Add(dialog.ResultEntry);
                LoadPlatformCards();
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 平台編輯對話方塊 ──────────────────────────────────────────────────

        /// <summary>
        /// 底部提示列「Y 新增」按鈕的滑鼠點選處理。
        /// </summary>
        private void AddPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            _ = ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 底部提示列「X 編輯」按鈕的滑鼠點選處理。
        /// 編輯目前 GridView 中選取的使用者平台。
        /// </summary>
        private void EditPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentCategoryTag != "User") return;
            if (PlatformGridView.SelectedItem is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>
        /// 顯示新增/編輯使用者平台的 PlatformEditDialog。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        private async Task ShowPlatformEditDialogAsync(UserPlatformEntry? existingEntry)
        {
            if (_isDialogOpen) return;
            _isDialogOpen = true;

            try
            {
                _exportTipTimer.Stop();
                ExportSuccessTeachingTip.IsOpen = false;

                bool isEdit = existingEntry != null;
                var dialog = new PlatformEditDialog(
                    this.XamlRoot, _resourceLoader,
                    Hwnd, existingEntry);

                StopGamepadPolling();
                var result = await dialog.ShowAsync();
                StartGamepadPolling();

                if (result == ContentDialogResult.Primary && dialog.ResultEntry != null)
                {
                    var entry = dialog.ResultEntry;

                    // 匯入卡片背景圖（縮放至 800x560）
                    if (dialog.PendingIconFile != null)
                    {
                        if (!string.IsNullOrEmpty(entry.IconFileName))
                            UserPlatformStore.DeleteIconFile(entry.IconFileName);
                        entry.IconFileName = await UserPlatformStore.ImportIconAsync(dialog.PendingIconFile);
                    }

                    if (isEdit)
                        UserPlatformStore.Update(entry);
                    else
                        UserPlatformStore.Add(entry);

                    LoadPlatformCards();
                }
                else if (result == ContentDialogResult.Secondary && isEdit && existingEntry != null)
                {
                    // 刪除平台：從 Store 移除後，視剩餘數量決定留在使用者索引標籤或切回系統索引標籤
                    UserPlatformStore.Delete(existingEntry.Id);

                    var remainingUser = UserPlatformStore.GetAllDefinitions();
                    if (remainingUser.Count > 0)
                    {
                        // 使用者索引標籤仍有其他平台，留在使用者索引標籤並選取第一個
                        _selectedPlatformId = remainingUser[0].Id;
                        LoadPlatformCards();
                    }
                    else
                    {
                        // 使用者索引標籤已無平台，切換至系統索引標籤
                        _selectedPlatformId = PlatformCatalog.All[0].Id;
                        _currentCategoryTag = "";
                        SwitchCategoryTab("System");
                    }
                }
            }
            finally
            {
                _isDialogOpen = false;
            }
        }

        // ── 手把輸入處理 ──────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 Xbox 手把的輸入輪詢機制。
        /// 若尚未初始化 <see cref="GamepadNavigationService"/>，則會在此建立其實體，
        /// 以 <see cref="SettingsNav"/> 為 XY 焦點根容器，並傳遞各按鍵回呼函式。
        /// </summary>
        public void StartGamepadPolling()
        {
            if (_gamepadNavigationService == null)
            {
                _gamepadNavigationService = new GamepadNavigationService(
                    this.SettingsNav,
                    this.DispatcherQueue,
                    OnGamepadAButtonPressed,
                    OnGamepadBButtonPressed,
                    OnGamepadLBPressed,
                    OnGamepadRBPressed,
                    OnGamepadXButtonPressed,
                    OnGamepadYButtonPressed,
                    OnGamepadMenuButtonPressed
                );
            }
            _gamepadNavigationService.Start();
        }

        /// <summary>
        /// 停止 Xbox 手把的輸入輪詢機制。
        /// 於結束應用程式或離開設定介面時呼叫。
        /// </summary>
        public void StopGamepadPolling()
        {
            _gamepadNavigationService?.Stop();
        }

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式（設定介面）。
        /// 依焦點所在元素分派：GridViewItem 選取平台、NavigationViewItem 切換頁面、各控制項觸發對應操作。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);

            switch (focused)
            {
                // 平台卡片：確認選取（不可用卡片不響應，避免意外切換預設平台）
                case GridViewItem { Content: PlatformCardItem { IsAvailable: true } card }:
                    PlatformGridView.SelectedItem = card;
                    _selectedPlatformId = card.Id;
                    break;

                // 分類索引標籤（系統 / 使用者）：透過 SwitchCategoryTab 統一切換
                case NavigationViewItem navItem when PlatformCategoryNav.MenuItems.Contains(navItem):
                    if (navItem.Tag is string categoryTag)
                        SwitchCategoryTab(categoryTag);
                    break;

                // 設定導覽項目（一般 / 進階 / 疑難排解）：選取頁面並收合側邊欄
                case NavigationViewItem navItem:
                    SettingsNav.SelectedItem = navItem;
                    SettingsNav.IsPaneOpen = false;
                    break;

                // NavigationView 內建返回按鈕：無操作（避免誤觸觸發系統行為）
                case Button { Name: "NavigationViewBackButton" }:
                    break;

                // 漢堡選單按鈕：切換側邊欄展開 / 收合狀態
                case FrameworkElement { Name: "TogglePaneButton" }:
                    SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
                    break;

                // 重置 Game Bar 按鈕：觸發殺行程並重新啟動 FSE 的備援流程
                case Button btn when ReferenceEquals(btn, ResetGameBarButton):
                    ResetGameBarButton_Click(this, new RoutedEventArgs());
                    break;

                // 自訂平台免責聲明接受按鈕：同意後解鎖使用者平台索引標籤
                case Button btn when ReferenceEquals(btn, CustomConsentAcceptButton):
                    CustomConsentAcceptButton_Click(this, new RoutedEventArgs());
                    break;

                // 匯入按鈕（使用者索引標籤可見時）：開啟匯入對話方塊
                case Button btn when ReferenceEquals(btn, ImportPlatformButton):
                    ImportPlatformButton_Click(this, new RoutedEventArgs());
                    break;

                // Game Bar 媒體櫃開關：On = 媒體櫃按鈕開啟 OmniConsole 設定；Off = 開啟預設平台
                case ToggleSwitch sw when ReferenceEquals(sw, UseGameBarLibrarySwitch):
                    UseGameBarLibrarySwitch.IsOn = !sw.IsOn;
                    break;

                // Passthrough 開關：切換「首頁 / 媒體櫃按鈕直接導向預設平台，跳過 OmniConsole」
                case ToggleSwitch sw when ReferenceEquals(sw, EnablePassthroughSwitch):
                    EnablePassthroughSwitch.IsOn = !sw.IsOn;
                    break;
            }
        }

        /// <summary>
        /// 處理手把 'B' 鍵被按下的回呼函式。
        /// 導覽選單展開時先收合，否則觸發全域退出。
        /// </summary>
        private void OnGamepadBButtonPressed()
        {
            if (SettingsNav.IsPaneOpen)
            {
                SettingsNav.IsPaneOpen = false;
                return;
            }

            ExitApplicationRequested?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 手把 LB 肩鍵：切換到上一個分類索引標籤。
        /// </summary>
        private void OnGamepadLBPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "User")
                SwitchCategoryTab("System");
        }

        /// <summary>
        /// 手把 RB 肩鍵：切換到下一個分類索引標籤。
        /// </summary>
        private void OnGamepadRBPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "System")
                SwitchCategoryTab("User");
        }

        /// <summary>
        /// 手把 Y 鍵：使用者索引標籤時觸發新增平台。
        /// </summary>
        private void OnGamepadYButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag == "User" && SettingsService.GetCustomPlatformConsentAccepted())
                _ = ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 手把 X 鍵：使用者索引標籤時觸發編輯目前聚焦的平台。
        /// </summary>
        private void OnGamepadXButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (_currentCategoryTag != "User") return;
            if (!SettingsService.GetCustomPlatformConsentAccepted()) return;

            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem gridViewItem &&
                gridViewItem.Content is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

        /// <summary>
        /// 底部提示列「Menu 啟動」按鈕的滑鼠點選處理。
        /// </summary>
        private void LaunchPlatformHintButton_Click(object sender, RoutedEventArgs e)
        {
            OnGamepadMenuButtonPressed();
        }

        /// <summary>
        /// 手把 Menu（☰）鍵：直接啟動目前聚焦（或已選取）的平台，跳過手動 FSE 切換流程。
        /// 僅在 FSE 模式中有效；自訂平台索引標籤需已接受同意聲明。
        /// 若焦點在可用的平台卡片上，先將其設為選取（同 A 鍵），再通知 MainWindow 啟動。
        /// </summary>
        private void OnGamepadMenuButtonPressed()
        {
            if (_currentNavTag != "General") return;
            if (!FseService.IsActive()) return;
            if (_currentCategoryTag == "User" && !SettingsService.GetCustomPlatformConsentAccepted()) return;

            // 若焦點在可用卡片上，先確認選取（更新預設平台）
            var focused = FocusManager.GetFocusedElement(this.XamlRoot);
            if (focused is GridViewItem { Content: PlatformCardItem { IsAvailable: true } card })
            {
                PlatformGridView.SelectedItem = card;
                _selectedPlatformId = card.Id;
            }

            if (string.IsNullOrEmpty(_selectedPlatformId)) return;

            LaunchPlatformDirectlyRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
