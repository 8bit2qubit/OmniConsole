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
