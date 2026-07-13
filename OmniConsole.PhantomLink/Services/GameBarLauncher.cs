using Microsoft.Gaming.XboxGameBar;
using System;
using System.Threading.Tasks;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 以 Game Bar 代發 URI：收合 overlay、喚起 App。
    /// </summary>
    internal static class GameBarLauncher
    {
        /// <summary>收合 Game Bar overlay。</summary>
        public static Task<bool> DismissGameBarAsync(XboxGameBarWidget? widget, string tag)
        {
            return LaunchAsync(widget, GameBarUris.DismissGameBar, tag + ": dismiss");
        }

        /// <summary>交 Game Bar 代發指定 URI，回傳是否受理。</summary>
        public static async Task<bool> LaunchAsync(XboxGameBarWidget? widget, string uri, string tag)
        {
            if (widget == null)
            {
                DebugLogger.Log($"[GameBarLauncher] {tag}: widget is null, skip");
                return false;
            }

            try
            {
                bool accepted = await widget.LaunchUriAsync(new Uri(uri));
                DebugLogger.Log($"[GameBarLauncher] {tag}: returned {accepted}");
                return accepted;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[GameBarLauncher] {tag}: THREW HResult=0x{ex.HResult:X8} type={ex.GetType().Name} msg={ex.Message}");
                return false;
            }
        }
    }
}
