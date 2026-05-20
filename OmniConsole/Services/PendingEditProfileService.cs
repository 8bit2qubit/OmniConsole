using OmniConsole.Models;
using System;

namespace OmniConsole.Services
{
    /// <summary>
    /// 跨 process 邊界傳遞「待編輯手把 profile」請求的暫存層。
    /// Program.cs 收到 omniconsole://edit-gamepad-profile 時 Stash 進 LocalSettings；
    /// SettingsPage 進手把映射分頁時 TryConsume 取出並清除。
    /// </summary>
    public static class PendingEditProfileService
    {
        /// <summary>Protocol query string 字元數上限，超過直接放棄解析。</summary>
        private const int MaxProtocolQueryLength = 2048;

        /// <summary>單一參數 value 解碼後字元數上限，超過該參數忽略。</summary>
        private const int MaxProtocolParamLength = 512;

        private const string KeyAppId = "PendingEditProfileAppId";
        private const string KeyDisplayName = "PendingEditProfileDisplayName";
        private const string KeyFullPath = "PendingEditProfileFullPath";

        /// <summary>
        /// 解析 omniconsole://edit-gamepad-profile?appId=...&displayName=... 的 query string，寫入 LocalSettings 供 SettingsPage 取用。
        /// Query 總長超過 MaxProtocolQueryLength 直接放棄；單一參數 value 超過 MaxProtocolParamLength 忽略。
        /// </summary>
        public static void Stash(Uri uri)
        {
            try
            {
                string query = uri.Query ?? string.Empty;
                if (query.Length > MaxProtocolQueryLength)
                {
                    DebugLogger.Log($"→ PendingEditProfileService.Stash: query length {query.Length} exceeds {MaxProtocolQueryLength}, ignored");
                    return;
                }
                string appId = string.Empty;
                string displayName = string.Empty;
                string fullPath = string.Empty;
                if (query.StartsWith("?")) query = query.Substring(1);
                foreach (var pair in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = pair.Substring(0, eq);
                    string val = pair.Substring(eq + 1);
                    string decoded = Uri.UnescapeDataString(val.Replace('+', ' '));
                    if (decoded.Length > MaxProtocolParamLength) continue;
                    if (string.Equals(key, "appId", StringComparison.OrdinalIgnoreCase)) appId = decoded;
                    else if (string.Equals(key, "displayName", StringComparison.OrdinalIgnoreCase)) displayName = decoded;
                    else if (string.Equals(key, "fullPath", StringComparison.OrdinalIgnoreCase)) fullPath = decoded;
                }
                var local = Windows.Storage.ApplicationData.Current.LocalSettings;
                if (!string.IsNullOrEmpty(appId))
                    local.Values[KeyAppId] = appId;
                if (!string.IsNullOrEmpty(displayName))
                    local.Values[KeyDisplayName] = displayName;
                if (!string.IsNullOrEmpty(fullPath))
                    local.Values[KeyFullPath] = fullPath;
                DebugLogger.Log($"→ Stashed pending edit profile: appId={appId}, displayName={displayName}, fullPath={fullPath}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"→ PendingEditProfileService.Stash EXCEPTION: {ex.Message}");
            }
        }

        /// <summary>
        /// 取出先前 Stash 進去的 appId / displayName 並從 LocalSettings 移除；無暫存或解析失敗皆回 false 且輸出 null/empty。
        /// </summary>
        public static bool TryConsume(out AppId? appId, out string displayName)
        {
            appId = null;
            displayName = string.Empty;
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalSettings;
                string? pendingFullPath = null;
                if (local.Values.TryGetValue(KeyFullPath, out var pathObj) && pathObj is string pathStr)
                {
                    if (AppId.IsValidFullPath(pathStr) && !string.IsNullOrWhiteSpace(pathStr))
                        pendingFullPath = pathStr;
                    local.Values.Remove(KeyFullPath);
                }
                if (local.Values.TryGetValue(KeyAppId, out var idObj) && idObj is string idStr)
                {
                    appId = AppId.Parse(idStr);
                    if (appId != null && appId.Kind == IdKind.Process)
                        appId.FullPath = pendingFullPath;
                    local.Values.Remove(KeyAppId);
                }
                if (local.Values.TryGetValue(KeyDisplayName, out var nameObj) && nameObj is string nameStr)
                {
                    displayName = nameStr;
                    local.Values.Remove(KeyDisplayName);
                }
                return appId != null || !string.IsNullOrEmpty(displayName);
            }
            catch
            {
                appId = null;
                displayName = string.Empty;
                return false;
            }
        }
    }
}
