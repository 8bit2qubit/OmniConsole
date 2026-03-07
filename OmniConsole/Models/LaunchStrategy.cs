namespace OmniConsole.Models
{
    /// <summary>
    /// 啟動策略的類型，決定執行時採用哪種機制啟動平台。
    /// </summary>
    public enum LaunchStrategyType
    {
        /// <summary>透過 Windows Protocol URI 啟動（例如 steam://、xbox://）。</summary>
        ProtocolUri,

        /// <summary>從 Windows 登錄機碼讀取安裝路徑，直接執行執行檔。</summary>
        Registry,

        /// <summary>透過 PackageManager 找到已安裝的 MSIX 套件並啟動。</summary>
        MsixPackage,

        /// <summary>直接執行指定的執行檔（配合絕對路徑、SearchPaths 或系統 PATH/App Paths 機制）。</summary>
        Executable,
    }

    /// <summary>
    /// 描述單一啟動策略所需的所有參數。
    /// 不同策略類型使用不同欄位，未使用的欄位保持 null。
    /// </summary>
    public record LaunchStrategy
    {
        /// <summary>策略類型，決定使用哪組欄位。</summary>
        public required LaunchStrategyType Type { get; init; }

        // ── ProtocolUri 策略 ──────────────────────────────────────────────────

        /// <summary>要啟動的 Protocol URI（例如 "steam://open/bigpicture"）。</summary>
        public string? Uri { get; init; }

        // ── Registry 策略 ────────────────────────────────────────────────────

        /// <summary>登錄機碼根節點，"HKCU" 或 "HKLM"。</summary>
        public string? RegistryRoot { get; init; }

        /// <summary>登錄機碼子路徑（例如 @"SOFTWARE\Valve\Steam"）。</summary>
        public string? RegistrySubKey { get; init; }

        /// <summary>要讀取的登錄值名稱（例如 "InstallPath"）。</summary>
        public string? RegistryValueName { get; init; }

        // ── Executable 與 Registry 共用屬性 ──────────────────────────────────

        /// <summary>執行檔名稱或絕對路徑（例如 "steam.exe" 或 "C:\Games\GameName\Game.exe"）。</summary>
        public string? ExecutableName { get; init; }

        /// <summary>傳遞給執行檔的命令列參數（例如 "-bigpicture"）。</summary>
        public string? Arguments { get; init; }

        // ── MsixPackage 策略 ─────────────────────────────────────────────────

        /// <summary>MSIX 套件名稱（PackageFamilyName 格式）。</summary>
        public string? PackageName { get; init; }

        /// <summary>MSIX 套件發佈者識別字串。</summary>
        public string? Publisher { get; init; }

        // ── Executable 策略 ──────────────────────────────────────────────

        /// <summary>純檔名啟動時的額外搜尋目錄（支援環境變數，例如 "%LOCALAPPDATA%\Playnite"），適用於 Executable 策略。</summary>
        public string[]? SearchPaths { get; init; }
    }
}
