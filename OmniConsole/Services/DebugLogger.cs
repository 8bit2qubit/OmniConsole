using System.Diagnostics;

namespace OmniConsole.Services
{
    /// <summary>
    /// 簡易的檔案式 Debug 日誌工具。
    /// 僅在 DEBUG 建置時實際寫入檔案；Release 建置中所有呼叫皆為空操作 (no-op)。
    /// 日誌位置：%LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\OmniConsole\activation.log
    /// </summary>
    public static class DebugLogger
    {
#if DEBUG
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniConsole", "activation.log");
#endif

        /// <summary>
        /// 寫入一行帶有時戳的日誌訊息。在 Release 建置中此方法為 no-op。
        /// </summary>
        [Conditional("DEBUG")]
        public static void Log(string message)
        {
#if DEBUG
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
#endif
        }
    }
}
