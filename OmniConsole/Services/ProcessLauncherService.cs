using Microsoft.Win32;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OmniConsole.Services
{
    /// <summary>
    /// 泛用的平台啟動服務，依據 <see cref="PlatformDefinition"/> 中定義的策略清單依序嘗試啟動。
    /// 本服務不包含任何平台特定的硬編碼邏輯，所有平台參數均來自 <see cref="PlatformCatalog"/>。
    /// </summary>
    public static class ProcessLauncherService
    {
        private static readonly ResourceLoader _resourceLoader = new();

        /// <summary>
        /// 依序嘗試平台定義中的啟動策略，第一個成功即停止。
        /// </summary>
        public static async Task<bool> LaunchPlatformAsync(PlatformDefinition platform)
        {
            foreach (var strategy in platform.LaunchStrategies)
            {
                if (await ExecuteStrategyAsync(strategy, platform.Id))
                    return true;
            }

            Debug.WriteLine($"[ProcessLauncher] {platform.Id}: all launch strategies failed.");
            return false;
        }

        /// <summary>
        /// 取得平台的在地化顯示名稱。
        /// 優先從 .resw 資源檔讀取，若失敗則回退到 Id。
        /// </summary>
        public static string GetPlatformDisplayName(PlatformDefinition platform)
        {
            try
            {
                string? name = _resourceLoader.GetString(platform.DisplayNameKey);
                return !string.IsNullOrEmpty(name) ? name : platform.Id;
            }
            catch
            {
                return platform.Id;
            }
        }

        /// <summary>
        /// 檢查指定平台是否已安裝（不觸發啟動）。
        /// </summary>
        public static Task<bool> CheckPlatformAvailableAsync(PlatformDefinition platform)
        {
            var s = platform.AvailabilityStrategy;
            return s.Type switch
            {
                LaunchStrategyType.ProtocolUri => IsUriSupportedAsync(s.Uri!),
                LaunchStrategyType.Registry => Task.FromResult(IsRegistryPathPresent(s)),
                LaunchStrategyType.MsixPackage => IsMsixPackageInstalledAsync(s.PackageName!, s.Publisher!),
                _ => Task.FromResult(false),
            };
        }

        // ── 策略執行 ──────────────────────────────────────────────────────────

        private static Task<bool> ExecuteStrategyAsync(LaunchStrategy strategy, string platformId) =>
            strategy.Type switch
            {
                LaunchStrategyType.ProtocolUri => TryLaunchUriAsync(strategy.Uri!, platformId),
                LaunchStrategyType.Registry => Task.FromResult(TryLaunchFromRegistry(strategy, platformId)),
                LaunchStrategyType.MsixPackage => TryLaunchMsixAsync(strategy, platformId),
                _ => Task.FromResult(false),
            };

        /// <summary>
        /// 透過 Protocol URI 啟動，啟動前先確認 URI handler 已註冊。
        /// </summary>
        private static async Task<bool> TryLaunchUriAsync(string uriString, string platformId)
        {
            try
            {
                var uri = new Uri(uriString);

                // 確認 URI handler 已安裝，避免跳出「在 Store 中尋找應用程式」對話方塊
                var status = await Windows.System.Launcher.QueryUriSupportAsync(
                    uri, Windows.System.LaunchQuerySupportType.Uri);

                if (status != Windows.System.LaunchQuerySupportStatus.Available)
                {
                    Debug.WriteLine($"[ProcessLauncher] {platformId} URI not supported: {status}");
                    return false;
                }

                bool success = await Windows.System.Launcher.LaunchUriAsync(uri);
                if (success)
                    Debug.WriteLine($"[ProcessLauncher] {platformId} launched via URI.");
                return success;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] {platformId} URI launch failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 從登錄機碼讀取安裝路徑，直接啟動執行檔。
        /// </summary>
        private static bool TryLaunchFromRegistry(LaunchStrategy strategy, string platformId)
        {
            try
            {
                string? installPath = ReadRegistryValue(
                    strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!);

                if (string.IsNullOrEmpty(installPath)) return false;

                string exePath = Path.Combine(installPath, strategy.ExecutableName!);
                return LaunchProcess(exePath, strategy.Arguments ?? "");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] {platformId} Registry launch failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 透過 PackageManager 找到已安裝的 MSIX 套件並啟動。
        /// </summary>
        private static async Task<bool> TryLaunchMsixAsync(LaunchStrategy strategy, string platformId)
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = pm.FindPackagesForUser(
                    string.Empty, strategy.PackageName!, strategy.Publisher!);

                foreach (var package in packages)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    if (entries.Count > 0 && await entries[0].LaunchAsync())
                    {
                        Debug.WriteLine($"[ProcessLauncher] {platformId} launched via PackageManager.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] {platformId} MSIX launch failed: {ex.Message}");
            }
            return false;
        }

        // ── 可用性查詢輔助方法 ────────────────────────────────────────────────

        /// <summary>
        /// 查詢系統是否有已註冊的 URI handler 可處理指定 URI scheme，
        /// 不實際啟動應用程式。
        /// </summary>
        private static async Task<bool> IsUriSupportedAsync(string uriString)
        {
            try
            {
                var uri = new Uri(uriString);
                var status = await Windows.System.Launcher.QueryUriSupportAsync(
                    uri, Windows.System.LaunchQuerySupportType.Uri);
                return status == Windows.System.LaunchQuerySupportStatus.Available;
            }
            catch { return false; }
        }

        /// <summary>
        /// 檢查登錄機碼對應的字串值是否存在且非空，
        /// 用以判斷平台是否已安裝（Registry 策略）。
        /// </summary>
        private static bool IsRegistryPathPresent(LaunchStrategy strategy) =>
            !string.IsNullOrEmpty(ReadRegistryValue(
                strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!));

        /// <summary>
        /// 以 PackageManager 搜尋指定 packageName / publisher 的 MSIX 套件，
        /// 判斷是否已為目前使用者安裝。
        /// </summary>
        private static Task<bool> IsMsixPackageInstalledAsync(string packageName, string publisher)
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = pm.FindPackagesForUser(string.Empty, packageName, publisher);
                return Task.FromResult(packages.Any());
            }
            catch { return Task.FromResult(false); }
        }

        // ── 通用輔助方法 ──────────────────────────────────────────────────────

        /// <summary>
        /// 從 Windows 登錄機碼讀取字串值。
        /// </summary>
        private static string? ReadRegistryValue(string registryRoot, string subKeyPath, string valueName)
        {
            try
            {
                var rootKey = registryRoot.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                    ? Registry.CurrentUser
                    : Registry.LocalMachine;

                using var key = rootKey.OpenSubKey(subKeyPath);
                return key?.GetValue(valueName) as string;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ProcessLauncher] Registry read failed ({registryRoot}\\{subKeyPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通用的程式啟動方法。
        /// </summary>
        public static bool LaunchProcess(string filePath, string arguments = "")
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Debug.WriteLine($"[ProcessLauncher] Executable not found: {filePath}");
                    return false;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = arguments,
                    UseShellExecute = true,
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
