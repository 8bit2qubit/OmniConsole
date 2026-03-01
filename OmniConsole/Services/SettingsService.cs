using OmniConsole.Models;
using Windows.Storage;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理應用程式設定的持久化讀寫。
    /// 使用 ApplicationData.Current.LocalSettings 儲存於本機。
    /// </summary>
    public static class SettingsService
    {
        private const string DefaultPlatformKey = "DefaultPlatform";
        private const string LastLaunchedVersionKey = "LastLaunchedVersion";

        /// <summary>
        /// 取得目前應用程式的版本號字串。
        /// </summary>
        private static string GetAppVersion()
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
            {
                return true;
            }

            // 若已設定平台，檢查是否為剛更新版本
            if (settings.Values.TryGetValue(LastLaunchedVersionKey, out object? value) && value is string lastVersion)
            {
                if (lastVersion != GetAppVersion())
                {
                    return true;
                }
            }
            else
            {
                // 若無版本紀錄 (例如從舊版升級上來)，亦視為需重新確認的更新啟動
                return true;
            }

            return false;
        }

        /// <summary>
        /// 儲存目前應用程式的版本號以供下次啟動比對。
        /// </summary>
        public static void SaveCurrentVersion()
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[LastLaunchedVersionKey] = GetAppVersion();
        }

        /// <summary>
        /// 取得使用者設定的預設遊戲平台。
        /// 若尚未設定，回傳 SteamBigPicture 作為預設值。
        /// </summary>
        public static GamePlatform GetDefaultPlatform()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(DefaultPlatformKey, out object? value) && value is int intValue)
            {
                return (GamePlatform)intValue;
            }
            return GamePlatform.SteamBigPicture;
        }

        /// <summary>
        /// 儲存使用者選擇的預設遊戲平台。
        /// </summary>
        public static void SetDefaultPlatform(GamePlatform platform)
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[DefaultPlatformKey] = (int)platform;
        }
    }
}
