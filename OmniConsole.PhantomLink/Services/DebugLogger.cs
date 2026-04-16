#if DEBUG
using System;
using System.IO;
#endif
using System.Diagnostics;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 簡易的檔案式 Debug 日誌工具。
    /// 僅在 DEBUG 建置時實際寫入檔案；Release 建置中所有呼叫皆為空操作 (no-op)。
    /// 日誌位置：%LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalCache\Local\OmniConsole\PhantomLinkTrace.log
    /// </summary>
    internal static class DebugLogger
    {
#if DEBUG
        private static readonly string _logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniConsole", "PhantomLinkTrace.log");
#endif

        [Conditional("DEBUG")]
        public static void Log(string message)
        {
#if DEBUG
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
#endif
        }
    }
}
