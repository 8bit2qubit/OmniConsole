using OmniConsole.Models;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniConsole.Services
{
    /// <summary>
    /// 自訂平台分享用的可序列化資料結構。
    /// 刻意省略 id 與 iconFileName：id 由接收端重新產生，圖示不隨文字傳遞。
    /// 各欄位依 launchType 僅輸出有意義的欄位（null 欄位不序列化）：
    /// ProtocolUri → shell + displayName + launchType + launchTarget
    /// Executable  → shell + displayName + launchType + launchTarget + arguments
    /// PackagedApp → shell + displayName + launchType + packageFamilyName
    /// </summary>
    internal class PlatformSharePayload
    {
        [JsonPropertyName("shell")]
        public string Shell { get; set; } = "OmniConsole";

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = "";

        [JsonPropertyName("launchType")]
        public string LaunchType { get; set; } = "ProtocolUri";

        [JsonPropertyName("launchTarget")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? LaunchTarget { get; set; }

        [JsonPropertyName("arguments")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Arguments { get; set; }

        [JsonPropertyName("packageFamilyName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PackageFamilyName { get; set; }
    }

    [JsonSerializable(typeof(PlatformSharePayload))]
    internal partial class PlatformShareJsonContext : JsonSerializerContext { }

    /// <summary>
    /// 提供自訂平台的 JSON 匯出與匯入，與 UI 完全解耦。
    /// 欄位驗證邏輯委由 <see cref="PlatformFieldValidator"/> 統一執行，與 PlatformEditDialog 保持一致。
    /// </summary>
    public static class UserPlatformShareService
    {
        private static readonly string[] ValidLaunchTypes = ["ProtocolUri", "Executable", "PackagedApp"];

        /// <summary>
        /// 將 <see cref="UserPlatformEntry"/> 序列化為縮排 JSON 字串，供剪貼簿分享。
        /// 依 launchType 僅輸出有意義的欄位，不含 id 與 iconFileName。
        /// 輸出首行含 "shell": "OmniConsole" 識別此 JSON 為 OmniConsole 格式。
        /// </summary>
        public static string Export(UserPlatformEntry entry)
        {
            var payload = new PlatformSharePayload
            {
                Shell = "OmniConsole",
                DisplayName = entry.DisplayName,
                LaunchType = entry.LaunchType,
            };

            switch (entry.LaunchType)
            {
                case "ProtocolUri":
                    payload.LaunchTarget = entry.LaunchTarget;
                    break;
                case "Executable":
                    payload.LaunchTarget = entry.LaunchTarget;
                    payload.Arguments = entry.Arguments;
                    break;
                case "PackagedApp":
                    payload.PackageFamilyName = entry.PackageFamilyName;
                    break;
            }

            var options = new JsonSerializerOptions(PlatformShareJsonContext.Default.Options) { WriteIndented = true };
            return JsonSerializer.Serialize(payload, new PlatformShareJsonContext(options).PlatformSharePayload);
        }

        /// <summary>
        /// 將 JSON 字串反序列化並驗證後，回傳新的 <see cref="UserPlatformEntry"/>。
        /// 依 launchType 僅讀取有意義的欄位，其餘欄位強制清空。
        /// 欄位驗證規則與 PlatformEditDialog 一致（共用 <see cref="PlatformFieldValidator"/>）。
        /// </summary>
        /// <param name="json">使用者貼上的 JSON 字串。</param>
        /// <returns>
        /// 成功：(Entry, null)；失敗：(null, errorKey)，errorKey 為 Resources.resw 資源鍵。
        /// </returns>
        public static (UserPlatformEntry? Entry, string? ErrorKey) Import(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (null, "Import_Error_EmptyJson");

            PlatformSharePayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize(json, PlatformShareJsonContext.Default.PlatformSharePayload);
            }
            catch (JsonException)
            {
                return (null, "Import_Error_InvalidJson");
            }

            if (payload is null)
                return (null, "Import_Error_InvalidJson");

            if (!PlatformFieldValidator.IsValidName(payload.DisplayName))
                return (null, string.IsNullOrWhiteSpace(payload.DisplayName)
                    ? "Import_Error_MissingName"
                    : "Import_Error_InvalidCharacters");

            if (Array.IndexOf(ValidLaunchTypes, payload.LaunchType) < 0)
                return (null, "Import_Error_InvalidLaunchType");

            string launchTarget = "";
            string arguments = "";
            string packageFamilyName = "";

            switch (payload.LaunchType)
            {
                case "ProtocolUri":
                    {
                        string t = payload.LaunchTarget?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(t))
                            return (null, "Import_Error_MissingTarget");
                        if (!PlatformFieldValidator.IsValidUri(t))
                            return (null, "Import_Error_InvalidUri");
                        launchTarget = t;
                        break;
                    }
                case "Executable":
                    {
                        string t = payload.LaunchTarget?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(t))
                            return (null, "Import_Error_MissingTarget");
                        if (!PlatformFieldValidator.IsValidExecutablePath(t))
                            return (null, "Import_Error_InvalidExecutable");
                        launchTarget = t;
                        string a = payload.Arguments?.Trim() ?? "";
                        if (!PlatformFieldValidator.IsValidArguments(a))
                            return (null, "Import_Error_InvalidCharacters");
                        arguments = a;
                        break;
                    }
                case "PackagedApp":
                    {
                        string p = payload.PackageFamilyName?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(p))
                            return (null, "Import_Error_MissingTarget");
                        if (!PlatformFieldValidator.IsValidPackageFamilyName(p))
                            return (null, "Import_Error_InvalidCharacters");
                        string ownFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;
                        if (!PlatformFieldValidator.IsAllowedPackageFamilyName(p, ownFamilyName))
                            return (null, "Import_Error_PackageNotAllowed");
                        packageFamilyName = p;
                        break;
                    }
            }

            var entry = new UserPlatformEntry
            {
                Id = $"user_{Guid.NewGuid():N}",
                DisplayName = payload.DisplayName.Trim(),
                LaunchType = payload.LaunchType,
                LaunchTarget = launchTarget,
                Arguments = arguments,
                PackageFamilyName = packageFamilyName,
                IconFileName = "",
            };
            return (entry, null);
        }
    }
}
