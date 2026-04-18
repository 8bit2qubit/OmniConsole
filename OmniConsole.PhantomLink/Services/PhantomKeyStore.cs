using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Storage;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 透過 PublisherCacheFolder 共用 INI 與主程式 / PhantomKey 交換設定。
    /// 同 Publisher (CN=8bit2qubit) 的 MSIX 套件共用資料夾，繞過各自的沙箱。
    /// </summary>
    internal static class PhantomKeyStore
    {
        // ── 常數與有效值 ─────────────────────────────────────────────────────

        public const string MouseModeOff = "Off";
        public const string MouseModeAuto = "Auto";
        public const string MouseModeForceOn = "ForceOn";

        public const string LayoutOmniNav = "OmniNav";
        public const string LayoutClassic = "Classic";

        public static readonly int[] ValidCursorSpeedPercents = { 25, 50, 75, 100, 125, 150, 175, 200 };

        // ── 共用 INI 路徑（PublisherCacheFolder） ────────────────────────────

        private const string SharedFolderName = "OmniConsoleShared";
        private const string SharedIniFileName = "Shared.ini";

        private static string _cachedPath;

        /// <summary>
        /// 同 Publisher MSIX 套件共用的 INI 檔路徑；首次讀取時建立資料夾並快取。
        /// </summary>
        private static string SharedIniPath
        {
            get
            {
                if (_cachedPath != null) return _cachedPath;
                try
                {
                    var folder = ApplicationData.Current.GetPublisherCacheFolder(SharedFolderName);
                    _cachedPath = Path.Combine(folder.Path, SharedIniFileName);
                    DebugLogger.Log("[Store] PublisherCacheFolder=" + _cachedPath);
                }
                catch (Exception ex)
                {
                    _cachedPath = string.Empty;
                    DebugLogger.Log("[Store] GetPublisherCacheFolder FAIL: " + ex);
                }
                return _cachedPath;
            }
        }

        // ── INI 讀寫（Win32 Private Profile API） ───────────────────────────

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetPrivateProfileStringW(
            string lpAppName, string lpKeyName, string lpDefault,
            StringBuilder lpReturnedString, int nSize, string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileStringW(
            string lpAppName, string lpKeyName, string lpString, string lpFileName);

        /// <summary>
        /// 從共用 INI 讀值；路徑取不到或檔案不存在時回傳傳入的預設值。
        /// </summary>
        private static string Read(string section, string key, string def)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return def;
            var sb = new StringBuilder(256);
            GetPrivateProfileStringW(section, key, def, sb, sb.Capacity, path);
            return sb.ToString();
        }

        /// <summary>
        /// 寫入共用 INI；PublisherCacheFolder 無法取得時靜默略過。
        /// </summary>
        private static void Write(string section, string key, string value)
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path)) return;
            try { WritePrivateProfileStringW(section, key, value, path); } catch { }
        }

        /// <summary>
        /// Shared.ini 不存在時一次補齊 [PhantomKey] 所有預設值，避免只寫單一 key
        /// 造成其他 key 首次讀取拿不到值。[General] DefaultPlatform 屬主程式職責，不在此處寫入。
        /// </summary>
        public static void EnsureDefaultsIfMissing()
        {
            var path = SharedIniPath;
            if (string.IsNullOrEmpty(path) || File.Exists(path)) return;
            Write("PhantomKey", "MouseMode", MouseModeAuto);
            Write("PhantomKey", "MouseModeLayout", LayoutOmniNav);
            Write("PhantomKey", "CursorSpeedPercent", "100");
            Write("PhantomKey", "SteamInGameOverlayEnabled", "1");
            DebugLogger.Log("[Store] Seeded default [PhantomKey] values.");
        }

        // ── 公開 API：Mouse Mode ─────────────────────────────────────────────

        /// <summary>
        /// 讀取 Mouse Mode（Off / Auto / ForceOn）；無效或缺失值回 Auto。
        /// </summary>
        public static string GetMouseMode()
        {
            var s = Read("PhantomKey", "MouseMode", MouseModeAuto);
            if (s == MouseModeOff || s == MouseModeAuto || s == MouseModeForceOn) return s;
            return MouseModeAuto;
        }

        /// <summary>
        /// 寫入 Mouse Mode；非預期值會 fallback 成 Auto，避免下次讀取再做一次 fallback。
        /// </summary>
        public static void SetMouseMode(string mode)
        {
            if (mode != MouseModeOff && mode != MouseModeAuto && mode != MouseModeForceOn)
                mode = MouseModeAuto;
            Write("PhantomKey", "MouseMode", mode);
        }

        // ── 公開 API：Layout ─────────────────────────────────────────────────

        /// <summary>
        /// 讀取手把配置（OmniNav / Classic）；無效或缺失值回 OmniNav。
        /// </summary>
        public static string GetMouseModeLayout()
        {
            var s = Read("PhantomKey", "MouseModeLayout", LayoutOmniNav);
            if (s == LayoutClassic) return LayoutClassic;
            return LayoutOmniNav;
        }

        /// <summary>
        /// 寫入手把配置；非預期值 fallback 成 OmniNav。
        /// </summary>
        public static void SetMouseModeLayout(string layout)
        {
            if (layout != LayoutOmniNav && layout != LayoutClassic) layout = LayoutOmniNav;
            Write("PhantomKey", "MouseModeLayout", layout);
        }

        // ── 公開 API：Steam In-Game Overlay ──────────────────────────────────

        /// <summary>
        /// 讀取長按 ☰ 觸發 Steam In-Game Overlay 是否啟用；與主程式 WriteShared 約定一致（"1"=啟用）。
        /// 非 "0" 一律視為啟用，與缺失情境的預設值對齊。
        /// </summary>
        public static bool GetSteamInGameOverlayEnabled()
        {
            var s = Read("PhantomKey", "SteamInGameOverlayEnabled", "1");
            return s != "0";
        }

        /// <summary>
        /// 寫入長按 ☰ 觸發 Steam In-Game Overlay 開關狀態。
        /// </summary>
        public static void SetSteamInGameOverlayEnabled(bool enabled)
        {
            Write("PhantomKey", "SteamInGameOverlayEnabled", enabled ? "1" : "0");
        }

        // ── 公開 API：Cursor Speed ──────────────────────────────────────────

        /// <summary>
        /// 讀取游標速度百分比；必須落在 ValidCursorSpeedPercents 範圍內，否則回 100。
        /// </summary>
        public static int GetCursorSpeedPercent()
        {
            var s = Read("PhantomKey", "CursorSpeedPercent", "100");
            if (int.TryParse(s, out int pct))
            {
                foreach (var p in ValidCursorSpeedPercents)
                    if (p == pct) return p;
            }
            return 100;
        }

        /// <summary>
        /// 寫入游標速度百分比；不在 ValidCursorSpeedPercents 範圍時 fallback 成 100。
        /// </summary>
        public static void SetCursorSpeedPercent(int percent)
        {
            int valid = 100;
            foreach (var p in ValidCursorSpeedPercents)
                if (p == percent) { valid = p; break; }
            Write("PhantomKey", "CursorSpeedPercent", valid.ToString());
        }
    }
}
