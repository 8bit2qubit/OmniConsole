using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Management.Deployment;
#if DEBUG
using Windows.Storage;
#endif

namespace OmniConsole.Services
{
    /// <summary>
    /// 透過 GitHub Releases API 檢查是否有新版本可用，並提供下載功能。
    /// 檢查結果快取於 SettingsService，供 InfoBar 讀取。
    /// </summary>
    public static class UpdateCheckService
    {
        private const string GitHubApiUrl = "https://api.github.com/repos/8bit2qubit/OmniConsole/releases/latest";
        private const string ReleasePageUrl = "https://github.com/8bit2qubit/OmniConsole/releases/latest";

        public const string PhantomLinkFamilyName = "4fa8e044-7ffa-4059-b034-e4111881d96e_n7gpkx2kypjte";

        public static string ReleaseNotesUrl => ReleasePageUrl;

        public enum UpdateKind
        {
            None,
            MissingPhantomLink,
            MainAppUpdate
        }

        /// <summary>
        /// 呼叫 GitHub API（或 DEBUG mock）檢查最新版本與 PhantomLink 安裝狀態，決定 UpdateKind 並快取結果。
        /// </summary>
        public static async Task<(UpdateKind Kind, string LatestVersion)> CheckForUpdateAsync()
        {
            try
            {
                string json;
#if DEBUG
                var mockPath = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path,
                    "MockRelease.json");
                if (File.Exists(mockPath))
                    json = await File.ReadAllTextAsync(mockPath);
                else
                    json = await FetchGitHubReleaseJsonAsync();
#else
                json = await FetchGitHubReleaseJsonAsync();
#endif
                var tagName = ParseTagName(json);
                if (string.IsNullOrEmpty(tagName))
                    return (UpdateKind.None, "");

                var versionStr = tagName.TrimStart('v');
                if (!Version.TryParse(versionStr, out var latest))
                    return (UpdateKind.None, "");

                var currentStr = SettingsService.GetAppVersion();
                if (!Version.TryParse(currentStr, out var current))
                    return (UpdateKind.None, "");

                var (mainUrl, phantomLinkUrl) = ParseBundleDownloadUrls(json);

                bool phantomLinkUpToDate = IsPhantomLinkUpToDate(latest);
                bool hasNewVersion = latest > current;

                UpdateKind kind;
                if (hasNewVersion)
                {
                    // 主程式有新版 → MainAppUpdate（InstallBundleAsync 會連帶更新 PhantomLink）
                    kind = UpdateKind.MainAppUpdate;
                    SettingsService.SetCachedNewVersion(versionStr);
                    if (!string.IsNullOrEmpty(mainUrl))
                        SettingsService.SetCachedDownloadUrl(mainUrl);
                    if (!string.IsNullOrEmpty(phantomLinkUrl))
                        SettingsService.SetCachedPhantomLinkUrl(phantomLinkUrl);
                }
                else if (!phantomLinkUpToDate)
                {
                    // 同版本但 PhantomLink 缺件或版本過時
                    kind = UpdateKind.MissingPhantomLink;
                    SettingsService.SetCachedNewVersion(versionStr);
                    if (!string.IsNullOrEmpty(mainUrl))
                        SettingsService.SetCachedDownloadUrl(mainUrl);
                    if (!string.IsNullOrEmpty(phantomLinkUrl))
                        SettingsService.SetCachedPhantomLinkUrl(phantomLinkUrl);
                }
                else
                {
                    kind = UpdateKind.None;
                    SettingsService.ClearCachedNewVersion();
                    SettingsService.ClearCachedDownloadUrl();
                    SettingsService.ClearCachedPhantomLinkUrl();
                }

                SettingsService.SetCachedUpdateKind(kind.ToString());
                return (kind, versionStr);
            }
            catch
            {
                return (UpdateKind.None, "");
            }
        }

        /// <summary>
        /// 判斷是否應執行自動檢查（開關啟用 + 日期已跨日）。
        /// </summary>
        public static bool ShouldAutoCheck()
        {
            if (!SettingsService.GetAutoUpdateCheckEnabled())
                return false;

            var lastDate = SettingsService.GetLastUpdateCheckDate();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            return lastDate != today;
        }

