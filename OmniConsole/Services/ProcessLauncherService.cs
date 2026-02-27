using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace OmniConsole.Services
{
    /// <summary>
    /// 封裝遊戲平台啟動邏輯的靜態服務。
    /// 支援透過 Protocol URI 或直接執行檔啟動。
    /// </summary>
    public static class ProcessLauncherService
    {
        private const string SteamBigPictureUri = "steam://open/bigpicture";
        private const string SteamRegistryKey = @"SOFTWARE\Valve\Steam";
        private const string SteamRegistryValue = "InstallPath";
        private const string SteamExeName = "steam.exe";

        /// <summary>
        /// 啟動 Steam Big Picture 模式。
        /// 優先使用 Protocol URI，若失敗則嘗試從 Registry 查找執行檔路徑。
        /// </summary>
        /// <returns>是否成功啟動。</returns>
        public static async Task<bool> LaunchSteamBigPictureAsync()
        {
            // 策略一：透過 Protocol URI 啟動
            try
            {
                var uri = new Uri(SteamBigPictureUri);
                bool success = await Windows.System.Launcher.LaunchUriAsync(uri);
                if (success)
                {
                    Debug.WriteLine("[ProcessLauncher] Steam launched via URI.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] URI launch failed: {ex.Message}");
            }

            // 策略二：從 Registry 查找 Steam 路徑，直接啟動
            string? steamPath = TryGetSteamInstallPath();
            if (!string.IsNullOrEmpty(steamPath))
            {
                string steamExePath = System.IO.Path.Combine(steamPath, SteamExeName);
                return LaunchProcess(steamExePath, "-bigpicture");
            }

            Debug.WriteLine("[ProcessLauncher] All launch strategies failed.");
            return false;
        }

        /// <summary>
        /// 從 Windows Registry 讀取 Steam 的安裝路徑。
        /// 依序嘗試 HKCU 和 HKLM（皆為讀取，不需要提權）。
        /// </summary>
        private static string? TryGetSteamInstallPath()
        {
            // 先嘗試 HKCU
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SteamRegistryKey);
                string? path = key?.GetValue(SteamRegistryValue) as string;
                if (!string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine($"[ProcessLauncher] Steam found via HKCU: {path}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] HKCU read failed: {ex.Message}");
            }

            // 再嘗試 HKLM
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(SteamRegistryKey);
                string? path = key?.GetValue(SteamRegistryValue) as string;
                if (!string.IsNullOrEmpty(path))
                {
                    Debug.WriteLine($"[ProcessLauncher] Steam found via HKLM: {path}");
                    return path;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] HKLM read failed: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 通用的程式啟動方法。可供未來擴展其他平台使用。
        /// </summary>
        /// <param name="filePath">執行檔路徑。</param>
        /// <param name="arguments">命令列參數。</param>
        /// <returns>是否成功啟動。</returns>
        public static bool LaunchProcess(string filePath, string arguments = "")
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = arguments,
                    UseShellExecute = true
                };
                Process.Start(startInfo);
                Debug.WriteLine($"[ProcessLauncher] Process launched: {filePath} {arguments}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] Process launch failed: {ex.Message}");
                return false;
            }
        }
    }
}
