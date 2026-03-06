using OmniConsole.Models;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理應用程式設定的持久化讀寫。
    /// 使用 ApplicationData.Current.LocalSettings 儲存於本機。
    /// 預設平台以穩定的字串 Id 儲存，而非列舉整數，確保平台清單調整後設定仍可正確讀取。
    /// </summary>
    public static class SettingsService
    {
        private const string DefaultPlatformKey = "DefaultPlatform";
        private const string LastLaunchedVersionKey = "LastLaunchedVersion";

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
                return PlatformCatalog.FindById(id) ?? PlatformCatalog.All[0];
            }

            return PlatformCatalog.All[0];
        }

        /// <summary>
        /// 儲存使用者選擇的預設遊戲平台（以 Id 字串持久化）。
        /// </summary>
        public static void SetDefaultPlatform(PlatformDefinition platform)
        {
            ApplicationData.Current.LocalSettings.Values[DefaultPlatformKey] = platform.Id;
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
    }
}
