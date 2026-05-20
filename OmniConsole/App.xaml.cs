using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using OmniConsole.Services;
using System;
using System.Runtime.InteropServices;

namespace OmniConsole
{
    /// <summary>
    /// 提供應用程式層級的行為與重導啟動的橋接。
    /// </summary>
    public partial class App : Application
    {
        // ── P/Invoke：視窗狀態與 z-order 控制 ──
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        // ── P/Invoke：輸入注入（SendInput + INPUT 結構） ──
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>SendInput 用的輸入事件容器，type 對應事件種類（鍵盤／滑鼠／硬體）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        /// <summary>INPUT 內各事件型別的 union；本檔僅使用鍵盤事件。</summary>
        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        /// <summary>鍵盤輸入事件欄位：wVk 虛擬鍵碼、dwFlags 用 KEYEVENTF_KEYUP 區分按下／放開。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        // ── 常數：ShowWindow nCmdShow ──
        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        // ── 常數：SetWindowPos hWndInsertAfter 與 uFlags ──
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // ── 常數：SendInput INPUT.type 與 KEYBDINPUT 欄位 ──
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_MENU = 0x12;

        private static Window? _window;
        private static DispatcherQueue? _dispatcherQueue;
        private readonly bool _startWithSettings;

        /// <summary>建立 App 實例；showSettings=true 表示由設定入口啟動，跳過 FSE 引導直接顯示設定介面。</summary>
        public App(bool showSettings = false)
        {
            _startWithSettings = showSettings;
            InitializeComponent();
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            // 註冊 FSE 狀態變化通知（取代輪詢），記錄啟動時的 FSE 狀態供診斷
            FseService.StartListening();
            DebugLogger.Log($"[App] IsSupported={FseService.IsSupported()}, CanActivate={FseService.CanActivate()}, IsActive={FseService.IsActive()}");

            // 首次安裝或版本更新時：先遷移舊版設定（若有殘留 LocalCache INI），再同步共用 INI
            // 確保所有欄位存在、含內建手把映射偵測結果；一般啟動不需重跑這兩個動作
            if (SettingsService.IsFirstRunOrUpdate())
            {
                SettingsService.MigrateLegacyIfNeeded();
                SettingsService.SyncPhantomKeyStore();
            }

            // 待續更新優先：偵測到中斷的更新時跳過整段 FSE 引導（不彈系統「重新啟動以提升效能」對話方塊），
            // 直接開設定頁並彈出「是否繼續更新？」對話方塊。
            var pending = UpdateCheckService.GetPendingUpdateState();
            bool hasPendingUpdate = !string.IsNullOrEmpty(pending.Phase);

            // 從桌面環境啟動（非 FSE 模式、非設定模式、非待續更新）時自動觸發 FSE
            if (!_startWithSettings && !FseService.IsActive() && !hasPendingUpdate)
            {
                if (!FseService.IsSupported())
                {
                    // 系統完全不支援 FSE（舊版 Windows 或未啟用任何 FSE），引導使用者先啟用
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable());
                    return;
                }

                if (!FseService.IsHandheldFseAvailable())
                {
                    // 僅支援微軟於 PC 推出的「PC 限制版 FSE」（IsSupported=true 但 DeviceForm≠46），
                    // 不支援 Home App 設定與開機啟動，需引導透過 XFSET 取得掌機完整版
                    ShowGuidanceWindow(w => w.ShowFseNotAvailable(handheldRequired: true));
                    return;
                }

                if (!FseService.CanActivate() || !FseService.IsOmniConsoleSetAsHomeApp())
                {
                    // 掌機完整版可用但 Home App 未設為 OmniConsole（設為「無」、其他 App、或尚未設定）
                    ShowGuidanceWindow(w => w.ShowFseHomeAppNotSet());
                    return;
                }

                // [Windows Bug] Game Bar 未執行時（常見於系統從休眠回復後尚未就緒），FSE 觸發雖會回
                // 傳成功，但 FSE 進入對話方塊不會出現，導致靜默退出後無任何視窗。此為 Windows 本身
                // 的缺陷，非 OmniConsole 可控範圍；先確保 Game Bar 就緒再觸發以避免 FSE 啟動失敗。
                if (!FseService.IsGameBarReady())
                {
                    await FseService.EnsureGameBarReadyAsync();
                    await System.Threading.Tasks.Task.Delay(500);
                }

                // [Windows Bug] 部分應用程式（音訊面板、Windows 設定等）在進入 FSE 後會被最大化並搶
                // 走前景焦點，在進入前先終止以避免干擾。
                FseService.KillIgnoredBackgroundServices();

                if (FseService.TryActivate())
                {
                    // FSE 已觸發，Windows 會重新以 FSE 環境啟動本應用程式
                    ExitApp();
                    return;
                }

                // TryActivate 失敗：使用者在 FSE 進入對話方塊中選擇了「停留在桌面上」，
                // 或系統支援但觸發失敗。不應在桌面環境啟動遊戲平台，而是直接退出。
                // Windows 11 Build 26220.8165+ 的 SetGamingFullScreenExperience 會同步阻塞至使用者選擇，
                // 選擇「Stay on desktop」時回傳 0x80004004 (E_ABORT)。
                ExitApp();
                return;
            }

            ApplyNavigationSoundsSetting();

            var mainWindow = new MainWindow();
            _window = mainWindow;
            _dispatcherQueue = mainWindow.DispatcherQueue;

