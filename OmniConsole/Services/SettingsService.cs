using OmniConsole.Models;
using System.IO;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理應用程式設定的持久化讀寫。
    /// 使用 ApplicationData.Current.LocalSettings 儲存於本機。
    /// 預設平台以穩定的字串 Id 儲存，而非列舉整數，確保平台清單調整後設定仍可正確讀取。
    /// 同時維護 OmniConsole.ini 供外部程式（PhantomKey 等）讀取共用設定。
    /// </summary>
    public static class SettingsService
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool WritePrivateProfileString(
            string lpAppName, string lpKeyName, string lpString, string lpFileName);

        private static readonly string IniDir = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "OmniConsole");

        private static readonly string IniPath = Path.Combine(IniDir, "OmniConsole.ini");

        /// <summary>
        /// 將設定寫入 OmniConsole.ini，供外部程式（PhantomKey 等）讀取。
        /// </summary>
        private static void WriteIni(string section, string key, string value)
        {
            Directory.CreateDirectory(IniDir);
            WritePrivateProfileString(section, key, value, IniPath);
        }

        private const string DefaultPlatformKey = "DefaultPlatform";
        private const string LastLaunchedVersionKey = "LastLaunchedVersion";

        /// <summary>
        /// 將 LocalSettings 中的共用設定同步至 OmniConsole.ini，
        /// 確保即使使用者從未手動切換設定，PhantomKey 仍能讀取正確的值。
        /// 僅在首次安裝或版本更新時呼叫（由 IsFirstRunOrUpdate() 判斷）。
        /// </summary>
        public static void SyncIni()
        {
            var platform = GetDefaultPlatform();
            WriteIni("General", "DefaultPlatform", platform.Id);
            WriteIni("PhantomKey", "SteamInGameOverlayEnabled",
                GetUsePhantomKeySteamInGameOverlay() ? "1" : "0");
            WriteIni("PhantomKey", "MouseModeEnabled",
                GetUsePhantomKeyMouseMode() ? "1" : "0");
            WriteIni("PhantomKey", "MouseModeLayout", GetMouseModeLayout());
            WriteIni("PhantomKey", "CursorSpeedPercent",
                GetCursorSpeedPercent().ToString());
        }

        /// <summary>
        /// 取得目前應用程式的版本號字串。
        /// </summary>
        public static string GetAppVersion()
        {
            try
            {
                var version = Windows.ApplicationModel.Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// 判斷是否為首次啟動（尚未設定預設平台），或為更新後的首次啟動。
        /// </summary>
        public static bool IsFirstRunOrUpdate()
        {
            var settings = ApplicationData.Current.LocalSettings;

            // 若尚未設定平台，必為首次安裝啟動
            if (!settings.Values.ContainsKey(DefaultPlatformKey))
                return true;

            // 若已設定平台，檢查是否為剛更新版本
            if (settings.Values.TryGetValue(LastLaunchedVersionKey, out object? value) && value is string lastVersion)
                return lastVersion != GetAppVersion();

            // 若無版本紀錄（例如從舊版升級），亦視為需重新確認的更新啟動
            return true;
        }

        /// <summary>
        /// 儲存目前應用程式的版本號以供下次啟動比對。
        /// </summary>
        public static void SaveCurrentVersion()
        {
            ApplicationData.Current.LocalSettings.Values[LastLaunchedVersionKey] = GetAppVersion();
        }

        /// <summary>
        /// 取得使用者設定的預設遊戲平台。
        /// 儲存值為平台 Id 字串；若找不到對應的平台定義，則回退至清單中的第一個平台。
        /// </summary>
        public static PlatformDefinition GetDefaultPlatform()
        {
            var settings = ApplicationData.Current.LocalSettings;

            if (settings.Values.TryGetValue(DefaultPlatformKey, out object? value) && value is string id)
            {
                // 先查系統平台，再查使用者自訂平台
                return PlatformCatalog.FindById(id)
                    ?? UserPlatformStore.FindById(id)
                    ?? PlatformCatalog.All[0];
            }

            return PlatformCatalog.All[0];
        }

        /// <summary>
        /// 儲存使用者選擇的預設遊戲平台（以 Id 字串持久化）。
        /// </summary>
        public static void SetDefaultPlatform(PlatformDefinition platform)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(DefaultPlatformKey, out object? prev) && prev is string id && id == platform.Id)
                return;
            settings.Values[DefaultPlatformKey] = platform.Id;
            WriteIni("General", "DefaultPlatform", platform.Id);
        }
        /// <summary>
        /// 取得是否啟用「Game Bar 媒體櫃按鈕進入設定介面」功能。
        /// 預設為 true。
        /// </summary>
        public static bool GetUseGameBarLibraryForSettings()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UseGameBarLibraryForSettings", out object? value) && value is bool isEnabled)
            {
                return isEnabled;
            }
            return true;
        }

        /// <summary>
        /// 儲存是否啟用「Game Bar 媒體櫃按鈕進入設定介面」功能。
        /// </summary>
        public static void SetUseGameBarLibraryForSettings(bool isEnabled)
        {
            ApplicationData.Current.LocalSettings.Values["UseGameBarLibraryForSettings"] = isEnabled;
        }

        /// <summary>
        /// 取得是否啟用「Game Bar 平台對接 (Passthrough)」功能。
        /// 預設為 false。
        /// </summary>
        public static bool GetEnablePassthrough()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("EnablePassthrough", out object? value) && value is bool isEnabled)
            {
                return isEnabled;
            }
            return false;
        }

        /// <summary>
        /// 儲存是否啟用「Game Bar 平台對接 (Passthrough)」功能。
        /// </summary>
        public static void SetEnablePassthrough(bool isEnabled)
        {
            ApplicationData.Current.LocalSettings.Values["EnablePassthrough"] = isEnabled;
        }

        /// <summary>
        /// 取得使用者是否已接受自訂平台實驗性功能的免責聲明。
        /// </summary>
        public static bool GetCustomPlatformConsentAccepted()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CustomPlatformConsentAccepted", out object? value) && value is bool accepted)
            {
                return accepted;
            }
            return false;
        }

        /// <summary>
        /// 儲存使用者已接受自訂平台實驗性功能的免責聲明。
        /// </summary>
        public static void SetCustomPlatformConsentAccepted(bool accepted)
        {
            ApplicationData.Current.LocalSettings.Values["CustomPlatformConsentAccepted"] = accepted;
        }

        /// <summary>
        /// 取得是否啟用自動檢查更新。
        /// 預設為 true。
        /// </summary>
        public static bool GetAutoUpdateCheckEnabled()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("AutoUpdateCheckEnabled", out object? value) && value is bool enabled)
            {
                return enabled;
            }
            return true;
        }

        /// <summary>
        /// 儲存是否啟用自動檢查更新。
        /// </summary>
        public static void SetAutoUpdateCheckEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values["AutoUpdateCheckEnabled"] = enabled;
        }

        /// <summary>
        /// 取得上次檢查更新的日期（"yyyy-MM-dd" 格式）。
        /// </summary>
        public static string GetLastUpdateCheckDate()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("LastUpdateCheckDate", out object? value) && value is string date)
            {
                return date;
            }
            return "";
        }

        /// <summary>
        /// 儲存上次檢查更新的日期。
        /// </summary>
        public static void SetLastUpdateCheckDate(string date)
        {
            ApplicationData.Current.LocalSettings.Values["LastUpdateCheckDate"] = date;
        }

        /// <summary>
        /// 取得快取的最新可用版本號（如 "1.3.0.0"）。
        /// 空字串表示無新版或尚未檢查。
        /// </summary>
        public static string GetCachedNewVersion()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedNewVersion", out object? value) && value is string version)
            {
                return version;
            }
            return "";
        }

        /// <summary>
        /// 儲存快取的最新可用版本號。
        /// </summary>
        public static void SetCachedNewVersion(string version)
        {
            ApplicationData.Current.LocalSettings.Values["CachedNewVersion"] = version;
        }

        /// <summary>
        /// 清除快取的最新可用版本號（表示已是最新版）。
        /// </summary>
        public static void ClearCachedNewVersion()
        {
            ApplicationData.Current.LocalSettings.Values["CachedNewVersion"] = "";
        }

        /// <summary>
        /// 取得快取的 .msix 下載 URL。
        /// 空字串表示無可用下載連結。
        /// </summary>
        public static string GetCachedDownloadUrl()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CachedDownloadUrl", out object? value) && value is string url)
            {
                return url;
            }
            return "";
        }

        /// <summary>
        /// 儲存快取的 .msix 下載 URL。
        /// </summary>
        public static void SetCachedDownloadUrl(string url)
        {
            ApplicationData.Current.LocalSettings.Values["CachedDownloadUrl"] = url;
        }

        /// <summary>
        /// 清除快取的下載 URL。
        /// </summary>
        public static void ClearCachedDownloadUrl()
        {
            ApplicationData.Current.LocalSettings.Values["CachedDownloadUrl"] = "";
        }

        /// <summary>
        /// 取得是否啟用 PhantomKey 手把輸入服務（⧉ 鍵開啟平台選單）。
        /// 預設為 true。
        /// </summary>
        public static bool GetUsePhantomKey()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKey", out object? value) && value is bool enabled)
            {
                return enabled;
            }
            return true;
        }

        /// <summary>
        /// 儲存是否啟用 PhantomKey 手把輸入服務。
        /// </summary>
        public static void SetUsePhantomKey(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values["UsePhantomKey"] = enabled;
        }

        /// <summary>
        /// 取得是否啟用 Steam In-Game Overlay（長按 ☰ 送出 Overlay 快速鍵）。
        /// 預設為 true。
        /// </summary>
        public static bool GetUsePhantomKeySteamInGameOverlay()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeySteamInGameOverlay", out object? value) && value is bool enabled)
            {
                return enabled;
            }
            return true;
        }

        /// <summary>
        /// 儲存是否啟用 Steam In-Game Overlay，同步寫入 INI 供 PhantomKey 讀取。
        /// </summary>
        public static void SetUsePhantomKeySteamInGameOverlay(bool enabled)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeySteamInGameOverlay", out object? prev) && prev is bool val && val == enabled)
                return;
            settings.Values["UsePhantomKeySteamInGameOverlay"] = enabled;
            WriteIni("PhantomKey", "SteamInGameOverlayEnabled", enabled ? "1" : "0");
        }

        // ─── Gamepad Mouse Mode ──────────────────────────────────────────

        /// <summary>
        /// 取得是否啟用 Gamepad Mouse Mode（在瀏覽器/Epic 等前景時將手把映射為滑鼠+鍵盤）。
        /// 預設為 true。
        /// </summary>
        public static bool GetUsePhantomKeyMouseMode()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeyMouseMode", out object? value) && value is bool enabled)
                return enabled;
            return true;
        }

        /// <summary>
        /// 儲存是否啟用 Mouse Mode，同步寫入 INI 供 PhantomKey 讀取。
        /// </summary>
        public static void SetUsePhantomKeyMouseMode(bool enabled)
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("UsePhantomKeyMouseMode", out object? prev) && prev is bool val && val == enabled)
                return;
            settings.Values["UsePhantomKeyMouseMode"] = enabled;
            WriteIni("PhantomKey", "MouseModeEnabled", enabled ? "1" : "0");
        }

        /// <summary>OmniNav 預設版面配置。</summary>
        public const string LayoutOmniNav = "OmniNav";
        /// <summary>Classic 版面配置。</summary>
        public const string LayoutClassic = "Classic";

        /// <summary>
        /// 取得 Mouse Mode 按鍵配置（"OmniNav" 或 "Classic"）。預設 "OmniNav"。
        /// </summary>
        public static string GetMouseModeLayout()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseModeLayout", out object? value) && value is string str
                && (str == LayoutOmniNav || str == LayoutClassic))
                return str;
            return LayoutOmniNav;
        }

        /// <summary>
        /// 儲存 Mouse Mode 按鍵配置，未知值回退至 "OmniNav"，同步寫入 INI。
        /// </summary>
        public static void SetMouseModeLayout(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) layout = LayoutOmniNav;
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("MouseModeLayout", out object? prev) && prev is string pv && pv == layout)
                return;
            settings.Values["MouseModeLayout"] = layout;
            WriteIni("PhantomKey", "MouseModeLayout", layout);
        }

        public static readonly int[] ValidCursorSpeedPercents = { 25, 50, 75, 100, 125, 150, 175, 200 };

        /// <summary>
        /// 取得游標速度百分比，限制為 25/50/75/100/125/150/175/200。預設 100。
        /// </summary>
        public static int GetCursorSpeedPercent()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CursorSpeedPercent", out object? value) && value is int pct)
            {
                foreach (var p in ValidCursorSpeedPercents)
                    if (p == pct) return p;
            }
            return 100;
        }

        /// <summary>
        /// 儲存游標速度百分比（限制為合法檔位），同步寫入 INI。
        /// </summary>
        public static void SetCursorSpeedPercent(int percent)
        {
            int valid = 100;
            foreach (var p in ValidCursorSpeedPercents)
                if (p == percent) { valid = p; break; }
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue("CursorSpeedPercent", out object? prev) && prev is int pv && pv == valid)
                return;
            settings.Values["CursorSpeedPercent"] = valid;
            WriteIni("PhantomKey", "CursorSpeedPercent", valid.ToString());
        }

        /// <summary>
        /// 偵測裝置是否內建廠商手把映射軟體（與 Mouse Mode 衝突需停用）。
        /// 目前清單僅包含 ROG Ally 家族（Armoury Crate SE）；未來可擴充其他掌機。
        /// </summary>
        public static bool HasBuiltInGamepadMapping()
        {
            // return false; // 測試用：略過內建映射偵測
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
                if (key?.GetValue("SystemProductName") is not string product) return false;
                var upper = product.ToUpperInvariant();
                string[] knownKeywords = { "RC71L", "RC72L", "RC72LA", "RC73XA", "RC73YA" };
                foreach (var kw in knownKeywords)
                    if (upper.Contains(kw)) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
