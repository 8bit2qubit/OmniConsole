using System.Runtime.InteropServices;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 偵測目前是否處於 Windows FSE（Full Screen Experience / 全螢幕體驗）模式。
    /// 用於 Widget 平台條件可見性判斷（SteamInGameOverlay 觸發按鈕僅在 FSE + 預設平台為 SteamBigPicture 時顯示）。
    /// </summary>
    internal static class FseStatus
    {
        // api-ms-win-gaming-experience-l1-1-0.dll 為 Windows API Set；不可解析時下方 try/catch 回 false。
        // 與 OmniConsole 主程式 FseService、PhantomBridge 內部偵測共用同一 API。
        [DllImport("api-ms-win-gaming-experience-l1-1-0.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsGamingFullScreenExperienceActive();

        public static bool IsActive()
        {
            try { return IsGamingFullScreenExperienceActive(); }
            catch { return false; }
        }
    }
}
