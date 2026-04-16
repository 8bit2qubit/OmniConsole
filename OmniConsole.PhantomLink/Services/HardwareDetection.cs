using System;
using System.Runtime.InteropServices;
using System.Text;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 硬體偵測 — 透過 HKLM BIOS Registry 判斷裝置型號。
    /// </summary>
    internal static class HardwareDetection
    {
        // ── Registry P/Invoke（UWP 無 Microsoft.Win32.Registry，需直接呼叫 advapi32） ──

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegOpenKeyExW(
            IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegQueryValueExW(
            IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType,
            StringBuilder lpData, ref int lpcbData);

        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        // ── 型號偵測 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 讀 BIOS SystemProductName 判斷是否為內建手把映射的裝置（ROG Ally 家族等）。
        /// 命中時 Widget 會停用 Mouse Mode 三顆 ToggleButton 並顯示說明，避免與 OEM 映射衝突。
        ///
        /// 主程式、PhantomKey、PhantomLink 三處各自獨立偵測，不經 INI；
        /// 機型清單更新時必須三處同步修改：
        ///   - OmniConsole/Services/SettingsService.cs (HasBuiltInGamepadMapping)
        ///   - OmniConsole.PhantomKey/Config.cpp (DetectBuiltInGamepadMapping)
        ///   - OmniConsole.PhantomLink/Services/HardwareDetection.cs (此函式)
        /// </summary>
        public static bool HasBuiltInGamepadMapping()
        {
            IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);
            const int KEY_READ = 0x20019;
            IntPtr hKey;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                              @"HARDWARE\DESCRIPTION\System\BIOS",
                              0, KEY_READ, out hKey) != 0) return false;
            try
            {
                var sb = new StringBuilder(256);
                int cb = sb.Capacity * 2;
                if (RegQueryValueExW(hKey, "SystemProductName", IntPtr.Zero,
                                     out _, sb, ref cb) != 0) return false;
                var upper = sb.ToString().ToUpperInvariant();
                string[] knownKeywords = { "RC71L", "RC72L", "RC72LA", "RC73XA", "RC73YA" };
                foreach (var kw in knownKeywords)
                    if (upper.Contains(kw)) return true;
                return false;
            }
            catch { return false; }
            finally { RegCloseKey(hKey); }
        }
    }
}