            // 設定模式：在 Activate 前標記，防止 Activated 事件觸發平台啟動
            if (_startWithSettings || hasPendingUpdate)
            {
                mainWindow.PrepareForSettings();
                mainWindow.ShowSettings();

                if (hasPendingUpdate)
                    _ = mainWindow.TryHandlePendingUpdateAsync();

                // 設定模式啟動時主動搶前景，避免被其他應用程式蓋住
                BringWindowToForeground(mainWindow);
            }
            else
            {
                mainWindow.Activate();
            }
        }

        /// <summary>
        /// 建立引導視窗並顯示指定的引導畫面。
        /// 用於 FSE 不可用、Home App 未設定等需要顯示說明但不執行平台啟動的情境。
        /// </summary>
        private void ShowGuidanceWindow(Action<MainWindow> show)
        {
            ApplyNavigationSoundsSetting();
            var win = new MainWindow();
            _window = win;
            _dispatcherQueue = win.DispatcherQueue;
            win.PrepareForSettings(); // 防止 Activated 觸發平台啟動
            win.Activate();
            show(win);
        }

        /// <summary>
        /// 主實例收到 show-settings / edit-gamepad-profile redirect 時的進入點，在 UI 執行緒上
        /// 切到設定介面並呼叫 BringWindowToForeground 把主視窗搶到前景。
        /// </summary>
        public static void ShowSettingsFromRedirect()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.ShowSettings();
                    BringWindowToForeground(mainWindow);
                }
            });
        }

        /// <summary>
        /// 把指定視窗拉到 OS 前景，兩段策略 + 短路 return：
        ///   第 1 段 SendInput ALT + SetForegroundWindow — 搶到前景就 return
        ///   第 2 段 SetWindowPos HWND_TOPMOST → NOTOPMOST + BringWindowToTop + SetForegroundWindow
        ///     — 在 non-TOPMOST group 內把視窗排到其他普通視窗之上的防禦補位
        /// </summary>
        private static void BringWindowToForeground(Window window)
        {
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (hwnd == IntPtr.Zero) return;

                IntPtr fgBefore = GetForegroundWindow();
                uint fgPidBefore = 0;
                if (fgBefore != IntPtr.Zero) GetWindowThreadProcessId(fgBefore, out fgPidBefore);
                DebugLogger.Log($"[BWF] enter hwnd=0x{hwnd.ToInt64():X} visible={IsWindowVisible(hwnd)} iconic={IsIconic(hwnd)} fgHwnd=0x{fgBefore.ToInt64():X} fgPid={fgPidBefore}");

                // FSE 啟動成功後主視窗會被設成 WS_EX_TOOLWINDOW + ShowWindow(0) 隱藏狀態；
                // IsIconic 只判 minimize 不判 hidden，需另外用 IsWindowVisible 補 SW_SHOW
                if (!IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_SHOW);
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                // 第 1 段：送一組 ALT down + up keystroke 再 SetForegroundWindow
                var inputs = new INPUT[]
                {
                    new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU } } },
                    new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } },
                };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
                bool sfw1 = SetForegroundWindow(hwnd);
                IntPtr fgAfter1 = GetForegroundWindow();
                DebugLogger.Log($"[BWF] ALT+SFW ret={sfw1} fgHwnd=0x{fgAfter1.ToInt64():X} hit={fgAfter1 == hwnd}");
                if (sfw1 && fgAfter1 == hwnd) return;

                // 第 2 段：SetWindowPos HWND_TOPMOST 把視窗拉到 z-order 頂端，立刻降回 HWND_NOTOPMOST
                // 解除 always-on-top 屬性、保留剛升上去的 z-order 位置；BringWindowToTop + SetForegroundWindow 補一次
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                BringWindowToTop(hwnd);
                bool sfw2 = SetForegroundWindow(hwnd);
                IntPtr fgAfter2 = GetForegroundWindow();
                DebugLogger.Log($"[BWF] TOPMOST+SFW ret={sfw2} fgHwnd=0x{fgAfter2.ToInt64():X} hit={fgAfter2 == hwnd}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[App] BringWindowToForeground failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 從 FSE/Game Bar 重導時呼叫，重新啟動平台。
        /// </summary>
        public static void ReactivateFromRedirect()
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                if (_window is MainWindow mainWindow)
                {
                    mainWindow.Reactivate();
                }
            });
        }

        /// <summary>
        /// 依使用者設定將 ElementSoundPlayer 全域狀態切到 On/Off，供內建控制項自動播音；
        /// 手把自訂路徑（GamepadNavigationService）的 Play() 在 State=Off 時為 no-op。
        /// </summary>
        private static void ApplyNavigationSoundsSetting()
        {
            Microsoft.UI.Xaml.ElementSoundPlayer.State =
                SettingsService.GetEnableNavigationSounds()
                    ? Microsoft.UI.Xaml.ElementSoundPlayerState.On
                    : Microsoft.UI.Xaml.ElementSoundPlayerState.Off;
        }

        /// <summary>
        /// 統一退出應用程式。取消 FSE 狀態通知後以 Environment.Exit(0) 終止行程。
        /// </summary>
        public static void ExitApp()
        {
            DebugLogger.Log("[App] ExitApp: stopping FSE listener and exiting");
            FseService.StopListening();
            Environment.Exit(0);
        }

        /// <summary>
        /// 從 Game Bar 重導時呼叫，直接啟動平台專屬 URI (Passthrough) 後退出應用程式。
        /// </summary>
        public static void PassthroughFromRedirect(string uri)
        {
            _dispatcherQueue?.TryEnqueue(() =>
            {
                _ = Windows.System.Launcher.LaunchUriAsync(new Uri(uri));
                // 先隱藏視窗，避免退出時閃白
                if (_window is not null)
                    ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 0);
                ExitApp();
            });
        }
    }
}
