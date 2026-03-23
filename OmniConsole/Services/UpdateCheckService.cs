using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

        public static string ReleaseNotesUrl => ReleasePageUrl;

        public enum CheckResult
        {
            NewVersionAvailable,
            UpToDate,
            Failed
        }

        /// <summary>
        /// 呼叫 GitHub API（或 DEBUG mock）檢查最新版本，比較後快取結果。
        /// </summary>
        public static async Task<(CheckResult Result, string LatestVersion)> CheckForUpdateAsync()
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
                    return (CheckResult.Failed, "");

                var versionStr = tagName.TrimStart('v');
                if (!Version.TryParse(versionStr, out var latest))
                    return (CheckResult.Failed, "");

                var currentStr = SettingsService.GetAppVersion();
                if (!Version.TryParse(currentStr, out var current))
                    return (CheckResult.Failed, "");

                if (latest > current)
                {
                    SettingsService.SetCachedNewVersion(versionStr);
                    var msixUrl = ParseMsixDownloadUrl(json);
                    if (!string.IsNullOrEmpty(msixUrl))
                        SettingsService.SetCachedDownloadUrl(msixUrl);
                    return (CheckResult.NewVersionAvailable, versionStr);
                }
                else
                {
                    SettingsService.ClearCachedNewVersion();
                    SettingsService.ClearCachedDownloadUrl();
                    return (CheckResult.UpToDate, versionStr);
                }
            }
            catch
            {
                return (CheckResult.Failed, "");
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
                SettingsService.ClearCachedNewVersion();
                SettingsService.ClearCachedDownloadUrl();
            }
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
                foreach (var file in Directory.GetFiles(directory, "OmniConsole_*.msix"))
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
        /// 從 GitHub Release JSON 的 assets 陣列中找出 .msix 的下載連結。
        /// </summary>
        private static string ParseMsixDownloadUrl(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("assets", out var assets))
                return "";

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameElement))
                {
                    var name = nameElement.GetString() ?? "";
                    if (name.EndsWith("_x64.msix", StringComparison.OrdinalIgnoreCase) &&
                        asset.TryGetProperty("browser_download_url", out var urlElement))
                    {
                        return urlElement.GetString() ?? "";
                    }
                }
            }
            return "";
        }
    }
}
