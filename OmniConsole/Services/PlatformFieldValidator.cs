using System;
using System.IO;
using System.Text.RegularExpressions;

namespace OmniConsole.Services
{
    /// <summary>
    /// 平台欄位的核心驗證邏輯，供 <see cref="UserPlatformShareService"/> 與
    /// <see cref="OmniConsole.Dialogs.PlatformEditDialog"/> 共用，確保兩處規則一致。
    /// </summary>
    internal static class PlatformFieldValidator
    {
        /// <summary>執行檔路徑中拒絕的字元。</summary>
        internal static readonly char[] DangerousPathChars = ['|', '&', ';', '>', '<', '`', '$'];

        /// <summary>Protocol URI 與啟動參數中拒絕的字元；控制字元另由 <see cref="HasControlCharacters"/> 檢查。</summary>
        internal static readonly char[] DangerousUriArgChars = ['|', '`', '$'];

        private static readonly Regex UriRegex =
            new(@"^[a-zA-Z][a-zA-Z0-9+\-.]*://\S*$", RegexOptions.Compiled);

        // ── 名稱 ─────────────────────────────────────────────────────────────

        /// <summary>驗證平台顯示名稱：不可空白、長度不超過 50、不含控制字元。</summary>
        public static bool IsValidName(string name)
            => !string.IsNullOrWhiteSpace(name)
            && name.Length <= 50
            && !HasControlCharacters(name);

        // ── Protocol URI ──────────────────────────────────────────────────────

        /// <summary>
        /// 驗證 Protocol URI：不可空白、長度不超過 2048、符合 URI 格式、不含控制字元、不含 <see cref="DangerousUriArgChars"/>。
        /// </summary>
        public static bool IsValidUri(string uri)
            => !string.IsNullOrWhiteSpace(uri)
            && uri.Length <= 2048
            && UriRegex.IsMatch(uri)
            && !HasControlCharacters(uri)
            && uri.IndexOfAny(DangerousUriArgChars) < 0;

        /// <summary>OmniConsole 自身登錄的 protocol scheme。</summary>
        internal const string OwnProtocolScheme = "omniconsole";

        /// <summary>
        /// 檢查 URI 的 scheme 是否為 OmniConsole 自身登錄的 protocol。
        /// 啟動自身會導致 FSE 循環重啟。
        /// </summary>
        public static bool IsSelfProtocolUri(string uri)
        {
            int schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return false;
            return uri[..schemeEnd].Equals(OwnProtocolScheme, StringComparison.OrdinalIgnoreCase);
        }

        // ── 執行檔路徑 ────────────────────────────────────────────────────────

        /// <summary>
        /// 驗證執行檔路徑：不可空白、長度不超過 260、不含 <see cref="DangerousPathChars"/> 或無效路徑字元、須以 .exe 結尾。
        /// </summary>
        public static bool IsValidExecutablePath(string path)
            => !string.IsNullOrWhiteSpace(path)
            && path.Length <= 260
            && path.IndexOfAny(DangerousPathChars) < 0
            && path.IndexOfAny(Path.GetInvalidPathChars()) < 0
            && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 檢查執行檔是否為 Console 應用程式（IMAGE_SUBSYSTEM_WINDOWS_CUI）。
        /// 讀取 PE Header 的 Subsystem 欄位，僅需讀取前幾百 bytes，不執行檔案。
        /// </summary>
        /// <returns>Console app 回傳 <see langword="true"/>；GUI app、檔案不存在或讀取失敗回傳 <see langword="false"/>。</returns>
        public static bool IsConsoleApplication(string path)
        {
            try
            {
                // FileShare.ReadWrite：避免其他程式正在寫入此檔時發生鎖定衝突
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // 基本長度檢查（DOS Header 至少需要 0x40 bytes）
                if (fs.Length < 0x40) return false;

                using var reader = new BinaryReader(fs);

                // DOS Header: 前 2 bytes 必須是 "MZ"
                if (reader.ReadUInt16() != 0x5A4D) return false;

                // e_lfanew: offset 0x3C，指向 PE Header 位置
                fs.Seek(0x3C, SeekOrigin.Begin);
                int peOffset = reader.ReadInt32();

                // 安全性檢查：確保 peOffset 合理，且檔案夠大能容納
                // PE Signature(4) + COFF Header(20) + Optional Header 到 Subsystem(0x44) + Subsystem(2) = 0x5E
                if (peOffset < 0 || peOffset > fs.Length - 0x5E) return false;

                // PE Signature: 必須是 "PE\0\0" (0x00004550)
                fs.Seek(peOffset, SeekOrigin.Begin);
                if (reader.ReadUInt32() != 0x00004550) return false;

                // COFF Header: 20 bytes，跳過
                fs.Seek(20, SeekOrigin.Current);

                // Optional Header: Subsystem 在 offset 0x44（PE32 與 PE32+ 相同，
                // 因為 PE32 的 BaseOfData(4)+ImageBase(4) = PE32+ 的 ImageBase(8)）
                fs.Seek(0x44, SeekOrigin.Current);
                ushort subsystem = reader.ReadUInt16();

                // IMAGE_SUBSYSTEM_WINDOWS_CUI = 3
                bool isCui = subsystem == 3;
                DebugLogger.Log($"[PlatformFieldValidator] IsConsoleApplication '{path}': subsystem={subsystem}, isCUI={isCui}");
                return isCui;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PlatformFieldValidator] IsConsoleApplication failed for '{path}': {ex.Message}");
                return false;
            }
        }

