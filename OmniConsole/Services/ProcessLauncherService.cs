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
            DebugLogger.Log($"[ProcessLauncher] Launching platform: {platform.Id} ({platform.LaunchStrategies.Count} strategies defined)");

            for (int i = 0; i < platform.LaunchStrategies.Count; i++)
            {
                var strategy = platform.LaunchStrategies[i];
                DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1}/{platform.LaunchStrategies.Count}: Attempting {strategy.Type}...");

                if (await ExecuteStrategyAsync(strategy, platform.Id))
                {
                    DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1} ({strategy.Type}) succeeded.");
                    return true;
                }

                DebugLogger.Log($"[ProcessLauncher] Strategy {i + 1} ({strategy.Type}) failed.");
            }

            DebugLogger.Log($"[ProcessLauncher] {platform.Id}: All launch strategies failed.");
            return false;
        }

        /// <summary>
        /// 取得平台的在地化顯示名稱。
        /// 優先從 .resw 資源檔讀取，若失敗則回退到 Id。
        /// </summary>
        public static string GetPlatformDisplayName(PlatformDefinition platform)
        {
            // 使用者自訂平台的 DisplayNameKey 以 __user__ 開頭，直接從 UserPlatformStore 取名稱
            if (platform.DisplayNameKey.StartsWith("__user__"))
            {
                var entry = UserPlatformStore.FindEntryById(platform.Id);
                return entry?.DisplayName ?? platform.Id;
            }

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
                LaunchStrategyType.PackagedApp => IsPackagedAppInstalledAsync(s),
                LaunchStrategyType.Executable => Task.FromResult(IsExecutableAvailable(s)),
                _ => Task.FromResult(false),
            };
        }

        // ── 策略執行 ──────────────────────────────────────────────────────────

        private static Task<bool> ExecuteStrategyAsync(LaunchStrategy strategy, string platformId) =>
            strategy.Type switch
            {
                LaunchStrategyType.ProtocolUri => TryLaunchUriAsync(strategy.Uri!, platformId),
                LaunchStrategyType.Registry => Task.FromResult(TryLaunchFromRegistry(strategy, platformId)),
                LaunchStrategyType.PackagedApp => TryLaunchPackagedAppAsync(strategy, platformId),
                LaunchStrategyType.Executable => Task.FromResult(TryLaunchExecutable(strategy, platformId)),
                _ => Task.FromResult(false),
            };

        /// <summary>
        /// 透過 Protocol URI 啟動，啟動前先確認 URI handler 已登錄。
        /// </summary>
        private static async Task<bool> TryLaunchUriAsync(string uriString, string platformId)
        {
            try
            {
                DebugLogger.Log($"[ProcessLauncher]   Target URI: {uriString}");
                var uri = new Uri(uriString);

                // 確認 URI handler 已安裝，避免跳出「在 Store 中尋找應用程式」對話方塊
                var status = await Windows.System.Launcher.QueryUriSupportAsync(
                    uri, Windows.System.LaunchQuerySupportType.Uri);

                if (status != Windows.System.LaunchQuerySupportStatus.Available)
                {
                    DebugLogger.Log($"[ProcessLauncher]   URI not supported: {status}");
                    return false;
                }

                bool success = await Windows.System.Launcher.LaunchUriAsync(uri);
                if (success)
                    DebugLogger.Log($"[ProcessLauncher]   LaunchUriAsync call succeeded.");
                return success;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   URI launch exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 從登錄機碼讀取安裝路徑，直接啟動執行檔。
        /// ExecutableName 有值時視為目錄 + 檔名組合；為 null 時視登錄值本身為完整執行檔路徑。
        /// </summary>
        private static bool TryLaunchFromRegistry(LaunchStrategy strategy, string platformId)
        {
            try
            {
                DebugLogger.Log($"[ProcessLauncher]   Registry lookup: {strategy.RegistryRoot}\\{strategy.RegistrySubKey}\\{strategy.RegistryValueName}");
                string? registryValue = ReadRegistryValue(
                    strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!);

                if (string.IsNullOrEmpty(registryValue))
                {
                    DebugLogger.Log("[ProcessLauncher]   Registry value is empty or missing.");
                    return false;
                }

                string exePath;
                if (strategy.ParseCommandToDirectory)
                {
                    // 登錄值為命令字串（例如 URI handler 的 shell\open\command），
                    // 解析出執行檔路徑，取其目錄後與 ExecutableName 組合
                    string? parsedExe = ParseExePathFromCommand(registryValue);
                    string? dir = parsedExe is not null ? Path.GetDirectoryName(parsedExe) : null;
                    if (string.IsNullOrEmpty(dir))
                    {
                        DebugLogger.Log("[ProcessLauncher]   Failed to parse directory from command string.");
                        return false;
                    }
                    exePath = strategy.ExecutableName is not null
                        ? Path.Combine(dir, strategy.ExecutableName)
                        : dir;
                }
                else
                {
                    exePath = strategy.ExecutableName is not null
                        ? Path.Combine(registryValue, strategy.ExecutableName)
                        : registryValue;
                }

                DebugLogger.Log($"[ProcessLauncher]   Resolved registry path: {exePath}");
                return LaunchProcess(exePath, strategy.Arguments ?? "");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Registry launch exception: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 透過 PackageManager 找到已安裝的封裝應用程式並啟動。
        /// </summary>
        private static async Task<bool> TryLaunchPackagedAppAsync(LaunchStrategy strategy, string platformId)
        {
            try
            {
                string identifier = strategy.PackageFamilyName ?? strategy.PackageName ?? "Unknown";
                DebugLogger.Log($"[ProcessLauncher]   Target Packaged App: {identifier}");

                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = !string.IsNullOrEmpty(strategy.PackageFamilyName)
                    ? pm.FindPackagesForUser(string.Empty, strategy.PackageFamilyName)
                    : pm.FindPackagesForUser(string.Empty, strategy.PackageName!, strategy.Publisher!);

                var packageList = packages.ToList();
                if (!packageList.Any())
                {
                    DebugLogger.Log("[ProcessLauncher]   No matching package found for current user.");
                    return false;
                }

                foreach (var package in packageList)
                {
                    var entries = await package.GetAppListEntriesAsync();
                    if (entries.Count > 0 && await entries[0].LaunchAsync())
                    {
                        DebugLogger.Log($"[ProcessLauncher]   Successfully launched: {package.Id.FullName}");
                        return true;
                    }
                }
                DebugLogger.Log("[ProcessLauncher]   Found package but failed to launch any app entry.");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Packaged App launch exception: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// 直接啟動指定的執行檔。
        /// 支援絕對路徑；若僅指定檔名，會優先嘗試在定義的 SearchPaths 尋找，
        /// 若均未找到或未定義，則交由作業系統的 PATH 或 App Paths 機制尋找啟動。
        /// </summary>
        private static bool TryLaunchExecutable(LaunchStrategy strategy, string platformId)
        {
            try
            {
                if (string.IsNullOrEmpty(strategy.ExecutableName))
                {
                    DebugLogger.Log("[ProcessLauncher]   ExecutableName is empty.");
                    return false;
                }

                string exeName = Environment.ExpandEnvironmentVariables(strategy.ExecutableName);
                string launchPath = exeName;

                if (!Path.IsPathRooted(exeName) && strategy.SearchPaths != null)
                {
                    DebugLogger.Log($"[ProcessLauncher]   Searching for {exeName} in {strategy.SearchPaths.Length} paths...");
                    foreach (string dir in strategy.SearchPaths)
                    {
                        string expandedDir = Environment.ExpandEnvironmentVariables(dir);
                        string fullPath = Path.Combine(expandedDir, exeName);
                        if (File.Exists(fullPath))
                        {
                            launchPath = fullPath;
                            DebugLogger.Log($"[ProcessLauncher]   Found at: {launchPath}");
                            break;
                        }
                    }
                }

                // 若為絕對路徑，則可以事前檢查檔案是否存在
                if (Path.IsPathRooted(launchPath) && !File.Exists(launchPath))
                {
                    DebugLogger.Log($"[ProcessLauncher]   Executable not found at rooted path: {launchPath}");
                    return false;
                }

                return LaunchProcess(launchPath, strategy.Arguments ?? "");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Executable launch exception: {ex.Message}");
                return false;
            }
        }

        // ── 可用性查詢輔助方法 ────────────────────────────────────────────────

        /// <summary>
        /// 查詢系統是否有已登錄的 URI handler 可處理指定 URI scheme，
        /// 不實際啟動應用程式。
        /// </summary>
        private static async Task<bool> IsUriSupportedAsync(string uriString)
        {
            try
            {
                var uri = new Uri(uriString);
                var status = await Windows.System.Launcher.QueryUriSupportAsync(
                    uri, Windows.System.LaunchQuerySupportType.Uri);

                if (status != Windows.System.LaunchQuerySupportStatus.Available)
                    return false;

                // 若作業系統回報 Available，進一步檢查登錄檔中的執行檔是否存在，避免解除安裝後殘留的假象
                return IsUriHandlerExecutableValid(uri.Scheme);
            }
            catch { return false; }
        }

        /// <summary>
        /// 嘗試檢查 URI Scheme 在登錄檔中登錄的執行檔是否真實存在。
        /// 針對傳統桌面應用程式會解析絕對路徑並檢查檔案；若為 UWP 應用程式或無法解析絕對路徑時，預設回傳 true 交由系統處理。
        /// </summary>
        private static bool IsUriHandlerExecutableValid(string scheme)
        {
            try
            {
                // 檢查 HKCU 與 HKLM 的 Software\Classes\{scheme}\shell\open\command
                string? command = ReadRegistryValue("HKCU", $@"Software\Classes\{scheme}\shell\open\command", "")
                               ?? ReadRegistryValue("HKLM", $@"Software\Classes\{scheme}\shell\open\command", "");

                if (string.IsNullOrEmpty(command))
                {
                    // 有些可能是 UWP 應用程式，沒有傳統的 command 機碼，直接假設為 true 交給系統處理
                    return true;
                }

                string? exePath = ParseExePathFromCommand(command);
                if (string.IsNullOrEmpty(exePath))
                    return true;

                exePath = Environment.ExpandEnvironmentVariables(exePath);

                // 如果能解析出絕對路徑，則真實檢查檔案是否存在
                if (Path.IsPathRooted(exePath))
                {
                    bool exists = File.Exists(exePath);
                    if (!exists)
                    {
                        DebugLogger.Log($"[ProcessLauncher] Ghost URI Handler '{scheme}://' detected! Executable missing: {exePath}");
                    }
                    else
                    {
                        DebugLogger.Log($"[ProcessLauncher] Valid URI Handler '{scheme}://' confirmed at: {exePath}");
                    }
                    return exists;
                }

                // 如果無法解析為絕對路徑，保險起見當作存在
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher] URI handler validation failed for '{scheme}://': {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// 檢查登錄機碼對應的字串值是否存在且非空，
        /// 用以判斷平台是否已安裝（Registry 策略）。
        /// </summary>
        private static bool IsRegistryPathPresent(LaunchStrategy strategy) =>
            !string.IsNullOrEmpty(ReadRegistryValue(
                strategy.RegistryRoot!, strategy.RegistrySubKey!, strategy.RegistryValueName!));

        /// <summary>
        /// 以 PackageManager 搜尋封裝應用程式，判斷是否已為目前使用者安裝。
        /// 優先使用 PackageFamilyName（雙參數多載），否則使用 PackageName + Publisher（三參數多載）。
        /// </summary>
        private static Task<bool> IsPackagedAppInstalledAsync(LaunchStrategy strategy)
        {
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                var packages = !string.IsNullOrEmpty(strategy.PackageFamilyName)
                    ? pm.FindPackagesForUser(string.Empty, strategy.PackageFamilyName)
                    : pm.FindPackagesForUser(string.Empty, strategy.PackageName!, strategy.Publisher!);
                return Task.FromResult(packages.Any());
            }
            catch { return Task.FromResult(false); }
        }

        /// <summary>
        /// 檢查執行檔策略的目標是否存在。
        /// 絕對路徑直接檢查檔案；相對路徑或純檔名則嘗試 SearchPaths 後視為可用。
        /// </summary>
        private static bool IsExecutableAvailable(LaunchStrategy strategy)
        {
            if (string.IsNullOrEmpty(strategy.ExecutableName)) return false;

            string exePath = Environment.ExpandEnvironmentVariables(strategy.ExecutableName);

            if (Path.IsPathRooted(exePath))
                return File.Exists(exePath);

            // 非絕對路徑：嘗試 SearchPaths
            if (strategy.SearchPaths != null)
            {
                foreach (string dir in strategy.SearchPaths)
                {
                    string expandedDir = Environment.ExpandEnvironmentVariables(dir);
                    if (File.Exists(Path.Combine(expandedDir, exePath)))
                        return true;
                }
            }

            // 非絕對路徑且無 SearchPaths 或未找到：無法驗證，視為不可用
            return false;
        }

        // ── 通用輔助方法 ──────────────────────────────────────────────────────

        /// <summary>
        /// 從命令字串（例如 URI handler 的 shell\open\command 值）解析出執行檔路徑。
        /// 支援帶引號（"C:\...\app.exe" "%1"）與不帶引號（C:\...\app.exe %1）兩種格式。
        /// </summary>
        private static string? ParseExePathFromCommand(string command)
        {
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int endQuote = command.IndexOf("\"", 1);
                if (endQuote > 1)
                    return command.Substring(1, endQuote - 1);
            }
            else
            {
                int spaceIndex = command.IndexOf(" ");
                return spaceIndex > 0 ? command.Substring(0, spaceIndex) : command;
            }
            return null;
        }

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
                DebugLogger.Log($"[ProcessLauncher] Registry read failed ({registryRoot}\\{subKeyPath}): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 通用的絕對路徑程式啟動輔助方法。
        /// 呼叫前會嚴格檢查（File.Exists）檔案是否存在，因此不支援依賴系統 PATH 或 App Paths 的純檔名啟動。
        /// </summary>
        public static bool LaunchProcess(string filePath, string arguments = "")
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    DebugLogger.Log($"[ProcessLauncher]   LaunchProcess: Executable not found: {filePath}");
                    return false;
                }

                DebugLogger.Log($"[ProcessLauncher]   Process launched: {filePath} {arguments}");
                var startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = arguments,
                    UseShellExecute = true,
                };
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[ProcessLauncher]   Process launch failed: {ex.Message}");
                return false;
            }
        }
    }
}
