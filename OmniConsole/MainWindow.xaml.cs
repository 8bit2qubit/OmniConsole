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

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private bool _isLaunching = false;
        private bool _hasLaunchedOnce = false;
        private bool _isFullScreenSet = false;
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
            this.ExtendsContentIntoTitleBar = true;
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

            // 首次啟動時切換到全螢幕（延遲到 Activated 才執行，避免建構函式中卡住）
            if (!_isFullScreenSet)
            {
                _isFullScreenSet = true;
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
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

                var platform = SettingsService.GetDefaultPlatform();
                string platformName = ProcessLauncherService.GetPlatformDisplayName(platform);

                StatusText.Text = string.Format(_resourceLoader.GetString("Launching"), platformName);

                bool success = await ProcessLauncherService.LaunchPlatformAsync(platform);

                _hasLaunchedOnce = true;

                if (success)
                {
                    // 啟動成功：顯示狀態，等待目標平台進入前景後結束應用程式
                    // 5 秒延遲確保平台已到前景，FSE 不會重啟首頁
                    // 結束後開設定或 Game Bar 重導都是冷啟動全新實例，避免視窗恢復問題
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchSuccess"), platformName);

                    // 立即從工作檢視和工作列隱藏
                    var hwnd = WindowNative.GetWindowHandle(this);
                    int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

                    // 等待目標平台進入前景後結束應用程式
                    await Task.Delay(5000);
                    Application.Current.Exit();
                    return;
                }
                else
                {
                    StatusText.Text = string.Format(_resourceLoader.GetString("LaunchFailed"), platformName);
                    OpenSettingsButton.Visibility = Visibility.Visible;
                    ReturnToDesktopButton.Visibility = Visibility.Visible;
                    OpenSettingsButton.Focus(FocusState.Programmatic);
                    StartLaunchPanelGamepadPolling();
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

            // 確保全螢幕
            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);

            await LaunchDefaultPlatformAsync();
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示，引導使用者透過工具啟用。
        /// </summary>
        public void ShowFseNotAvailable()
        {
            LaunchPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
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

            // 從 PlatformCatalog 動態建立卡片清單（顯示名稱從資源檔讀取）
            _cardItems = PlatformCatalog.All
                .Select(p => new PlatformCardItem
                {
                    Platform = p,
                    DisplayName = ProcessLauncherService.GetPlatformDisplayName(p),
                })
                .ToList();

            PlatformGridView.ItemsSource = _cardItems;

            // 還原上次儲存的選取狀態
            var current = SettingsService.GetDefaultPlatform();
            _selectedPlatformId = current.Id;

            var selectedCard = _cardItems.FirstOrDefault(c => c.Id == _selectedPlatformId);
            if (selectedCard != null)
            {
                PlatformGridView.SelectedItem = selectedCard;
            }

            this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            this.Activate();

            StartGamepadPolling();

            // 非同步查詢各平台可用性，完成後更新卡片狀態（透過 INotifyPropertyChanged 驅動）
            _ = LoadPlatformAvailabilityAsync();
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
                    OnGamepadAButtonPressed
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
            else if (ReferenceEquals(focused, SaveButton))
            {
                SaveButton_Click(this, new RoutedEventArgs());
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
        /// 啟動失敗後，點選「返回桌面」按鈕時呼叫，觸發 FSE 退出流程。
        /// FSE overlay 對話方塊顯示期間使用者無法點選 OmniConsole 的按鈕，
        /// 因此不需要停用按鈕；只需在背景輪詢 IsActive() 等待繼續。
        ///   - 對話方塊繼續 → IsActive() 變 false → Exit()
        ///   - 對話方塊取消 → overlay 消失，OmniConsole 按鈕自動恢復可點選
        ///   - 再次點選本按鈕 → 取消上一輪背景輪詢，重新送 Win+F11
        /// </summary>
        private async void ReturnToDesktopButton_Click(object sender, RoutedEventArgs e)
        {
            _fseExitCts?.Cancel();
            _fseExitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = _fseExitCts.Token;

            FseService.TryExitToDesktop();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(200, token);
                    if (!FseService.IsActive())
                    {
                        Application.Current.Exit();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>
        /// 啟動失敗時為 LaunchPanel 啟動手把輪詢，使 A 鍵可觸發「開啟設定」按鈕。
        /// </summary>
        private void StartLaunchPanelGamepadPolling()
        {
            _launchPanelGamepadService ??= new GamepadNavigationService(
                this.LaunchPanel,
                this.DispatcherQueue,
                OnLaunchPanelGamepadAButtonPressed
            );
            _launchPanelGamepadService.Start();
        }

        /// <summary>
        /// LaunchPanel 中手把 'A' 鍵的處理：焦點在「開啟設定」按鈕時觸發點選。
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
        /// 儲存使用者選擇的預設遊戲平台，並結束應用程式。
        /// </summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var platform = PlatformCatalog.FindById(_selectedPlatformId) ?? PlatformCatalog.All[0];

            SettingsService.SetDefaultPlatform(platform);
            SettingsService.SaveCurrentVersion();

            StopGamepadPolling();
            Application.Current.Exit();
        }
    }
}
