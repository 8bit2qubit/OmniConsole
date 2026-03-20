using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

// keybd_event 虛擬按鍵碼
file static class VK
{
    public const byte LWIN = 0x5B;
    public const byte F11 = 0x7A;
}

file static class KEYEVENTF
{
    public const uint KEYUP = 0x0002;
}

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
        private static extern int SetGamingFullScreenExperience();

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

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
        };

        /// <summary>
        /// 回傳目前是否處於 FSE 模式（由 Windows FSE 機制啟動）。
        /// </summary>
        public static bool IsActive()
        {
            try
            {
                bool result = IsGamingFullScreenExperienceActive();
                DebugLogger.Log($"[FseService] IsActive = {result}");
                return result;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] IsActive failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 回傳目前是否可以觸發 FSE（例如系統不支援時為 false）。
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
        /// 模擬 Win+F11，從 FSE 模式觸發「切換回桌面」確認對話方塊。
        /// </summary>
        public static void TryExitToDesktop()
        {
            try
            {
                keybd_event(VK.LWIN, 0, 0, 0);
                keybd_event(VK.F11, 0, 0, 0);
                keybd_event(VK.F11, 0, KEYEVENTF.KEYUP, 0);
                keybd_event(VK.LWIN, 0, KEYEVENTF.KEYUP, 0);
                DebugLogger.Log("[FseService] TryExitToDesktop: Win+F11 sent");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] TryExitToDesktop failed: {ex.Message}");
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
                int hr = SetGamingFullScreenExperience();
                DebugLogger.Log($"[FseService] SetGamingFullScreenExperience HRESULT: 0x{hr:X8}");
                return hr >= 0;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[FseService] TryActivate failed: {ex.Message}");
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
        /// 回傳 GameBar.exe 是否正在執行。
        /// </summary>
        public static bool IsGameBarRunning()
            => Process.GetProcessesByName("GameBar").Length > 0;

        /// <summary>
        /// 透過 ms-gamebar:// URI 啟動 GameBar，輪詢直到 GameBarFTServer.exe 出現或逾時。
        /// GameBarFTServer 是 GameBar 的服務端元件，出現時代表 GameBar 已完成初始化。
        /// </summary>
        /// <param name="timeoutMs">最長等待毫秒數，預設 5000ms。</param>
        public static async System.Threading.Tasks.Task EnsureGameBarRunningAsync(int timeoutMs = 5000)
        {
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
        /// 主動終止所有已知干擾應用程式的行程。
        /// 這些 App 僅是前端 UI，終止後不影響底層音訊驅動服務。
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
