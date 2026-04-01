using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OmniConsole.Services
{
    /// <summary>
    /// 封裝 Windows Gaming Full Screen Experience (FSE) 的偵測與觸發。
    /// 使用 api-ms-win-gaming-experience-l1-1-0.dll（Windows API Set，由 OS loader 動態解析）。
    /// </summary>
    public static partial class FseService
    {
        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsGamingFullScreenExperienceActive();

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CanSetGamingFullScreenExperience();

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsGamingFullScreenExperienceSupported();

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        private static extern int SetGamingFullScreenExperience([MarshalAs(UnmanagedType.Bool)] bool active);

        private delegate void GamingFullScreenExperienceChangeRoutine(IntPtr context);

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        private static extern int RegisterGamingFullScreenExperienceChangeNotification(
            GamingFullScreenExperienceChangeRoutine routine,
            IntPtr context,
            out IntPtr registration);

        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        private static extern void UnregisterGamingFullScreenExperienceChangeNotification(
            IntPtr registration);

        /// <summary>callback delegate 必須以欄位持有參照，防止 GC 回收導致 callback 失效。</summary>
        private static GamingFullScreenExperienceChangeRoutine? _changeCallback;
        private static IntPtr _changeRegistration;

        /// <summary>FSE 狀態變化時觸發（可能在背景執行緒）。訂閱者需自行 Dispatch 回 UI 執行緒。</summary>
        public static event Action? StateChanged;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        /// <summary>
        /// FSE 模式下會被最大化並搶走前景焦點的已知背景服務。
        /// 輪詢時忽略這些行程，避免誤判平台已到前景。
        /// </summary>
        private static readonly HashSet<string> _ignoredProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Nahimic3",
            "RtkUWP",
            "SystemSettings",
        };

        /// <summary>
        /// 註冊 FSE 狀態變化通知。呼叫後 StateChanged 事件會在 FSE 進入/退出時觸發。
        /// </summary>
        public static void StartListening()
        {
            if (_changeRegistration != IntPtr.Zero) return;

            _changeCallback = OnFseStateChanged;
            int hr = RegisterGamingFullScreenExperienceChangeNotification(
                _changeCallback, IntPtr.Zero, out _changeRegistration);
            DebugLogger.Log($"[FseService] RegisterChangeNotification HRESULT: 0x{hr:X8}");
        }

        /// <summary>
        /// 取消 FSE 狀態變化通知。
        /// </summary>
        public static void StopListening()
        {
            if (_changeRegistration == IntPtr.Zero) return;

            UnregisterGamingFullScreenExperienceChangeNotification(_changeRegistration);
            DebugLogger.Log("[FseService] UnregisterChangeNotification");
            _changeRegistration = IntPtr.Zero;
            _changeCallback = null;
        }

        /// <summary>FSE 狀態變化時由系統從背景執行緒回呼，轉發為 StateChanged 事件。</summary>
        private static void OnFseStateChanged(IntPtr context)
        {
            bool active = IsGamingFullScreenExperienceActive();
            DebugLogger.Log($"[FseService] StateChanged callback: IsActive = {active}");
            StateChanged?.Invoke();
        }

        /// <summary>
        /// 回傳系統是否支援 FSE（需透過 Xbox Full Screen Experience Tool 啟用或原生 FSE 掌機）。
        /// 與 CanActivate() 的差異：IsSupported() 不受 Home App 設定影響。
        /// </summary>
        public static bool IsSupported()
        {
            try
            {
                bool result = IsGamingFullScreenExperienceSupported();
                DebugLogger.Log($"[FseService] IsSupported = {result}");
                return result;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] IsSupported failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 回傳目前是否處於 FSE 模式（由 Windows FSE 機制啟動）。
        /// </summary>
        public static bool IsActive([CallerMemberName] string caller = "")
        {
            try
            {
                bool result = IsGamingFullScreenExperienceActive();
                DebugLogger.Log($"[FseService] IsActive = {result} (caller: {caller})");
                return result;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] IsActive failed: {ex.Message} (caller: {caller})");
                return false;
            }
        }

        /// <summary>
        /// 回傳目前是否可以觸發 FSE。需要 IsSupported()=true 且 Home App 已設定（非「無」）。
        /// </summary>
        public static bool CanActivate()
        {
            try
            {
                bool result = CanSetGamingFullScreenExperience();
                DebugLogger.Log($"[FseService] CanActivate = {result}");
                return result;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] CanActivate failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 觸發進入 FSE 模式（等同於按 Win+F11 或 Game Bar 的進入 FSE 按鈕）。
        /// 成功後 Windows 會顯示確認對話方塊，使用者確認後重新啟動本應用程式於 FSE 環境。
        /// </summary>
        /// <returns>HRESULT >= 0 為成功。</returns>
        public static bool TryActivate()
        {
            try
            {
                int hr = SetGamingFullScreenExperience(true);
                DebugLogger.Log($"[FseService] SetGamingFullScreenExperience(true) HRESULT: 0x{hr:X8}");
                return hr >= 0;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] TryActivate failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 觸發退出 FSE 模式，Windows 會顯示「切換到 Windows 桌面」確認對話方塊。
        /// 使用者確認後 IsActive() 會變為 false。
        /// </summary>
        public static bool TryDeactivate()
        {
            try
            {
                int hr = SetGamingFullScreenExperience(false);
                DebugLogger.Log($"[FseService] SetGamingFullScreenExperience(false) HRESULT: 0x{hr:X8}");
                return hr >= 0;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] TryDeactivate failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 檢查 GamingHomeApp 是否設為 OmniConsole（比對動態取得的 AUMID）。
        /// 僅在 CanActivate()=true 後呼叫；CanActivate()=false 時不適用。
        /// </summary>
        public static bool IsOmniConsoleSetAsHomeApp()
        {
            try
            {
                string aumid = Windows.ApplicationModel.Package.Current.Id.FamilyName + "!App";
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\GamingConfiguration");
                if (key is null) return false;
                return key.GetValue("GamingHomeApp") is string value &&
                       value.Equals(aumid, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] IsOmniConsoleSetAsHomeApp failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 回傳 Game Bar 是否已就緒（以 GameBarFTServer.exe 為準）。
        /// 休眠回復後 GameBar.exe 可能存在但 GameBarFTServer.exe 尚未啟動，
        /// 此時 FSE 觸發會靜默失敗。
        /// </summary>
        public static bool IsGameBarReady()
        {
            bool ftServerRunning = Process.GetProcessesByName("GameBarFTServer").Length > 0;
            bool gameBarRunning = Process.GetProcessesByName("GameBar").Length > 0;
            DebugLogger.Log($"[FseService] IsGameBarReady: GameBar={gameBarRunning}, GameBarFTServer={ftServerRunning}");
            return ftServerRunning;
        }

        /// <summary>
        /// 確保 Game Bar 完全就緒。若 GameBar.exe 已存在（如休眠回復後的殭屍狀態），
        /// 先終止再透過 ms-gamebar:// URI 重新啟動，輪詢直到 GameBarFTServer.exe 出現或逾時。
        /// GameBarFTServer 是 GameBar 的服務端元件，出現時代表 GameBar 已完成初始化。
        /// </summary>
        /// <param name="timeoutMs">最長等待毫秒數，預設 5000ms。</param>
        public static async System.Threading.Tasks.Task EnsureGameBarReadyAsync(int timeoutMs = 5000)
        {
            // 休眠回復後 GameBar.exe 可能以殭屍狀態殘留，阻止 ms-gamebar:// 正常重啟。
            // 先終止殘留行程，再重新啟動以確保 GameBarFTServer 一併帶起。
            if (Process.GetProcessesByName("GameBar").Length > 0)
            {
                DebugLogger.Log("[FseService] Killing zombie GameBar.exe before restart");
                KillGameBar();
                await System.Threading.Tasks.Task.Delay(500);
            }

            _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-gamebar://"));

            int elapsed = 0;
            const int interval = 200;
            while (elapsed < timeoutMs)
            {
                await System.Threading.Tasks.Task.Delay(interval);
                elapsed += interval;
                if (Process.GetProcessesByName("GameBarFTServer").Length > 0)
                {
                    DebugLogger.Log($"[FseService] GameBar ready after {elapsed}ms");
                    return;
                }
            }

            DebugLogger.Log($"[FseService] EnsureGameBarRunning timed out after {timeoutMs}ms");
        }

        /// <summary>
        /// 強制終止 GameBar.exe 與 GameBarFTServer.exe 行程。
        /// 適用於 FSE 進入對話方塊卡住時的手動修復機制。
        /// </summary>
        public static void KillGameBar()
        {
            string[] names = ["GameBar", "GameBarFTServer"];
            foreach (var name in names)
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                    using (process)
                    {
                        try
                        {
                            DebugLogger.Log($"[FseService] Killing {name}.exe (PID: {process.Id})");
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Log($"[FseService] Kill {name} failed: {ex.Message}");
                        }
                    }
            }
        }

        /// <summary>
        /// 主動終止所有已知會搶前景焦點的干擾應用程式行程。
        /// 音訊面板僅是前端 UI，終止後不影響底層驅動服務；Windows 設定終止後可由使用者隨時重新開啟。
        /// </summary>
        public static void KillIgnoredBackgroundServices()
        {
            foreach (var name in _ignoredProcessNames)
            {
                var processes = Process.GetProcessesByName(name);
                foreach (var process in processes)
                    using (process)
                    {
                        try
                        {
                            DebugLogger.Log($"[FseService] Killing {process.ProcessName} PID={process.Id}");
                            process.Kill();
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Log($"[FseService] Kill {process.ProcessName} failed: {ex.Message}");
                        }
                    }
            }
        }

        /// <summary>
        /// 判斷前景視窗是否屬於已知的干擾應用程式，若是則忽略並繼續輪詢。
        /// 這些行程已由 KillIgnoredBackgroundServices() 在輪詢前主動終止，
        /// 此方法僅作為防禦性檢查，避免殘留行程干擾前景判定。
        /// </summary>
        public static bool IsIgnoredForegroundWindow(IntPtr hwnd)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                return _ignoredProcessNames.Contains(proc.ProcessName);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] IsIgnoredForegroundWindow failed: {ex.Message}");
                return false;
            }
        }
    }
}