        /// <summary>
        /// 記錄今日已執行檢查。
        /// </summary>
        public static void RecordCheckDate()
        {
            SettingsService.SetLastUpdateCheckDate(DateTime.Now.ToString("yyyy-MM-dd"));
        }

        /// <summary>
        /// 應用程式啟動時呼叫：若快取的新版本號不再大於目前版本，清除快取。
        /// 修正 MSIX 更新後 LocalSettings 保留導致 InfoBar 誤顯示「有新版可下載」的問題。
        /// </summary>
        public static void InvalidateCacheIfCurrentVersion()
        {
            var cached = SettingsService.GetCachedNewVersion();
            if (string.IsNullOrEmpty(cached)) return;

            var currentStr = SettingsService.GetAppVersion();
            if (Version.TryParse(cached, out var cachedVer) &&
                Version.TryParse(currentStr, out var currentVer) &&
                cachedVer <= currentVer)
            {
                if (IsPhantomLinkUpToDate(cachedVer))
                {
                    SettingsService.ClearCachedNewVersion();
                    SettingsService.ClearCachedDownloadUrl();
                    SettingsService.ClearCachedPhantomLinkUrl();
                    SettingsService.SetCachedUpdateKind(UpdateKind.None.ToString());
                }
                else
                {
                    SettingsService.SetCachedUpdateKind(UpdateKind.MissingPhantomLink.ToString());
                }
            }
        }

        /// <summary>
        /// 讀取待續更新狀態。若目前版本與 PhantomLink 版本都已達待續目標版本，清掉旗標並回傳空值。
        /// </summary>
        public static (string Phase, string PhantomLinkUrl, string MainUrl, string TargetVersion) GetPendingUpdateState()
        {
            var phase = SettingsService.GetPendingUpdatePhase();
            DebugLogger.Log($"[PendingUpdate] phase='{phase}'");
            if (string.IsNullOrEmpty(phase)) return ("", "", "", "");

            var target = SettingsService.GetPendingUpdateTargetVersion();
            var currentStr = SettingsService.GetAppVersion();
            bool plOk = false;
            bool versionReached = Version.TryParse(target, out var targetVer) &&
                                  Version.TryParse(currentStr, out var currentVer) &&
                                  currentVer >= targetVer;
            if (versionReached)
                plOk = IsPhantomLinkUpToDate(targetVer!);
            DebugLogger.Log($"[PendingUpdate] target={target}, current={currentStr}, versionReached={versionReached}, phantomLinkOk={plOk}");

            // 主程式與 PhantomLink 版本均已達目標：視為更新完成
            if (versionReached && plOk)
            {
                DebugLogger.Log("[PendingUpdate] both reached target → clearing pending");
                SettingsService.ClearPendingUpdate();
                return ("", "", "", "");
            }

            DebugLogger.Log($"[PendingUpdate] resuming with phase={phase}");
            return (phase,
                    SettingsService.GetPendingUpdatePhantomLinkUrl(),
                    SettingsService.GetPendingUpdateMainUrl(),
                    target);
        }

