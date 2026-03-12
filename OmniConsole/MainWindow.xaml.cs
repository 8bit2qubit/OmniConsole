using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private bool _isMaximized = false;
        private bool _isSettingsMode = false;
        private readonly ResourceLoader _resourceLoader = new();

        private GamepadNavigationService? _gamepadNavigationService;
        private GamepadNavigationService? _launchPanelGamepadService;
        private CancellationTokenSource? _fseExitCts;

        // 設定介面的平台卡片清單與目前選取的平台 Id
        private List<PlatformCardItem> _cardItems = [];
        private string _selectedPlatformId = "";

        // 目前顯示的平台分類索引標籤（System / User）
        private string _currentCategoryTag = "System";

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public MainWindow()
        {
            InitializeComponent();

            // 移除標題列與邊框，避免全螢幕時出現最小化/最大化/關閉按鈕
            if (this.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
            }

            // 強制直角，避免 Windows 11 預設圓角
            var hwnd = WindowNative.GetWindowHandle(this);
            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            this.Activated += MainWindow_Activated;
        }

        /// <summary>
        /// 在 Activate() 之前呼叫，標記為設定模式，防止 Activated 事件觸發平台啟動。
        /// </summary>
        public void PrepareForSettings()
        {
            _isSettingsMode = true;
        }

        /// <summary>
        /// 處理視窗啟動事件，負責初始化全螢幕狀態並在符合條件時自動啟動預設平台。
        /// </summary>
        private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            // 僅在視窗取得前景焦點時啟動，且防止重入
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (_isLaunching) return;

            // 首次啟動時設定全螢幕（延遲到 Activated 才執行，避免建構函式中卡住）
            // 在此 Activated 回呼中設定，視窗尚未完成第一次繪製，
            // 可避免 OverlappedPresenter → FullScreen 的可見轉換及其系統音效（Windows Background.wav）
            if (!_isMaximized && !_isSettingsMode)
            {
                _isMaximized = true;
                (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            }

            // 已經成功啟動過一次，不再透過 Activated 事件重複啟動
            if (_hasLaunchedOnce) return;

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (SettingsPanel.Visibility == Visibility.Visible) return;

            await LaunchDefaultPlatformAsync();
        }

        // ── 平台啟動 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 先預檢可用性，不可用則顯示錯誤訊息；啟動成功後隱藏視窗，
        /// 輪詢前景視窗確認平台已到前景後結束應用程式。
        /// </summary>
        private async Task LaunchDefaultPlatformAsync()
        {
            if (_isLaunching) return;

            // 若目前為設定模式或尚未設定平台/已更新，則不執行自動啟動
            if (_isSettingsMode || SettingsService.IsFirstRunOrUpdate())
            {
                ShowSettings();
                return;
            }

            _isLaunching = true;

            try
            {
                // 確保為啟動模式
                LaunchPanel.Visibility = Visibility.Visible;
                SettingsPanel.Visibility = Visibility.Collapsed;
                GamepadHintBar.Visibility = Visibility.Collapsed;

                StartLaunchPanelGamepadPolling();

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                // 預檢平台可用性，不可用則直接顯示訊息，避免無謂的啟動嘗試與逾時等待
                if (!await ProcessLauncherService.CheckPlatformAvailableAsync(platform))
                {
                    BrandingText.Visibility = Visibility.Collapsed;
                    StatusText.Text = string.Format(_resourceLoader.GetString("PlatformNotAvailable"), platformName);
                    OpenSettingsButton.Visibility = Visibility.Visible;
                    ReturnToDesktopButton.Visibility = Visibility.Visible;
                    GamepadHintBar.Visibility = Visibility.Visible;
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                    return;
                }

                // 顯示平台圖示與進度指示
                LaunchIconImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(platform.IconAsset));
                LaunchIconBorder.Visibility = Visibility.Visible;
                LaunchProgressRing.IsActive = true;
                LaunchProgressRing.Visibility = Visibility.Visible;

                StatusText.Text = string.Format(_resourceLoader.GetString("Launching"), platformName);

                bool isTimeout = false;
                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 給予足夠的逾時時間來確保平台順利到前景，避免 FSE 重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    var hwnd = WindowNative.GetWindowHandle(this);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // 輪詢前景視窗：一旦前景不再是 OmniConsole，代表平台已到前景，可以安全退出
                    // 最多等 15 秒 (30 * 0.5s)，避免平台啟動但未取得前景的極端情況
                    bool platformToForeground = false;
                    int maxRetries = 30;

                    for (int i = 0; i < maxRetries; i++)
                    {
                        await Task.Delay(500);
                        if (GetForegroundWindow() != hwnd)
                        {
                            platformToForeground = true;
                            break;
                        }
                    }

                    if (platformToForeground)
                    {
                        Application.Current.Exit();
                        return;
                    }

                    // 若逾時仍未取得前景，還原視窗狀態並進入失敗流程
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
                    success = false;
                    isTimeout = true;
                }

                if (!success)
                {
                    // 啟動失敗時隱藏平台圖示與浮水印
                    LaunchIconBorder.Visibility = Visibility.Collapsed;
                    BrandingText.Visibility = Visibility.Collapsed;

                    // 啟動失敗時隱藏進度指示
                    LaunchProgressRing.IsActive = false;
                    LaunchProgressRing.Visibility = Visibility.Collapsed;

                    string errorStringKey = isTimeout ? "LaunchTimeout" : "LaunchFailed";
                    StatusText.Text = string.Format(_resourceLoader.GetString(errorStringKey), platformName);
                    OpenSettingsButton.Visibility = Visibility.Visible;
                    ReturnToDesktopButton.Visibility = Visibility.Visible;
                    GamepadHintBar.Visibility = Visibility.Visible;
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                }
            }
            finally
            {
                _isLaunching = false;
            }
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重新啟動平台。
        /// </summary>
        public async void Reactivate()
        {
            _hasLaunchedOnce = false;
            LaunchPanel.Visibility = Visibility.Visible;

            // 確保最大化
            (this.AppWindow.Presenter as OverlappedPresenter)?.Maximize();

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示，引導使用者透過工具啟用。
        /// </summary>
        public void ShowFseNotAvailable()
        {
            LaunchPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            BrandingText.Visibility = Visibility.Collapsed;
            GamepadHintBar.Visibility = Visibility.Visible;
            StatusText.Text = _resourceLoader.GetString("FseNotAvailable");
            EnableFseButton.Visibility = Visibility.Visible;
            EnableFseButton.Focus(FocusState.Programmatic);
            StartLaunchPanelGamepadPolling();
        }

        /// <summary>
        /// 開啟 Xbox Full Screen Experience Tool 頁面後結束應用程式。
        /// 等待 LaunchUriAsync 完成確保瀏覽器已開啟再退出。
        /// </summary>
        private async void EnableFseButton_Click(object _, RoutedEventArgs __)
        {
            await Windows.System.Launcher.LaunchUriAsync(
                new Uri("https://github.com/8bit2qubit/XboxFullScreenExperienceTool"));
            Application.Current.Exit();
        }

        // ── 設定介面 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 顯示設定介面，從 PlatformCatalog 動態建立卡片清單。
        /// </summary>
        public void ShowSettings()
        {
            // 切換到設定模式
            LaunchPanel.Visibility = Visibility.Collapsed;
            BrandingText.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            GamepadHintBar.Visibility = Visibility.Visible;

            // 初始化 NavigationView，預設選取第一個「一般」項目
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            GeneralPage.Visibility = Visibility.Visible;
            AdvancedPage.Visibility = Visibility.Collapsed;
            TroubleshootPage.Visibility = Visibility.Collapsed;

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

            // 僅在尚未進入 FullScreen 時才切換，避免重複設定造成視覺閃動
            if (this.AppWindow.Presenter?.Kind != AppWindowPresenterKind.FullScreen)
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            this.Activate();

            StartGamepadPolling();

            // 非同步查詢各平台可用性，完成後更新卡片狀態（透過 INotifyPropertyChanged 驅動）
            _ = LoadPlatformAvailabilityAsync();
        }

        /// <summary>
        /// 處理 NavigationView 選項變更，切換內容頁面。
        /// </summary>
        private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem selectedItem)
            {
                string? tag = selectedItem.Tag?.ToString();

                // 切換頁面可見性
                GeneralPage.Visibility = (tag == "General") ? Visibility.Visible : Visibility.Collapsed;
                AdvancedPage.Visibility = (tag == "Advanced") ? Visibility.Visible : Visibility.Collapsed;
                TroubleshootPage.Visibility = (tag == "Troubleshoot") ? Visibility.Visible : Visibility.Collapsed;

                // 非「一般」頁面時隱藏 LB/RB 與 Y/X 提示
                bool isGeneral = tag == "General";
                GamepadHintLBRB.Visibility = isGeneral ? Visibility.Visible : Visibility.Collapsed;
                GamepadHintY.Visibility = (isGeneral && _currentCategoryTag == "User") ? Visibility.Visible : Visibility.Collapsed;
                GamepadHintX.Visibility = (isGeneral && _currentCategoryTag == "User") ? Visibility.Visible : Visibility.Collapsed;
            }
        }

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

        /// <summary>
        /// 強制結束 GameBar.exe，透過 URI 重新啟動後再觸發 FSE。
        /// 當 FSE 進入對話方塊卡住時，透過此方法可重置環境並達成「殺死後重發」的備援路徑。
        /// </summary>
        private async void ResetGameBarButton_Click(object sender, RoutedEventArgs e)
        {
            ResetGameBarButton.IsEnabled = false;

            // 1. 殺掉 GameBar（先 GameBar 再 GameBarFTServer）
            FseService.KillGameBar();
            await Task.Delay(500);

            // 2. 透過 URI 確保 GameBar 重新啟動（系統有時不會自動重啟）
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-gamebar://"));
            await Task.Delay(500);

            // 3. 再次殺掉以繞過 FSE 進入對話方塊（「殺死後重發」機制）
            FseService.KillGameBar();
            await Task.Delay(500);

            if (FseService.TryActivate())
            {
                // 此應用程式會被重新啟動在 FSE 環境
                Application.Current.Exit();
            }

            ResetGameBarButton.IsEnabled = true;
        }

        // ── 手把輸入處理 ─────────────────────────────────────────────────────

        /// <summary>
        /// 啟動 Xbox 手把的輸入輪詢機制。
        /// 若尚未初始化 <see cref="GamepadNavigationService"/>，則會在此建立其實體，並傳遞 UI 根容器與 A 鍵的回呼函式。
        /// </summary>
        private void StartGamepadPolling()
        {
            if (_gamepadNavigationService == null)
            {
                _gamepadNavigationService = new GamepadNavigationService(
                    this.SettingsPanel,
                    this.DispatcherQueue,
                    OnGamepadAButtonPressed,
                    OnGamepadBButtonPressed,
                    OnGamepadLBPressed,
                    OnGamepadRBPressed,
                    OnGamepadXButtonPressed,
                    OnGamepadYButtonPressed
                );
            }
            _gamepadNavigationService.Start();
        }

        /// <summary>
        /// 停止 Xbox 手把的輸入輪詢機制。
        /// 於結束應用程式或離開需要手把輸入的畫面時呼叫。
        /// </summary>
        private void StopGamepadPolling()
        {
            _gamepadNavigationService?.Stop();
        }

        /// <summary>
        /// 處理手把 'A' 鍵被按下的回呼函式。
        /// 焦點在 GridViewItem 上時確認選取該平台；焦點在儲存按鈕時觸發儲存。
        /// </summary>
        private void OnGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);

            if (focused is GridViewItem gridViewItem &&
                gridViewItem.Content is PlatformCardItem card &&
                card.IsAvailable)
            {
                PlatformGridView.SelectedItem = card;
                _selectedPlatformId = card.Id;
            }
            else if (focused is NavigationViewItem navItem)
            {
                // 判斷此 NavigationViewItem 屬於哪個 NavigationView
                if (PlatformCategoryNav.MenuItems.Contains(navItem))
                {
                    // 分類索引標籤（系統/使用者）：透過 SwitchCategoryTab 切換
                    if (navItem.Tag is string tag)
                        SwitchCategoryTab(tag);
                }
                else
                {
                    // 設定導覽項目（一般/進階/疑難排解）
                    SettingsNav.SelectedItem = navItem;
                    SettingsNav.IsPaneOpen = false;
                }
            }
            else if (focused is Button btn && btn.Name == "NavigationViewBackButton")
            {
                // 忽略返回按鈕
            }
            else if (focused is DependencyObject dep && dep.GetType().Name == "Button" &&
                     FocusManager.GetFocusedElement(this.Content.XamlRoot) is FrameworkElement fe &&
                     fe.Name == "TogglePaneButton")
            {
                // 處理漢堡按鈕的點選
                SettingsNav.IsPaneOpen = !SettingsNav.IsPaneOpen;
            }
            else if (ReferenceEquals(focused, ResetGameBarButton))
            {
                ResetGameBarButton_Click(this, new RoutedEventArgs());
            }
            else if (ReferenceEquals(focused, UseGameBarLibrarySwitch))
            {
                UseGameBarLibrarySwitch.IsOn = !UseGameBarLibrarySwitch.IsOn;
            }
            else if (ReferenceEquals(focused, EnablePassthroughSwitch))
            {
                EnablePassthroughSwitch.IsOn = !EnablePassthroughSwitch.IsOn;
            }
            else if (ReferenceEquals(focused, CustomConsentAcceptButton))
            {
                CustomConsentAcceptButton_Click(this, new RoutedEventArgs());
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

            RequestExitApplication();
        }

        /// <summary>
        /// 全域退出邏輯。
        /// 若在設定介面中，直接退出應用程式（返回 FSE）。
        /// 若在其他介面且在 FSE 中，觸發退回桌面對話方塊。若不在則直接退出。
        /// </summary>
        private async void RequestExitApplication()
        {
            // 在設定介面時，不需要詢問退回桌面，直接結束回到原本呼叫的介面 (如 FSE) 即可
            if (SettingsPanel.Visibility == Visibility.Visible)
            {
                StopGamepadPolling();
                Application.Current.Exit();
                return;
            }

            // 啟動失敗等其他介面時，若系統啟用了 FSE 機制，必須透過模擬 Win+F11 叫出退回桌面對話方塊
            // 例如：啟動失敗後，點選「返回桌面」按鈕時呼叫，觸發 FSE 退出流程
            // FSE 退出對話方塊顯示期間使用者無法點選 OmniConsole 的按鈕，
            // 因此不需要停用按鈕；只需在背景輪詢 IsActive() 等待繼續
            //   - 對話方塊繼續 → IsActive() 變 false → Exit()
            //   - 對話方塊取消 → FSE 退出對話方塊消失，OmniConsole 按鈕可以點選
            //   - 再次點選「返回桌面」按鈕 → 取消上一輪背景輪詢，重新送 Win+F11
            if (FseService.IsActive())
            {
                _fseExitCts?.Cancel();
                _fseExitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var token = _fseExitCts.Token;

                FseService.TryExitToDesktop();

                try
                {
                    // 一旦 IsActive() 變成 false，代表對話方塊通過且準備退回桌面，此時可安全結束此應用程式。
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(200, token);
                        if (!FseService.IsActive())
                        {
                            StopGamepadPolling();
                            Application.Current.Exit();
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }
            // 若為一般視窗模式、或是尚未進入 FSE 環境時，一律直接退出應用程式
            else
            {
                StopGamepadPolling();
                Application.Current.Exit();
            }
        }

        // ── 設定事件處理 ─────────────────────────────────────────────────────

        /// <summary>
        /// 啟動失敗後，點選「開啟設定」按鈕時呼叫，切換至設定介面。
        /// </summary>
        private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _launchPanelGamepadService?.Stop();
            OpenSettingsButton.Visibility = Visibility.Collapsed;
            ReturnToDesktopButton.Visibility = Visibility.Collapsed;
            ShowSettings();
        }

        /// <summary>
        /// 啟動失敗後，點選「返回桌面」按鈕時呼叫，觸發退出流程。
        /// </summary>
        private void ReturnToDesktopButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExitApplication();
        }

        /// <summary>
        /// 啟動失敗時為 LaunchPanel 啟動手把輪詢，使 A 鍵可觸發按鈕。
        /// </summary>
        private void StartLaunchPanelGamepadPolling()
        {
            _launchPanelGamepadService ??= new GamepadNavigationService(
                this.LaunchPanel,
                this.DispatcherQueue,
                OnLaunchPanelGamepadAButtonPressed,
                OnGamepadBButtonPressed
            );
            _launchPanelGamepadService.Start();
        }

        /// <summary>
        /// LaunchPanel 中手把 'A' 鍵的處理：焦點在按鈕時觸發點選。
        /// </summary>
        private void OnLaunchPanelGamepadAButtonPressed()
        {
            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (ReferenceEquals(focused, OpenSettingsButton))
                OpenSettingsButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, ReturnToDesktopButton))
                ReturnToDesktopButton_Click(this, new RoutedEventArgs());
            else if (ReferenceEquals(focused, EnableFseButton))
                EnableFseButton_Click(this, new RoutedEventArgs());
        }

        /// <summary>
        /// 當 Game Bar 媒體櫃設定開關切換時，立即儲存設定。
        /// </summary>
        private void UseGameBarLibrarySwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetUseGameBarLibraryForSettings(UseGameBarLibrarySwitch.IsOn);
        }

        /// <summary>
        /// 底部提示列「B 退出」按鈕的滑鼠點選處理。
        /// </summary>
        private void ExitHintButton_Click(object sender, RoutedEventArgs e)
        {
            RequestExitApplication();
        }

        /// <summary>
        /// 當 Passthrough 開關切換時，立即儲存設定。
        /// </summary>
        private void EnablePassthroughSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.SetEnablePassthrough(EnablePassthroughSwitch.IsOn);
        }

        /// <summary>
        /// 使用者接受自訂平台免責聲明後，儲存同意狀態並載入自訂平台卡片。
        /// </summary>
        private void CustomConsentAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.SetCustomPlatformConsentAccepted(true);
            LoadPlatformCards();
        }

        // ── 平台分類索引標籤切換 ──────────────────────────────────────────────────

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
            bool showUserContent = isUserTab && isConsented;

            // 使用者索引標籤未同意時：顯示免責聲明，隱藏卡片和手把提示
            CustomConsentPanel.Visibility = (isUserTab && !isConsented) ? Visibility.Visible : Visibility.Collapsed;
            PlatformGridView.Visibility = (isUserTab && !isConsented) ? Visibility.Collapsed : Visibility.Visible;

            // 切換手把提示的可見性（僅已同意的使用者索引標籤才顯示 Y/X）
            GamepadHintY.Visibility = showUserContent ? Visibility.Visible : Visibility.Collapsed;
            GamepadHintX.Visibility = showUserContent ? Visibility.Visible : Visibility.Collapsed;
            GamepadHintLBRB.Visibility = (GeneralPage.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

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

        /// <summary>
        /// 手把 LB 肩鍵：切換到上一個分類索引標籤。
        /// </summary>
        private void OnGamepadLBPressed()
        {
            if (GeneralPage.Visibility != Visibility.Visible) return;
            if (_currentCategoryTag == "User")
                SwitchCategoryTab("System");
        }

        /// <summary>
        /// 手把 RB 肩鍵：切換到下一個分類索引標籤。
        /// </summary>
        private void OnGamepadRBPressed()
        {
            if (GeneralPage.Visibility != Visibility.Visible) return;
            if (_currentCategoryTag == "System")
                SwitchCategoryTab("User");
        }

        /// <summary>
        /// 手把 Y 鍵：使用者索引標籤時觸發新增平台。
        /// </summary>
        private void OnGamepadYButtonPressed()
        {
            if (GeneralPage.Visibility != Visibility.Visible) return;
            if (_currentCategoryTag == "User" && SettingsService.GetCustomPlatformConsentAccepted())
                _ = ShowPlatformEditDialogAsync(null);
        }

        /// <summary>
        /// 手把 X 鍵：使用者索引標籤時觸發編輯目前聚焦的平台。
        /// </summary>
        private void OnGamepadXButtonPressed()
        {
            if (GeneralPage.Visibility != Visibility.Visible) return;
            if (_currentCategoryTag != "User") return;
            if (!SettingsService.GetCustomPlatformConsentAccepted()) return;

            var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is GridViewItem gridViewItem &&
                gridViewItem.Content is PlatformCardItem card)
            {
                var entry = UserPlatformStore.FindEntryById(card.Id);
                if (entry != null)
                    _ = ShowPlatformEditDialogAsync(entry);
            }
        }

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

        // ── 平台編輯對話方塊 ──────────────────────────────────────────────────

        /// <summary>暫存使用者選取的圖示檔案（對話方塊期間使用）。</summary>
        private Windows.Storage.StorageFile? _pendingIconFile;

        /// <summary>
        /// 危險字元清單，用於防止命令注入。
        /// </summary>
        private static readonly char[] DangerousChars = ['|', '&', ';', '>', '<', '`', '$'];

        /// <summary>
        /// 驗證平台名稱是否合法。
        /// </summary>
        private string? ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (name.Length > 50)
                return _resourceLoader.GetString("PlatformDialog_ValidationNameTooLong");
            if (name.Any(c => char.IsControl(c)))
                return _resourceLoader.GetString("PlatformDialog_ValidationNameInvalid");
            return null;
        }

        /// <summary>
        /// 驗證 URI 格式。
        /// </summary>
        private string? ValidateUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (uri.Length > 2048)
                return _resourceLoader.GetString("PlatformDialog_ValidationUriTooLong");
            if (!System.Text.RegularExpressions.Regex.IsMatch(uri, @"^[a-zA-Z][a-zA-Z0-9+\-.]*://\S*$"))
                return _resourceLoader.GetString("PlatformDialog_ValidationUriInvalid");
            if (uri.IndexOfAny(DangerousChars) >= 0)
                return _resourceLoader.GetString("PlatformDialog_ValidationUriInvalid");
            return null;
        }

        /// <summary>
        /// 驗證執行檔路徑格式。
        /// </summary>
        private string? ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (path.Length > 260)
                return _resourceLoader.GetString("PlatformDialog_ValidationPathTooLong");
            if (path.IndexOfAny(DangerousChars) >= 0)
                return _resourceLoader.GetString("PlatformDialog_ValidationPathInvalid");
            if (path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                return _resourceLoader.GetString("PlatformDialog_ValidationPathInvalid");
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return _resourceLoader.GetString("PlatformDialog_ValidationPathNotExe");
            return null;
        }

        /// <summary>
        /// 驗證啟動參數。
        /// </summary>
        private string? ValidateArgs(string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            if (args.Length > 500)
                return _resourceLoader.GetString("PlatformDialog_ValidationArgsTooLong");
            if (args.IndexOfAny(DangerousChars) >= 0)
                return _resourceLoader.GetString("PlatformDialog_ValidationArgsInvalid");
            return null;
        }

        /// <summary>
        /// 顯示新增/編輯使用者平台的 ContentDialog。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        private async Task ShowPlatformEditDialogAsync(UserPlatformEntry? existingEntry)
        {
            bool isEdit = existingEntry != null;
            bool isExecutable = existingEntry?.LaunchType == "Executable";
            bool isPackagedApp = existingEntry?.LaunchType == "PackagedApp";
            _pendingIconFile = null;

            // 封裝應用程式快取（首次切換至「封裝套件」模式時延遲載入）
            List<(string DisplayName, string PackageFamilyName)>? packagedAppCache = null;
            string selectedPackageFamilyName = existingEntry?.PackageFamilyName ?? "";

            // 平台名稱
            var nameBox = new TextBox
            {
                PlaceholderText = _resourceLoader.GetString("PlatformDialog_NamePlaceholder"),
                Text = existingEntry?.DisplayName ?? "",
                MaxLength = 50,
                Margin = new Thickness(0, 0, 0, 4),
            };

            // 名稱驗證錯誤提示
            var nameError = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // 啟動類型選擇（Protocol URI / 執行檔 / 封裝套件）
            var launchTypeCombo = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 12),
                Items =
                {
                    "Protocol URI",
                    _resourceLoader.GetString("PlatformDialog_Executable"),
                    _resourceLoader.GetString("PlatformDialog_PackagedApp"),
                },
                SelectedIndex = isPackagedApp ? 2 : isExecutable ? 1 : 0,
            };

            // 動態標籤：依啟動類型顯示 URI / 路徑 / 封裝套件
            var targetLabel = new TextBlock
            {
                Text = isPackagedApp
                    ? _resourceLoader.GetString("PlatformDialog_PackagedAppLabel")
                    : isExecutable
                        ? _resourceLoader.GetString("PlatformDialog_PathLabel")
                        : _resourceLoader.GetString("PlatformDialog_UriLabel"),
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isPackagedApp ? Visibility.Collapsed : Visibility.Visible,
            };

            // URI 或執行檔路徑輸入
            var targetBox = new TextBox
            {
                PlaceholderText = isExecutable
                    ? _resourceLoader.GetString("PlatformDialog_PathPlaceholder")
                    : _resourceLoader.GetString("PlatformDialog_UriPlaceholder"),
                Text = existingEntry?.LaunchTarget ?? "",
                MaxLength = isExecutable ? 260 : 2048,
                Margin = new Thickness(0, 0, 0, 4),
            };

            // 目標驗證錯誤提示
            var targetError = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // 瀏覽按鈕（僅執行檔模式可見）
            var browseExeButton = new Button
            {
                Content = "...",
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = isExecutable ? Visibility.Visible : Visibility.Collapsed,
            };

            // 目標路徑行：TextBox + 瀏覽按鈕（封裝套件模式隱藏）
            var targetRow = new Grid
            {
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isPackagedApp ? Visibility.Collapsed : Visibility.Visible,
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                ColumnSpacing = 8,
            };
            targetBox.Margin = new Thickness(0);
            Grid.SetColumn(targetBox, 0);
            Grid.SetColumn(browseExeButton, 1);
            targetRow.Children.Add(targetBox);
            targetRow.Children.Add(browseExeButton);

            // 啟動參數（Protocol URI 模式隱藏）
            var argsLabel = new TextBlock
            {
                Text = _resourceLoader.GetString("PlatformDialog_ArgsLabel"),
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isExecutable ? Visibility.Visible : Visibility.Collapsed,
            };

            // 啟動參數輸入
            var argsBox = new TextBox
            {
                PlaceholderText = _resourceLoader.GetString("PlatformDialog_ArgsPlaceholder"),
                Text = existingEntry?.Arguments ?? "",
                MaxLength = 500,
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isExecutable ? Visibility.Visible : Visibility.Collapsed,
            };

            // 參數驗證錯誤提示
            var argsError = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // 封裝應用程式搜尋（僅「封裝套件」模式可見）
            var packagedAppWarning = new TextBlock
            {
                Text = _resourceLoader.GetString("PlatformDialog_PackagedAppWarning"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isPackagedApp ? Visibility.Visible : Visibility.Collapsed,
            };

            var packagedAppLabel = new TextBlock
            {
                Text = _resourceLoader.GetString("PlatformDialog_PackagedAppLabel"),
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isPackagedApp ? Visibility.Visible : Visibility.Collapsed,
            };

            // 封裝應用程式 AutoSuggestBox：輸入即時過濾已安裝套件
            var packagedAppSuggestBox = new AutoSuggestBox
            {
                PlaceholderText = _resourceLoader.GetString("PlatformDialog_PackagedAppPlaceholder"),
                Text = selectedPackageFamilyName,
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isPackagedApp ? Visibility.Visible : Visibility.Collapsed,
            };

            // 封裝應用程式驗證錯誤提示
            var packagedAppError = new TextBlock
            {
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
                FontSize = 12,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 8),
            };

            // 載入已安裝封裝應用程式清單（快取），排除自身與 Game Bar 以防循環啟動
            string ownFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
            const string gameBarFamilyName = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";
            List<(string DisplayName, string PackageFamilyName)> EnsurePackagedAppCache()
            {
                if (packagedAppCache != null) return packagedAppCache;

                packagedAppCache = [];
                try
                {
                    var pm = new Windows.Management.Deployment.PackageManager();
                    foreach (var pkg in pm.FindPackagesForUser(string.Empty))
                    {
                        try
                        {
                            if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;
                            if (pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                            // 排除自身與 Game Bar，避免循環啟動
                            if (pkg.Id.FamilyName == ownFamilyName || pkg.Id.FamilyName == gameBarFamilyName) continue;

                            string name = pkg.Id.Name;

                            // 全域過濾：去除任何發行商的延伸模組或系統功能負載
                            if (name.Contains("Extension", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.Contains("DecoderOEM", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.Contains("ASUSCommandCenter", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.Contains("RSXCM", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.StartsWith("aimgr", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.StartsWith("ASUSAmbientHAL", StringComparison.OrdinalIgnoreCase)) continue;
                            if (name.StartsWith("WindowsWorkload.", StringComparison.OrdinalIgnoreCase)) continue;

                            // 排除沒有進入點的微軟套件
                            bool isMicrosoftOrOfficial = name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                                                         name.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase) ||
                                                         name.StartsWith("MicrosoftCorporationII.", StringComparison.OrdinalIgnoreCase);
                            if (isMicrosoftOrOfficial)
                            {
                                if (name.Contains("WinAppRuntime", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("ExperiencePack", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("IdentityProvider", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Notification", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("GamingServices", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Overlay", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("TCUI", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("OneDriveSync", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("PurchaseApp", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("ActionsServer", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("AppInstaller", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("Handwriting", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("GameAssist", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("CrossDevice", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("DevHome", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("WidgetsPlatformRuntime", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("WebExperience", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("StartExperiences", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("ApplicationCompatibility", StringComparison.OrdinalIgnoreCase) ||
                                    name.Contains("AutoSuperResolution", StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }

                            string displayName = pkg.DisplayName;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            packagedAppCache.Add((displayName, pkg.Id.FamilyName));
                        }
                        catch { /* 部分系統套件存取 DisplayName 會拋例外 */ }
                    }
                    packagedAppCache = packagedAppCache
                        .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PackagedApp] Package enumeration failed: {ex.Message}");
                }
                return packagedAppCache;
            }

            // 聚焦時顯示完整清單（點選欄位即可瀏覽）
            packagedAppSuggestBox.GotFocus += (sender, _) =>
            {
                var box = (AutoSuggestBox)sender;
                var cache = EnsurePackagedAppCache();
                box.ItemsSource = cache
                    .Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})")
                    .ToList();
                box.IsSuggestionListOpen = true;
            };

            // AutoSuggestBox 文字變更時過濾套件清單
            packagedAppSuggestBox.TextChanged += (sender, args) =>
            {
                if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
                var cache = EnsurePackagedAppCache();
                string query = sender.Text.Trim();
                sender.ItemsSource = string.IsNullOrEmpty(query)
                    ? cache.Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})").ToList()
                    : cache
                        .Where(p => p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                 || p.PackageFamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                        .Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})")
                        .ToList();
            };

            // 選取建議項目後記錄 PackageFamilyName 並自動填入平台名稱
            packagedAppSuggestBox.SuggestionChosen += (sender, args) =>
            {
                string chosen = args.SelectedItem?.ToString() ?? "";
                var cache = EnsurePackagedAppCache();
                var match = cache.FirstOrDefault(p => $"{p.DisplayName}  ({p.PackageFamilyName})" == chosen);
                if (match != default)
                {
                    selectedPackageFamilyName = match.PackageFamilyName;
                    sender.Text = $"{match.DisplayName}  ({match.PackageFamilyName})";
                    // 自動填入平台名稱（僅在空白時）
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                        nameBox.Text = match.DisplayName;
                }
            };

            // 卡片背景圖瀏覽
            var iconFileNameText = new TextBlock
            {
                Text = existingEntry?.IconFileName ?? "",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray),
                FontSize = 13,
            };

            // 卡片背景圖瀏覽按鈕
            var browseIconButton = new Button
            {
                Content = "...",
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };

            // 卡片背景圖行：檔名 + 瀏覽按鈕
            var iconRow = new Grid
            {
                Margin = new Thickness(0, 0, 0, 12),
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
                ColumnSpacing = 8,
            };
            Grid.SetColumn(iconFileNameText, 0);
            Grid.SetColumn(browseIconButton, 1);
            iconRow.Children.Add(iconFileNameText);
            iconRow.Children.Add(browseIconButton);

            // 切換啟動類型時更新標籤、placeholder、可見性
            launchTypeCombo.SelectionChanged += (_, _) =>
            {
                bool isExe = launchTypeCombo.SelectedIndex == 1;
                bool isPackagedAppMode = launchTypeCombo.SelectedIndex == 2;

                // Protocol URI / 執行檔控制項
                targetLabel.Text = isExe
                    ? _resourceLoader.GetString("PlatformDialog_PathLabel")
                    : _resourceLoader.GetString("PlatformDialog_UriLabel");
                targetBox.PlaceholderText = isExe
                    ? _resourceLoader.GetString("PlatformDialog_PathPlaceholder")
                    : _resourceLoader.GetString("PlatformDialog_UriPlaceholder");
                targetBox.MaxLength = isExe ? 260 : 2048;
                targetLabel.Visibility = isPackagedAppMode ? Visibility.Collapsed : Visibility.Visible;
                targetRow.Visibility = isPackagedAppMode ? Visibility.Collapsed : Visibility.Visible;
                argsLabel.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;
                argsBox.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;
                browseExeButton.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;

                // 封裝應用程式控制項
                packagedAppWarning.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;
                packagedAppLabel.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;
                packagedAppSuggestBox.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;

                // 首次切換至「封裝套件」模式時預載套件清單
                if (isPackagedAppMode) EnsurePackagedAppCache();

                // 清除驗證錯誤
                targetError.Visibility = Visibility.Collapsed;
                argsError.Visibility = Visibility.Collapsed;
                packagedAppError.Visibility = Visibility.Collapsed;
            };

            // 瀏覽執行檔
            browseExeButton.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add(".exe");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                    targetBox.Text = file.Path;
            };

            // 瀏覽卡片背景圖
            browseIconButton.Click += async (_, _) =>
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".bmp");
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    _pendingIconFile = file;
                    iconFileNameText.Text = file.Name;
                }
            };

            // 實驗性功能警告標語（在按鈕上方）
            var configWarning = new TextBlock
            {
                Text = _resourceLoader.GetString("PlatformDialog_ConfigWarning"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange),
                Margin = new Thickness(0, 4, 0, 0),
            };

            // 對話方塊內容面板
            var contentPanel = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = _resourceLoader.GetString("PlatformDialog_NameLabel"), Margin = new Thickness(0, 0, 0, 4) },
                    nameBox,
                    nameError,
                    new TextBlock { Text = _resourceLoader.GetString("PlatformDialog_LaunchTypeLabel"), Margin = new Thickness(0, 0, 0, 4) },
                    launchTypeCombo,
                    targetLabel,
                    targetRow,
                    targetError,
                    argsLabel,
                    argsBox,
                    argsError,
                    packagedAppWarning,
                    packagedAppLabel,
                    packagedAppSuggestBox,
                    packagedAppError,
                    new TextBlock { Text = _resourceLoader.GetString("PlatformDialog_IconLabel"), Margin = new Thickness(0, 0, 0, 4) },
                    new TextBlock { Text = _resourceLoader.GetString("PlatformDialog_IconHint"), FontSize = 12,
                        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Gray), Margin = new Thickness(0, 0, 0, 4) },
                    iconRow,
                    configWarning,
                },
            };

            // 建立對話方塊
            var dialog = new ContentDialog
            {
                Title = isEdit
                    ? _resourceLoader.GetString("PlatformDialog_EditTitle")
                    : _resourceLoader.GetString("PlatformDialog_AddTitle"),
                Content = contentPanel,
                PrimaryButtonText = _resourceLoader.GetString("PlatformDialog_Save"),
                CloseButtonText = _resourceLoader.GetString("PlatformDialog_Cancel"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot,
            };

            // 編輯模式下顯示刪除按鈕
            if (isEdit)
            {
                dialog.SecondaryButtonText = _resourceLoader.GetString("PlatformDialog_Delete");
            }

            // 儲存前驗證：不合法時取消關閉並顯示紅字錯誤
            dialog.PrimaryButtonClick += (_, args) =>
            {
                bool hasError = false;

                string? nameErr = ValidateName(nameBox.Text.Trim());
                if (nameErr != null) { nameError.Text = nameErr; nameError.Visibility = Visibility.Visible; hasError = true; }
                else nameError.Visibility = Visibility.Collapsed;

                bool isExe = launchTypeCombo.SelectedIndex == 1;
                bool isPackagedAppMode = launchTypeCombo.SelectedIndex == 2;

                if (isPackagedAppMode)
                {
                    // 封裝套件驗證：PackageFamilyName 不可為空，且不可選取自身或 Game Bar
                    if (string.IsNullOrWhiteSpace(selectedPackageFamilyName))
                    {
                        packagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppEmpty");
                        packagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else if (selectedPackageFamilyName == ownFamilyName)
                    {
                        packagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppSelf");
                        packagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else if (selectedPackageFamilyName == gameBarFamilyName)
                    {
                        packagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppGameBar");
                        packagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else packagedAppError.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string? targetErr = isExe ? ValidatePath(targetBox.Text.Trim()) : ValidateUri(targetBox.Text.Trim());
                    if (targetErr != null) { targetError.Text = targetErr; targetError.Visibility = Visibility.Visible; hasError = true; }
                    else targetError.Visibility = Visibility.Collapsed;

                    if (isExe)
                    {
                        string? argsErr = ValidateArgs(argsBox.Text.Trim());
                        if (argsErr != null) { argsError.Text = argsErr; argsError.Visibility = Visibility.Visible; hasError = true; }
                        else argsError.Visibility = Visibility.Collapsed;
                    }
                }

                if (hasError) args.Cancel = true;
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                string name = nameBox.Text.Trim();
                string target = targetBox.Text.Trim();

                var entry = existingEntry ?? new UserPlatformEntry();
                entry.DisplayName = name;

                if (launchTypeCombo.SelectedIndex == 2)
                {
                    entry.LaunchType = "PackagedApp";
                    entry.LaunchTarget = "";
                    entry.Arguments = "";
                    entry.PackageFamilyName = selectedPackageFamilyName;
                }
                else
                {
                    entry.LaunchType = launchTypeCombo.SelectedIndex == 1 ? "Executable" : "ProtocolUri";
                    entry.LaunchTarget = target;
                    entry.Arguments = launchTypeCombo.SelectedIndex == 1 ? argsBox.Text.Trim() : "";
                    entry.PackageFamilyName = "";
                }

                // 匯入卡片背景圖（縮放至 800x560）
                if (_pendingIconFile != null)
                {
                    // 先清除舊圖示
                    if (!string.IsNullOrEmpty(entry.IconFileName))
                        UserPlatformStore.DeleteIconFile(entry.IconFileName);

                    entry.IconFileName = await UserPlatformStore.ImportIconAsync(_pendingIconFile);
                }

                if (isEdit)
                    UserPlatformStore.Update(entry);
                else
                    UserPlatformStore.Add(entry);

                LoadPlatformCards();
            }
            else if (result == ContentDialogResult.Secondary && isEdit && existingEntry != null)
            {
                // 刪除
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
    }
}
