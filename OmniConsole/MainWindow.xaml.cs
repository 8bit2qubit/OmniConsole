using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;

namespace OmniConsole
{
    public sealed partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;

        private bool _isMaximized = false;
        private bool _isSettingsMode = false;
        private bool _isShowingSettings = false;
        private IntPtr _hwnd;
        private CancellationTokenSource? _fseExitCts;

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
            _hwnd = WindowNative.GetWindowHandle(this);
            int corner = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            // 設定工作檢視與工作列圖示（使用套件內 Assets 的圖示）
            var iconPath = System.IO.Path.Combine(
                Windows.ApplicationModel.Package.Current.InstalledLocation.Path,
                "Assets", "AppIcon.ico");
            this.AppWindow.SetIcon(iconPath);

            // 訂閱兩個 Page 的導覽與退出事件
            LaunchPageControl.NavigateToSettingsRequested += (_, _) => ShowSettings();
            LaunchPageControl.ExitApplicationRequested += (_, _) => RequestExitApplication();
            SettingsPageControl.ExitApplicationRequested += (_, _) => RequestExitApplication();

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

            // 注入 HWND 至兩個 Page（LaunchPage 供 WS_EX_TOOLWINDOW 設定，SettingsPage 供 PlatformEditDialog FileOpenPicker 使用）
            _hwnd = WindowNative.GetWindowHandle(this);
            LaunchPageControl.Hwnd = _hwnd;
            SettingsPageControl.Hwnd = _hwnd;

            // 首次啟動時設定全螢幕（延遲到 Activated 才執行，避免建構函式中卡住）
            // 在此 Activated 回呼中設定，視窗尚未完成第一次繪製，
            // 可避免 OverlappedPresenter → FullScreen 的可見轉換及其系統音效（Windows Background.wav）
            if (!_isMaximized && !_isSettingsMode)
            {
                _isMaximized = true;
                (AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            }

            // 設定模式不自動啟動平台
            if (_isSettingsMode) return;

            // 若設定面板正在顯示，不自動啟動
            if (_isShowingSettings) return;

            // 已成功完成一次啟動嘗試，不因視窗重新取得焦點而再次啟動
            if (LaunchPageControl.HasLaunchedOnce) return;

            await LaunchPageControl.LaunchDefaultPlatformAsync();
        }

        // ── 頁面切換 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 切換至設定介面：隱藏 LaunchPage、顯示 SettingsPage 並啟動手把輪詢。
        /// </summary>
        public void ShowSettings()
        {
            LaunchPageControl.StopGamepadPolling();
            _isShowingSettings = true;
            LaunchPageControl.Visibility = Visibility.Collapsed;
            SettingsPageControl.Visibility = Visibility.Visible;

            // 切換至全螢幕 Presenter（設定模式下也需要全螢幕，確保無標題列）
            if (this.AppWindow.Presenter?.Kind != AppWindowPresenterKind.FullScreen)
                this.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            this.Activate();

            SettingsPageControl.ShowSettings();
        }

        // ── 全域退出 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 全域退出邏輯。
        /// 若在設定介面中，直接退出應用程式（返回 FSE）。
        /// 若在其他介面且在 FSE 中，觸發退回桌面對話方塊。若不在則直接退出。
        /// </summary>
        private async void RequestExitApplication()
        {
            // 在設定介面時，不需要詢問退回桌面，直接結束回到原本呼叫的介面 (如 FSE) 即可
            if (_isShowingSettings)
            {
                SettingsPageControl.StopGamepadPolling();
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
                            LaunchPageControl.StopGamepadPolling();
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
                LaunchPageControl.StopGamepadPolling();
                Application.Current.Exit();
            }
        }

        // ── App.xaml.cs 呼叫的公開方法 ───────────────────────────────────────

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重設啟動狀態並重新啟動平台。
        /// 若目前不在 FSE 環境，重新檢查 FSE 條件，避免略過引導畫面直接啟動平台。
        /// </summary>
        public void Reactivate()
        {
            if (!FseService.IsActive())
            {
                // FSE 功能未啟用 → 引導啟用，不啟動平台
                if (!FseService.CanActivate())
                {
                    LaunchPageControl.ShowFseNotAvailable();
                    return;
                }
                // FSE 可用但 Home App 尚未設為 OmniConsole（例如仍為 Xbox）→ 引導至設定，不啟動平台
                if (!FseService.IsOmniConsoleSetAsHomeApp())
                {
                    LaunchPageControl.ShowFseHomeAppNotSet();
                    return;
                }
                // FSE 可用且 Home App 已設為 OmniConsole，但目前不在 FSE 中
                // → 與首次啟動相同，觸發 FSE 進入流程後退出，由 Windows 以 FSE 環境重啟
                if (FseService.TryActivate())
                {
                    Application.Current.Exit();
                    return;
                }
                // TryActivate 失敗（系統支援但觸發失敗）→ 繼續正常啟動
            }

            _isShowingSettings = false;
            SettingsPageControl.Visibility = Visibility.Collapsed;
            LaunchPageControl.Visibility = Visibility.Visible;
            (this.AppWindow.Presenter as OverlappedPresenter)?.Maximize();
            LaunchPageControl.Reactivate();
        }

        /// <summary>
        /// 系統未啟用 FSE 時顯示提示畫面。
        /// </summary>
        public void ShowFseNotAvailable()
        {
            LaunchPageControl.ShowFseNotAvailable();
        }

        /// <summary>
        /// FSE 可用但 Home App 未設為 OmniConsole 時顯示提示畫面。
        /// </summary>
        public void ShowFseHomeAppNotSet()
        {
            LaunchPageControl.ShowFseHomeAppNotSet();
        }
    }
}