        /// <summary>
        /// 從指定 URL 下載 .msix 至本機路徑，透過 IProgress 回報進度百分比（0~100）。
        /// ContentLength 不可用時回報 -1（indeterminate）。
        /// </summary>
        public static async Task DownloadMsixAsync(
            string downloadUrl,
            string destinationPath,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniConsole-UpdateCheck");

            using var response = await client.GetAsync(
                new Uri(downloadUrl),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            long bytesRead = 0;

            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, read, cancellationToken);
                bytesRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                    progress.Report((double)bytesRead / totalBytes.Value * 100);
                else
                    progress.Report(-1);
            }
        }

        /// <summary>
        /// 刪除指定目錄中所有 OmniConsole_*.msix 檔案。
        /// </summary>
        public static void CleanUpOldMsixFiles(string directory)
        {
            try
            {
                foreach (var file in Directory.GetFiles(directory, "OmniConsole_*.msix")
                    .Concat(Directory.GetFiles(directory, "OmniConsole.PhantomLink_*-widget.msix")))
                {
                    File.Delete(file);
                    DebugLogger.Log($"[UpdateCheck] Deleted old MSIX: {Path.GetFileName(file)}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[UpdateCheck] Cleanup failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 檢查系統是否已啟用開發人員模式。
        /// OmniConsole 的 MSIX 安裝需要開發人員模式（因 SCCD CustomCapability），
        /// 未啟用時下載的 .msix 將無法安裝。
        /// </summary>
        public static bool IsDeveloperModeEnabled()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock");
                var value = key?.GetValue("AllowDevelopmentWithoutDevLicense");
                return value is int intVal && intVal != 0;
            }
            catch { return false; }
        }

        /// <summary>從 GitHub Releases API 取得最新版本的 JSON。</summary>
        private static async Task<string> FetchGitHubReleaseJsonAsync()
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OmniConsole-UpdateCheck");
            client.Timeout = TimeSpan.FromSeconds(10);
            return await client.GetStringAsync(GitHubApiUrl);
        }

        /// <summary>從 JSON 解析 tag_name 欄位（例如 "v1.3.0.0"）。</summary>
        private static string ParseTagName(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tagElement))
                return tagElement.GetString() ?? "";
            return "";
        }

        /// <summary>
        /// 從 GitHub Release JSON 的 assets 陣列中同時取出主程式 OmniConsole 與 PhantomLink 的 .msix 下載連結。
        /// </summary>
        private static (string mainUrl, string phantomLinkUrl) ParseBundleDownloadUrls(string json)
        {
            string mainUrl = "";
            string phantomLinkUrl = "";

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return ("", "");

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement))
                    continue;
                var name = nameElement.GetString() ?? "";
                if (!asset.TryGetProperty("browser_download_url", out var urlElement))
                    continue;
                var url = urlElement.GetString() ?? "";

                if (name.EndsWith("_x64-widget.msix", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("OmniConsole.PhantomLink_", StringComparison.OrdinalIgnoreCase))
                    phantomLinkUrl = url;
                else if (name.EndsWith("_x64.msix", StringComparison.OrdinalIgnoreCase)
                    && name.StartsWith("OmniConsole_", StringComparison.OrdinalIgnoreCase))
                    mainUrl = url;
            }
            return (mainUrl, phantomLinkUrl);
        }

        /// <summary>
        /// 判斷已安裝的 PhantomLink 版本是否 >= 指定版本。
        /// 未安裝時回傳 false。
        /// </summary>
        private static bool IsPhantomLinkUpToDate(Version targetVersion)
        {
            try
            {
                var pm = new PackageManager();
                var pkg = pm.FindPackagesForUser("", PhantomLinkFamilyName).FirstOrDefault();
                if (pkg == null) return false;
                var v = pkg.Id.Version;
                var installed = new Version(v.Major, v.Minor, v.Build, v.Revision);
                return installed >= targetVersion;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 下載並安裝 PhantomLink + OmniConsole 的統一入口。
        /// 先終止 PhantomLink 行程，再以 ForceApplicationShutdown 安裝 PhantomLink，
        /// 接著安裝 OmniConsole（ForceApplicationShutdown 會終止主程式）。
        /// mainSkippable 為 true 時跳過 OmniConsole 重裝，改用 RequestRestartAsync 重啟。
        /// resumeFromPhase2 為 true 時跳過 Phase 1（拔電/強制重開後續做用）。
        /// 各階段會寫入 SettingsService.SetPendingUpdatePhase 以利中斷後續做。
        /// status 回呼回報當前階段文字（下載/安裝），供 UI 顯示。
        /// </summary>
        public static async Task InstallBundleAsync(
            string phantomLinkUrl,
            string mainUrl,
            string targetVersion,
            bool mainSkippable,
            bool resumeFromPhase2,
            IProgress<double> progress,
            IProgress<string> status,
            CancellationToken ct)
        {
            var localFolder = Windows.Storage.ApplicationData.Current.LocalFolder.Path;

            DebugLogger.Log($"[InstallBundle] mainSkippable={mainSkippable}, resumeFromPhase2={resumeFromPhase2}, targetVersion={targetVersion}");

            CleanUpOldMsixFiles(localFolder);

            var pm = new PackageManager();

            // 寫入完整待續資訊（兩個 URL + 目標版本 + 階段），於 Phase 1 開始前完成
            SettingsService.SetPendingUpdatePhantomLinkUrl(phantomLinkUrl);
            SettingsService.SetPendingUpdateMainUrl(mainUrl);
            SettingsService.SetPendingUpdateTargetVersion(targetVersion);
            SettingsService.SetPendingUpdatePhase(resumeFromPhase2 ? "Phase2" : "Phase1");

            // PhantomBridge 為 PhantomLink 啟動的 Full Trust COM Server，正常情況下會在所有
            // COM interface 釋放後由 module_lock 歸零事件觸發退出；但 client 連線拆除時機
            // 不可控，顯式終止可確保 MSIX 安裝/解除安裝不被檔案鎖定卡住。
            foreach (var name in new[] { "OmniConsole.PhantomLink", "OmniConsole.PhantomBridge" })
            {
                foreach (var proc in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try
                    {
                        DebugLogger.Log($"[InstallBundle] Killing {name} PID={proc.Id}");
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }

            // ── Phase 1: PhantomLink ────────────────────────────────────────────
            if (!resumeFromPhase2 && !string.IsNullOrEmpty(phantomLinkUrl))
            {
                var plFileName = $"OmniConsole.PhantomLink_{targetVersion}_x64-widget.msix";
                var plPath = Path.Combine(localFolder, plFileName);

                DebugLogger.Log($"[InstallBundle] Phase 1: downloading PhantomLink to {plPath}");
                status.Report("Phase1Download");
                await DownloadMsixAsync(phantomLinkUrl, plPath, progress, ct);

                DebugLogger.Log("[InstallBundle] Phase 1: installing PhantomLink...");
                status.Report("Phase1Install");
                await pm.AddPackageAsync(
                    new Uri(plPath),
                    null,
                    DeploymentOptions.ForceApplicationShutdown);
                DebugLogger.Log("[InstallBundle] Phase 1: PhantomLink installed OK");
            }

            // Phase 1 已 commit（或 resume 跳過），旗標前進到 Phase2
            SettingsService.SetPendingUpdatePhase("Phase2");

            // ── Phase 2: OmniConsole ────────────────────────────────────────────
            if (mainSkippable)
            {
                DebugLogger.Log("[InstallBundle] Phase 2: mainSkippable=true, requesting restart...");
                SettingsService.SetPendingSettingsRestart(true);
                // 同版本快速重啟路徑：未實際重裝 OmniConsole，視為更新流程已結束，清旗標
                SettingsService.ClearPendingUpdate();

                // RequestRestartAsync 會送 graceful close 請求給本視窗（行為與 ForceApplicationShutdown
                // 相同）；先回報狀態給 UI 鬆開 AppWindow.Closing 鎖，再讓 50ms 給 UI 執行緒套用
                status.Report("Phase2RequestingRestart");
                await Task.Delay(50, CancellationToken.None);

                var result = await Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync("");
                DebugLogger.Log($"[InstallBundle] Phase 2: RequestRestartAsync returned {result}");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
                return;
            }

            if (!string.IsNullOrEmpty(mainUrl))
            {
                var mainFileName = $"OmniConsole_{targetVersion}_x64.msix";
                var mainPath = Path.Combine(localFolder, mainFileName);

                DebugLogger.Log($"[InstallBundle] Phase 2: downloading OmniConsole to {mainPath}");
                status.Report("Phase2Download");
                await DownloadMsixAsync(mainUrl, mainPath, progress, ct);

                DebugLogger.Log("[InstallBundle] Phase 2: installing OmniConsole (ForceApplicationShutdown)...");
                status.Report("Phase2Install");

                // Phase 2 安裝走 fire-and-forget + 主動結束：發出 deployment 請求後不 await，
                // 等 500ms 讓 OS staging 開始，再 Process.Kill 結束本行程，由 RegisterApplicationRestart
                // 接手重新啟動新版。實測中以 await + 等 OS 送 graceful close 的路徑會耗時約 30 秒
                _ = pm.AddPackageAsync(
                    new Uri(mainPath),
                    null,
                    DeploymentOptions.ForceApplicationShutdown);

                // 不傳 ct，這 500ms 刻意不可取消
                await Task.Delay(500, CancellationToken.None);

                DebugLogger.Log("[InstallBundle] Phase 2: self-terminating to let OS commit deployment");
                System.Diagnostics.Process.GetCurrentProcess().Kill();
            }
        }
    }
}
