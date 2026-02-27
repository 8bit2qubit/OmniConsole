using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace OmniConsole.Services
{
    /// <summary>
    /// 封裝遊戲平台啟動邏輯的靜態服務。
    /// 支援透過 Protocol URI 或直接執行檔啟動。
    /// </summary>
    public static class ProcessLauncherService
    {
        // Steam
        private const string SteamBigPictureUri = "steam://open/bigpicture";
        private const string SteamRegistryKey = @"SOFTWARE\Valve\Steam";
        private const string SteamRegistryValue = "InstallPath";
        private const string SteamExeName = "steam.exe";

        // Xbox
        private const string XboxUri = "xbox://";

        // Epic Games
        private const string EpicUri = "com.epicgames.launcher://";
        private const string EpicRegistryKey = @"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine";
        private const string EpicLauncherRegistryKey = @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher";

        private static readonly ResourceLoader _resourceLoader = new();

        /// <summary>
        /// 依據指定平台啟動對應的遊戲應用程式。
        /// </summary>
        public static async Task<bool> LaunchPlatformAsync(GamePlatform platform)
        {
            return platform switch
            {
                GamePlatform.SteamBigPicture => await LaunchSteamBigPictureAsync(),
                GamePlatform.XboxApp => await LaunchXboxAppAsync(),
                GamePlatform.EpicGames => await LaunchEpicGamesAsync(),
                _ => false
            };
        }

        /// <summary>
        /// 取得平台的在地化顯示名稱。
        /// 優先從 .resw 資源檔讀取，若失敗則回退到列舉名稱。
        /// </summary>
        public static string GetPlatformDisplayName(GamePlatform platform)
        {
            string key = platform switch
            {
                GamePlatform.SteamBigPicture => "Platform_SteamBigPicture",
                GamePlatform.XboxApp => "Platform_XboxApp",
                GamePlatform.EpicGames => "Platform_EpicGames",
                _ => platform.ToString()
            };

            try
            {
                string? name = _resourceLoader.GetString(key);
                return !string.IsNullOrEmpty(name) ? name : platform.ToString();
            }
            catch
            {
                return platform.ToString();
            }
        }

        /// <summary>
        /// 啟動 Steam Big Picture 模式。
        /// 優先使用 Protocol URI，若失敗則嘗試從 Registry 查找執行檔路徑。
        /// </summary>
        private static async Task<bool> LaunchSteamBigPictureAsync()
        {
            // 策略一：透過 Protocol URI 啟動
            if (await TryLaunchUriAsync(SteamBigPictureUri, "Steam"))
                return true;

            // 策略二：從 Registry 查找 Steam 路徑，直接啟動
            string? steamPath = TryGetRegistryValue(Registry.CurrentUser, SteamRegistryKey, SteamRegistryValue)
                             ?? TryGetRegistryValue(Registry.LocalMachine, SteamRegistryKey, SteamRegistryValue);

            if (!string.IsNullOrEmpty(steamPath))
            {
                string steamExePath = System.IO.Path.Combine(steamPath, SteamExeName);
                return LaunchProcess(steamExePath, "-bigpicture");
            }

            Debug.WriteLine("[ProcessLauncher] Steam: all launch strategies failed.");
            return false;
        }

        /// <summary>
        /// 啟動 Xbox App。
        /// </summary>
        private static async Task<bool> LaunchXboxAppAsync()
        {
            return await TryLaunchUriAsync(XboxUri, "Xbox");
        }

        /// <summary>
        /// 啟動 Epic Games Launcher。
        /// </summary>
        private static async Task<bool> LaunchEpicGamesAsync()
        {
            return await TryLaunchUriAsync(EpicUri, "Epic Games");
        }

        /// <summary>
        /// 嘗試透過 Protocol URI 啟動應用程式。
        /// </summary>
        private static async Task<bool> TryLaunchUriAsync(string uriString, string platformName)
        {
            try
            {
                var uri = new Uri(uriString);
                bool success = await Windows.System.Launcher.LaunchUriAsync(uri);
                if (success)
                {
                    Debug.WriteLine($"[ProcessLauncher] {platformName} launched via URI.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] {platformName} URI launch failed: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 從 Registry 讀取指定的值（讀取不需要提權）。
        /// </summary>
        private static string? TryGetRegistryValue(RegistryKey rootKey, string subKeyPath, string valueName)
        {
            try
            {
                using var key = rootKey.OpenSubKey(subKeyPath);
                return key?.GetValue(valueName) as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] Registry read failed ({rootKey.Name}\\{subKeyPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通用的程式啟動方法。
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
