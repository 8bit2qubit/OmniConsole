using System.Text.Json.Serialization;

namespace OmniConsole.Models
{
    /// <summary>
    /// 使用者自訂平台的可序列化資料結構。
    /// 儲存為 JSON 檔案於 LocalFolder，啟動時轉換為 <see cref="PlatformDefinition"/>。
    /// </summary>
    public class UserPlatformEntry
    {
        /// <summary>穩定的平台識別字串，自動產生（例如 "user_148c907dcba64306b03cbcd0d4cd6f7a"）。</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>使用者指定的顯示名稱。</summary>
        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        /// <summary>啟動類型：ProtocolUri 或 Executable。</summary>
        [JsonPropertyName("launchType")]
        public string LaunchType { get; set; } = "ProtocolUri";

        /// <summary>Protocol URI（例如 "steam://open/bigpicture"）或執行檔路徑。</summary>
        [JsonPropertyName("launchTarget")]
        public string LaunchTarget { get; set; } = "";

        /// <summary>啟動參數（選填）。</summary>
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; } = "";

        /// <summary>自訂圖示的檔案名稱（存放於 LocalFolder/PlatformIcons/，選填）。</summary>
        [JsonPropertyName("iconFileName")]
        public string IconFileName { get; set; } = "";

        /// <summary>
        /// 轉換為引擎可用的 <see cref="PlatformDefinition"/>。
        /// </summary>
        public PlatformDefinition ToPlatformDefinition()
        {
            var strategyType = LaunchType == "Executable"
                ? LaunchStrategyType.Executable
                : LaunchStrategyType.ProtocolUri;

            var strategy = strategyType == LaunchStrategyType.ProtocolUri
                ? new LaunchStrategy
                {
                    Type = LaunchStrategyType.ProtocolUri,
                    Uri = LaunchTarget,
                }
                : new LaunchStrategy
                {
                    Type = LaunchStrategyType.Executable,
                    ExecutableName = LaunchTarget,
                    Arguments = string.IsNullOrEmpty(Arguments) ? null : Arguments,
                };

            string iconAsset = string.IsNullOrEmpty(IconFileName)
                ? "ms-appx:///Assets/Platforms/custom.png"
                : $"ms-appdata:///local/PlatformIcons/{IconFileName}";

            return new PlatformDefinition
            {
                Id = Id,
                DisplayNameKey = $"__user__{Id}",
                IconAsset = iconAsset,
                AvailabilityStrategy = strategy,
                LaunchStrategies = [strategy],
            };
        }
    }

    /// <summary>
    /// System.Text.Json 原始碼產生器上下文，於編譯期產生序列化程式碼，
    /// 不依賴執行期反射，確保 IL Trimming 下正常運作。
    /// </summary>
    [JsonSerializable(typeof(UserPlatformEntry[]))]
    internal partial class UserPlatformJsonContext : JsonSerializerContext
    {
    }
}