        // ── 啟動參數 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 驗證啟動參數（可為空）：長度不超過 500、不含控制字元、不含 <see cref="DangerousUriArgChars"/>。
        /// </summary>
        public static bool IsValidArguments(string args)
            => args.Length <= 500
            && !HasControlCharacters(args)
            && args.IndexOfAny(DangerousUriArgChars) < 0;

        // ── 封裝應用程式識別名稱 ──────────────────────────────────────────────

        private static readonly Regex PackageFamilyNameRegex =
            new(@"^[A-Za-z0-9.\-_]+$", RegexOptions.Compiled);

        /// <summary>Game Bar 的 PackageFamilyName，啟動會導致 FSE 衝突。</summary>
        internal const string GameBarFamilyName = "Microsoft.XboxGamingOverlay_8wekyb3d8bbwe";

        /// <summary>
        /// 驗證封裝應用程式識別名稱：不可空白、僅允許字母/數字/點/底線/連字號。
        /// </summary>
        public static bool IsValidPackageFamilyName(string name)
            => !string.IsNullOrWhiteSpace(name)
            && PackageFamilyNameRegex.IsMatch(name.Trim());

        /// <summary>
        /// 驗證封裝應用程式識別名稱是否允許作為啟動目標。
        /// 在格式驗證之上，排除已知會造成循環啟動或啟動失敗的套件。
        /// </summary>
        /// <param name="familyName">欲驗證的 PackageFamilyName。</param>
        /// <param name="ownFamilyName">OmniConsole 自身的 PackageFamilyName，用於防止自我循環啟動。</param>
        /// <returns>允許作為啟動目標時回傳 <see langword="true"/>。</returns>
        public static bool IsAllowedPackageFamilyName(string familyName, string ownFamilyName)
        {
            if (!IsValidPackageFamilyName(familyName)) return false;

            string trimmed = familyName.Trim();

            // 禁止自身（循環啟動）與 Game Bar（FSE 衝突）
            if (trimmed.Equals(ownFamilyName, StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.Equals(GameBarFamilyName, StringComparison.OrdinalIgnoreCase)) return false;
            // 禁止 PhantomLink (OmniCharm)：Game Bar 小工具，無獨立啟動進入點
            if (trimmed.Equals(UpdateCheckService.PhantomLinkFamilyName, StringComparison.OrdinalIgnoreCase)) return false;

            // 從 PackageFamilyName 提取套件名稱部分（最後一個底線之前）
            int lastUnderscore = trimmed.LastIndexOf('_');
            string pkgName = lastUnderscore > 0 ? trimmed[..lastUnderscore] : trimmed;

            // 排除 FSE 干擾應用程式（啟動前會被 KillIgnoredBackgroundServices 終止，設為目標會導致循環啟動）
            if (pkgName.Contains("Nahimic", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.Contains("RealtekAudioControl", StringComparison.OrdinalIgnoreCase)) return false;

            // 排除延伸模組、解碼器 OEM、裝置廠商背景服務、系統功能負載
            if (pkgName.Contains("Extension", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.Contains("DecoderOEM", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.Contains("ASUSCommandCenter", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.Contains("RSXCM", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.StartsWith("aimgr", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.StartsWith("ASUSAmbientHAL", StringComparison.OrdinalIgnoreCase)) return false;
            if (pkgName.StartsWith("WindowsWorkload.", StringComparison.OrdinalIgnoreCase)) return false;

            // 排除無進入點的 Microsoft 執行時期/服務套件
            bool isMicrosoft = pkgName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            || pkgName.StartsWith("MicrosoftWindows.", StringComparison.OrdinalIgnoreCase)
                            || pkgName.StartsWith("MicrosoftCorporationII.", StringComparison.OrdinalIgnoreCase);
            if (isMicrosoft)
            {
                if (pkgName.Contains("WinAppRuntime", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("ExperiencePack", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("IdentityProvider", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("GamingServices", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("Overlay", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("TCUI", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("OneDriveSync", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("PurchaseApp", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("ActionsServer", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("AppInstaller", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("Handwriting", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("GameAssist", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("CrossDevice", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("DevHome", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("MicrosoftEdge", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("WidgetsPlatformRuntime", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("WebExperience", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("StartExperiences", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("ApplicationCompatibility", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("AutoSuperResolution", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("Speech", StringComparison.OrdinalIgnoreCase)
                 || pkgName.Contains("Voice", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return true;
        }

        // ── 共用輔助方法 ──────────────────────────────────────────────────────────

        /// <summary>檢查字串是否含有控制字元（0x00–0x1F，排除一般空白）。</summary>
        public static bool HasControlCharacters(string value)
        {
            foreach (char c in value)
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') return true;
            return false;
        }
    }
}
