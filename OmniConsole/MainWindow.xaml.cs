using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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

            // 首次啟動時最大化（延遲到 Activated 才執行，避免建構函式中卡住）
            if (!_isMaximized)
            {
                _isMaximized = true;
                (this.AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            }

            // 已經成功啟動過一次，不再透過 Activated 事件重複啟動
            if (_hasLaunchedOnce) return;

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (SettingsPanel.Visibility == Visibility.Visible) return;

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 自動啟動已設定的預設平台。
        /// 啟動成功後將隱藏視窗並在延遲後結束應用程式。
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
                    // 啟動失敗時隱藏平台圖示
                    LaunchIconBorder.Visibility = Visibility.Collapsed;

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

        /// <summary>
        /// 顯示設定介面，從 PlatformCatalog 動態建立卡片清單。
        /// </summary>
        public void ShowSettings()
        {
            // 切換到設定模式
            LaunchPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            GamepadHintBar.Visibility = Visibility.Visible;

            // 初始化 NavigationView，預設選取第一個「一般」項目
            SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
            GeneralPage.Visibility = Visibility.Visible;
            AdvancedPage.Visibility = Visibility.Collapsed;
            TroubleshootPage.Visibility = Visibility.Collapsed;

            // 從 PlatformCatalog 動態建立卡片清單（顯示名稱從資源檔讀取）
            _cardItems = PlatformCatalog.All
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                })
                .ToList();

            PlatformGridView.ItemsSource = _cardItems;

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

            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            (this.AppWindow.Presenter as OverlappedPresenter)?.Maximize();
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

                // 切換分頁可見性
                GeneralPage.Visibility = (tag == "General") ? Visibility.Visible : Visibility.Collapsed;
                AdvancedPage.Visibility = (tag == "Advanced") ? Visibility.Visible : Visibility.Collapsed;
                TroubleshootPage.Visibility = (tag == "Troubleshoot") ? Visibility.Visible : Visibility.Collapsed;
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
                    // 還原為上一個有效選取
                    var previous = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
                    PlatformGridView.SelectedItem = previous;
                    return;
                }

                _selectedPlatformId = selected.Id;

                // 選取即儲存
                var platform = PlatformCatalog.FindById(_selectedPlatformId) ?? PlatformCatalog.All[0];
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
        /// 強制結束 GameBar.exe 並重新嘗試觸發 FSE。
        /// 當 FSE 進入對話方塊卡住時，透過此方法可重置環境並達成「殺死後重發」的備援路徑。
        /// </summary>
        private void ResetGameBarButton_Click(object sender, RoutedEventArgs e)
        {
            FseService.KillGameBar();

            // 給予系統一點時間清理
            System.Threading.Thread.Sleep(500);

            if (FseService.TryActivate())
            {
                // 對話方塊重新觸發成功，使用者點選確認後此應用程式會被重新啟動在 FSE 環境
                Application.Current.Exit();
            }
        }

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
                    OnGamepadBButtonPressed
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
                SettingsNav.SelectedItem = navItem;
                // 選取後自動收合
                SettingsNav.IsPaneOpen = false;
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

    }
}
