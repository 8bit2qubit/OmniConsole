using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Management.Deployment;

namespace OmniConsole.Services
{
    /// <summary>
    /// 集中收集「關於」頁需要的資訊，並提供格式化為 Markdown 的方法。
    /// </summary>
    public static class AboutInfoService
    {
        // ── XFSET 安裝路徑（安裝程式不開放自訂，路徑固定） ─────────────────────
        private const string XfsetDir = @"C:\Program Files\8bit2qubit\Xbox FullScreen Experience Tool";
        private static readonly string XfsetToolPath = Path.Combine(XfsetDir, "XboxFullScreenExperienceTool.exe");
        private static readonly string PhysPanelPath = Path.Combine(XfsetDir, "PhysPanelCS.exe");
        private const string NotInstalled = "(not installed)";
        private const string Unknown = "(unknown)";

        // PhysPanelCS master 用 Global\ 前綴的命名 mutex 自我互斥；只要 master 還在跑就存在。
        // PhysPanelCPP/TouchManager.cpp::MASTER_MUTEX_NAME
        private const string PhysPanelMasterMutexName = @"Global\XFEST_TouchSvc_Master_Lock";

        // GetSystemMetrics(SM_MAXIMUMTOUCHES) 回傳硬體支援的觸控點數（>0 = 有觸控螢幕）。
        private const int SM_MAXIMUMTOUCHES = 95;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        // GlobalMemoryStatusEx：取系統實體記憶體總量
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        // GetUserGeoID(GEOCLASS_NATION) 取使用者「國家/地區」的 GeoId。
        [DllImport("kernel32.dll")]
        private static extern int GetUserGeoID(int GeoClass);

        // GetGeoInfoW(GeoId, GEO_ISO2, ...) 把 GeoId 轉為 ISO 兩字母國家／地區代碼。
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetGeoInfoW(int Location, int GeoType, StringBuilder lpGeoData, int cchData, int LangId);

        // GetUserGeoID 的 GeoClass：查詢國家層級。
        private const int GEOCLASS_NATION = 16;

        // GetGeoInfoW 的 GeoType：ISO 兩字母國家／地區代碼。
        private const int GEO_ISO2 = 4;

        // 「國家/地區」設定值位置（HKCU，REG_SZ）。
        private const string CountryRegionKey = @"Control Panel\International\Geo";
        private const string CountryRegionNationValue = "Nation";

        // 「裝置設定地區」設定值位置（HKLM，REG_DWORD）。
        private const string DeviceRegionKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Control Panel\DeviceRegion";
        private const string DeviceRegionValue = "DeviceRegion";

        // ── PhantomKey 健康檢查：透過 SendMessageTimeout ping 主迴圈推進狀況 ───────
        // PhantomKey 在 PingService 開了名稱為 "OmniConsole.PhantomKey.PingWnd" 的 message-only window，
        // 收到 WM_APP+1 會直接回傳「距離最後心跳的毫秒數」。
        private const string PhantomKeyPingWindowClass = "OmniConsole.PhantomKey.PingWnd";
        private const uint WM_APP_PING = 0x8000 + 1; // WM_APP + 1
        private const uint SMTO_ABORTIFHUNG = 0x0002;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // Token Integrity Level 讀取 — 區分 PhantomKey 是否在合適的完整性等級跑
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        private const uint TOKEN_QUERY = 0x0008;
        private const int TokenIntegrityLevel = 25;
        private const uint SECURITY_MANDATORY_LOW_RID = 0x1000;
        private const uint SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
        private const uint SECURITY_MANDATORY_HIGH_RID = 0x3000;
        private const uint SECURITY_MANDATORY_SYSTEM_RID = 0x4000;

        /// <summary>
        /// OmniConsole 套件群組（主程式 + PhantomBridge + PhantomKey + PhantomLink）的版本快照。
        /// </summary>
        public record ComponentVersions(
            string OmniConsole,
            string PhantomBridge,            // 從 PhantomLink 套件 InstalledLocation 取（PhantomBridge.exe 隨 PhantomLink MSIX 一同部署）
            string PhantomKey,               // 主套件 InstalledLocation 內的 .exe（FileVersionInfo 來源）
            string PhantomKeyDeployed,       // 使用者 LocalAppData 下實際被執行的 .exe 副本版本，用以診斷複製失敗或舊版殘留
            string PhantomLink);             // PhantomLink Package.Id.Version

        /// <summary>
        /// XFSET（Xbox Full Screen Experience Tool）的安裝狀態與 PhysPanelCS touchservice 執行狀況。
        /// </summary>
        public record XfsetInfo(
            bool ToolInstalled,
            string ToolVersion,
            bool PhysPanelInstalled,
            string PhysPanelVersion,
            TouchServiceState TouchService);

        /// <summary>
        /// PhysPanelCS.exe touchservice 的狀態。
        /// </summary>
        public enum TouchServiceState
        {
            NotConfigured,
            Running,
            Unknown
        }

        /// <summary>
        /// 顯示卡資訊（名稱、VRAM、驅動程式版本與日期）。
        /// </summary>
        public record GpuInfo(
            string Name,
            ulong VramBytes,
            string DriverVersion,
            string DriverDate);

        /// <summary>
        /// 系統硬體快照（主機/主機板廠牌、CPU、RAM、GPU）。
        /// </summary>
        public record HardwareInfo(
            string SystemManufacturer,
            string SystemProductName,
            string BaseboardManufacturer,
            string BaseboardProduct,
            string CpuName,
            int CpuMhz,
            int CpuPhysicalCores,
            int CpuLogicalCores,
            ulong RamTotalBytes,
            IReadOnlyList<GpuInfo> Gpus);

        /// <summary>
        /// PhantomKey 主迴圈推進狀況（透過 SendMessageTimeout 對 ping window 量測得到）。
        /// </summary>
        public enum PhantomKeyResponsiveness
        {
            NotRunning,    // 行程不存在
            NoPingWindow,  // 行程在但找不到 ping window（舊版 PhantomKey 或剛啟動）
            Hung,          // SendMessageTimeout 逾時 = 整個行程沒回應 (Hung)
            Stuck,         // ping 有回應，但延遲 > 1000ms = 主迴圈卡住但 ping 執行緒還活著
            Busy,          // 150ms < 延遲 <= 1000ms = 主迴圈忙但仍在推進（含 SendKeyCombo 等正常作業突發量）
            Responsive     // 延遲 <= 150ms = 健康（涵蓋閒置時 sleep=100ms 的自然心跳間隔）
        }

        /// <summary>
        /// Token Integrity Level 分級。
        /// </summary>
        public enum IntegrityLevel
        {
            Unknown,
            Low,
            Medium,
            High,
            System
        }

        /// <summary>
        /// PhantomKey 行程的健康快照：是否在跑、跑的是不是預期路徑、Uptime、完整性等級、主迴圈推進狀況。
        /// </summary>
        public record PhantomKeyHealth(
            bool ProcessRunning,
            int ProcessId,                         // -1 表示未在跑
            string ExecutablePath,                 // 跑的是哪個 .exe
            bool ExecutablePathExpected,           // ExecutablePath 是否等於 PhantomKeyService.DeployedExePath
            TimeSpan Uptime,                       // 從 Process.StartTime 推算
            IntegrityLevel IntegrityLevel,
            PhantomKeyResponsiveness Responsiveness,
            int PingLagMs);                        // ping 回傳的延遲（毫秒）；非 Responsive/Busy/Stuck 時為 -1

        /// <summary>
        /// 「關於」頁的整體快照：套件版本、XFSET、硬體、PhantomKey 健康、OS / FSE / Locale / 地區等環境資訊。
        /// </summary>
        public record EnvironmentSnapshot(
            ComponentVersions Versions,
            XfsetInfo Xfset,
            HardwareInfo Hardware,
            PhantomKeyHealth PhantomKey,
            string WindowsBuild,
            string FseState,
            string DeviceForm,
            int MaxTouchPoints,
            string Locale,
            string CountryRegion,
            string DeviceRegion,
            DateTimeOffset CapturedAt);

        // ── 公開 API ─────────────────────────────────────────────────────────

        /// <summary>
        /// 收集 OmniConsole 套件群組目前安裝的版本字串。
        /// </summary>
        public static ComponentVersions GetComponentVersions()
        {
            return new ComponentVersions(
                OmniConsole: SettingsService.GetAppVersion(),
                PhantomBridge: GetPhantomBridgeVersion(),
                PhantomKey: ReadFileVersion(PhantomKeyService.PackageExePath),
                PhantomKeyDeployed: ReadFileVersion(PhantomKeyService.DeployedExePath),
                PhantomLink: GetPhantomLinkVersion());
        }

        /// <summary>
        /// PhantomBridge.exe 隨 PhantomLink MSIX 一同部署（同 publisher），
        /// 故需從 PhantomLink 套件的 InstalledLocation 找它，而非主套件目錄。
        /// </summary>
        private static string GetPhantomBridgeVersion()
        {
            try
            {
                var pm = new PackageManager();
                var pkg = pm.FindPackagesForUser("", UpdateCheckService.PhantomLinkFamilyName).FirstOrDefault();
                if (pkg == null) return NotInstalled;

                var bridgePath = Path.Combine(pkg.InstalledLocation.Path, "OmniConsole.PhantomBridge.exe");
                return ReadFileVersion(bridgePath);
            }
            catch
            {
                return Unknown;
            }
        }

        /// <summary>
        /// 偵測 XFSET 與 PhysPanelCS 是否安裝，並透過 master mutex 判定 touchservice 執行狀態。
        /// </summary>
        public static XfsetInfo GetXfsetInfo()
        {
            bool toolInstalled = File.Exists(XfsetToolPath);
            string toolVersion = toolInstalled ? ReadFileVersion(XfsetToolPath) : NotInstalled;

            bool physInstalled = File.Exists(PhysPanelPath);
            string physVersion = physInstalled ? ReadFileVersion(PhysPanelPath) : NotInstalled;

            // PhysPanelCS 未安裝就直接判定 NotConfigured，省掉排程器查詢
            var touchState = physInstalled ? QueryTouchServiceState() : TouchServiceState.NotConfigured;

            return new XfsetInfo(
                ToolInstalled: toolInstalled,
                ToolVersion: toolVersion,
                PhysPanelInstalled: physInstalled,
                PhysPanelVersion: physVersion,
                TouchService: touchState);
        }

        /// <summary>
        /// 一次收齊所需的全部資訊：版本、XFSET、硬體、PhantomKey 健康、OS / FSE / Locale / 地區。
        /// </summary>
        public static EnvironmentSnapshot GetEnvironmentSnapshot()
        {
            int maxTouches = 0;
            try { maxTouches = GetSystemMetrics(SM_MAXIMUMTOUCHES); }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] GetSystemMetrics failed: {ex.Message}"); }

            return new EnvironmentSnapshot(
                Versions: GetComponentVersions(),
                Xfset: GetXfsetInfo(),
                Hardware: GetHardwareInfo(),
                PhantomKey: GetPhantomKeyHealth(),
                WindowsBuild: GetWindowsBuild(),
                FseState: GetFseStateText(),
                DeviceForm: GetDeviceFormText(),
                MaxTouchPoints: maxTouches,
                Locale: CultureInfo.CurrentUICulture.Name,
                CountryRegion: GetCountryRegionText(),
                DeviceRegion: GetDeviceRegionText(),
                CapturedAt: DateTimeOffset.Now);
        }

        // ── 地區資訊 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 把 GeoId 轉為「ISO 國碼 + GeoId」格式字串（例如 "TW (237)"）。
        /// GeoId 無效時回傳 Unknown；GetGeoInfoW 取不到 ISO 碼時僅回帶括號的 GeoId（例如 "(237)"）。
        /// </summary>
        private static string FormatGeoId(int geoId)
        {
            if (geoId <= 0) return Unknown;

            try
            {
                var sb = new StringBuilder(8);
                int len = GetGeoInfoW(geoId, GEO_ISO2, sb, sb.Capacity, 0);
                if (len > 0)
                {
                    string iso = sb.ToString().TrimEnd('\0');
                    return $"{iso} ({geoId})";
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] GetGeoInfoW failed: {ex.Message}"); }

            return $"({geoId})";
        }

        /// <summary>
        /// 取使用者「國家/地區」設定（HKCU Geo\Nation，REG_SZ）；讀不到時回退至 GetUserGeoID，兩者皆失敗回 Unknown。
        /// </summary>
        private static string GetCountryRegionText()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(CountryRegionKey);
                if (key?.GetValue(CountryRegionNationValue) is string str
                    && int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int geoId)
                    && geoId > 0)
                {
                    return FormatGeoId(geoId);
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] read country region failed: {ex.Message}"); }

            try { return FormatGeoId(GetUserGeoID(GEOCLASS_NATION)); }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] GetUserGeoID failed: {ex.Message}"); }

            return Unknown;
        }

        /// <summary>
        /// 取「裝置設定地區」設定（HKLM DeviceRegion，REG_DWORD），即裝置初次設定時選的國家/地區。
        /// </summary>
        private static string GetDeviceRegionText()
        {
            try
            {
                object? value = Registry.GetValue($@"HKEY_LOCAL_MACHINE\{DeviceRegionKey}", DeviceRegionValue, null);
                if (value is int geoId && geoId > 0)
                {
                    return FormatGeoId(geoId);
                }
            }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] read device region failed: {ex.Message}"); }

            return Unknown;
        }

        // ── PhantomKey 健康檢查 ─────────────────────────────────────────────────

        /// <summary>
        /// 收集 PhantomKey 的執行狀態：行程在不在、跑的是不是部署的版本、跑了多久、
        /// 完整性等級等級、以及主迴圈推進狀況（透過 ping window）。
        /// </summary>
        public static PhantomKeyHealth GetPhantomKeyHealth()
        {
            // ── 1. 從行程清單找跑的是 LocalAppData 的那支 ──
            int pid = -1;
            string exePath = string.Empty;
            DateTime startTime = default;
            string expectedPath = PhantomKeyService.DeployedExePath;
            bool pathExpected = false;

            foreach (var proc in Process.GetProcessesByName("Steam"))
            {
                try
                {
                    string? mainModule = proc.MainModule?.FileName;
                    if (mainModule != null &&
                        expectedPath.Equals(mainModule, StringComparison.OrdinalIgnoreCase))
                    {
                        pid = proc.Id;
                        exePath = mainModule;
                        startTime = proc.StartTime;
                        pathExpected = true;
                        break;
                    }
                }
                catch { /* 存取被拒等 — 略過 */ }
                finally { proc.Dispose(); }
            }

            if (pid < 0)
            {
                return new PhantomKeyHealth(
                    ProcessRunning: false,
                    ProcessId: -1,
                    ExecutablePath: string.Empty,
                    ExecutablePathExpected: false,
                    Uptime: TimeSpan.Zero,
                    IntegrityLevel: IntegrityLevel.Unknown,
                    Responsiveness: PhantomKeyResponsiveness.NotRunning,
                    PingLagMs: -1);
            }

            // ── 2. Uptime ──
            TimeSpan uptime = TimeSpan.Zero;
            try { uptime = DateTime.Now - startTime; }
            catch (Exception ex) { DebugLogger.Log($"[AboutInfoService] uptime calc failed: {ex.Message}"); }

            // ── 3. Token Integrity Level (完整性等級) ──
            var il = GetProcessIntegrityLevel(pid);

            // ── 4. Responsiveness：找 ping window → SendMessageTimeout ──
            var (resp, lagMs) = ProbePhantomKeyResponsiveness(pid);

            DebugLogger.Log($"[AboutInfoService] PhantomKey health: pid={pid}, path='{exePath}' (expected={pathExpected}), " +
                $"uptime={uptime}, IL={il}, resp={resp}, lag={lagMs}ms");

            return new PhantomKeyHealth(
                ProcessRunning: true,
                ProcessId: pid,
                ExecutablePath: exePath,
                ExecutablePathExpected: pathExpected,
                Uptime: uptime,
                IntegrityLevel: il,
                Responsiveness: resp,
                PingLagMs: lagMs);
        }

        /// <summary>
        /// 列舉所有 OmniConsole.PhantomKey.PingWnd 視窗，挑出隸屬於指定 PID 的那個，
        /// 用 SMTO_ABORTIFHUNG + 100ms 逾時做 SendMessageTimeout，回傳「健康分級 + 延遲毫秒」。
        /// </summary>
        private static (PhantomKeyResponsiveness, int) ProbePhantomKeyResponsiveness(int pid)
        {
            try
            {
                IntPtr hwnd = IntPtr.Zero;
                IntPtr cursor = IntPtr.Zero;
                while (true)
                {
                    cursor = FindWindowExW(IntPtr.Zero, cursor, PhantomKeyPingWindowClass, null);
                    if (cursor == IntPtr.Zero) break;

                    if (GetWindowThreadProcessId(cursor, out uint windowPid) && windowPid == (uint)pid)
                    {
                        hwnd = cursor;
                        break;
                    }
                }

                if (hwnd == IntPtr.Zero)
                    return (PhantomKeyResponsiveness.NoPingWindow, -1);

                IntPtr result;
                IntPtr ret = SendMessageTimeoutW(hwnd, WM_APP_PING, IntPtr.Zero, IntPtr.Zero,
                    SMTO_ABORTIFHUNG, 100, out result);

                if (ret == IntPtr.Zero)
                    return (PhantomKeyResponsiveness.Hung, -1);

                int lag = (int)result.ToInt64();
                if (lag < 0) lag = 0;

                if (lag > 1000) return (PhantomKeyResponsiveness.Stuck, lag);
                if (lag > 150) return (PhantomKeyResponsiveness.Busy, lag);
                return (PhantomKeyResponsiveness.Responsive, lag);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] ProbePhantomKeyResponsiveness failed: {ex.Message}");
                return (PhantomKeyResponsiveness.NoPingWindow, -1);
            }
        }

        /// <summary>
        /// 讀取行程的 Token Integrity Level (完整性等級)。
        /// PhantomKey 由使用者啟動 = Medium IL。
        /// </summary>
        private static IntegrityLevel GetProcessIntegrityLevel(int pid)
        {
            IntPtr hToken = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!OpenProcessToken(proc.Handle, TOKEN_QUERY, out hToken))
                    return IntegrityLevel.Unknown;

                GetTokenInformation(hToken, TokenIntegrityLevel, IntPtr.Zero, 0, out uint len);
                if (len == 0) return IntegrityLevel.Unknown;

                buf = Marshal.AllocHGlobal((int)len);
                if (!GetTokenInformation(hToken, TokenIntegrityLevel, buf, len, out _))
                    return IntegrityLevel.Unknown;

                IntPtr pSid = Marshal.ReadIntPtr(buf);
                IntPtr pSubCount = GetSidSubAuthorityCount(pSid);
                int subCount = Marshal.ReadByte(pSubCount);
                IntPtr pRid = GetSidSubAuthority(pSid, (uint)(subCount - 1));
                uint rid = (uint)Marshal.ReadInt32(pRid);

                if (rid >= SECURITY_MANDATORY_SYSTEM_RID) return IntegrityLevel.System;
                if (rid >= SECURITY_MANDATORY_HIGH_RID) return IntegrityLevel.High;
                if (rid >= SECURITY_MANDATORY_MEDIUM_RID) return IntegrityLevel.Medium;
                if (rid >= SECURITY_MANDATORY_LOW_RID) return IntegrityLevel.Low;
                return IntegrityLevel.Unknown;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetProcessIntegrityLevel failed: {ex.Message}");
                return IntegrityLevel.Unknown;
            }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (hToken != IntPtr.Zero) CloseHandle(hToken);
            }
        }

        /// <summary>
        /// 將 <see cref="EnvironmentSnapshot"/> 格式化為「關於」頁顯示用的 Markdown 文字。
        /// </summary>
        public static string FormatAsMarkdown(EnvironmentSnapshot s)
        {
            var sb = new StringBuilder();
            sb.AppendLine("**OmniConsole Environment**");
            sb.AppendLine();
            sb.AppendLine($"_Captured: {s.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}_");
            sb.AppendLine();
            sb.AppendLine("**OmniConsole Suite**");
            sb.AppendLine();
            sb.AppendLine($"- OmniConsole: {s.Versions.OmniConsole}");
            sb.AppendLine($"- PhantomBridge: {s.Versions.PhantomBridge}");
            sb.AppendLine($"- PhantomKey: {s.Versions.PhantomKey} (deployed: {s.Versions.PhantomKeyDeployed})");
            sb.AppendLine($"- PhantomLink (OmniCharm): {s.Versions.PhantomLink}");
            sb.AppendLine();
            sb.AppendLine("**PhantomKey Health**");
            sb.AppendLine();
            if (!s.PhantomKey.ProcessRunning)
            {
                sb.AppendLine("- Process: not running");
            }
            else
            {
                sb.AppendLine($"- Process: running (PID {s.PhantomKey.ProcessId})");
                sb.AppendLine($"- Path: {(s.PhantomKey.ExecutablePathExpected ? "expected location" : "unexpected location")}");
                sb.AppendLine($"- Uptime: {FormatUptime(s.PhantomKey.Uptime)}");
                sb.AppendLine($"- Integrity Level: {s.PhantomKey.IntegrityLevel}");
                sb.AppendLine($"- Responsiveness: {FormatResponsiveness(s.PhantomKey)}");
            }
            sb.AppendLine();
            sb.AppendLine("**XFSET (Xbox Full Screen Experience Tool)**");
            sb.AppendLine();
            sb.AppendLine($"- XboxFullScreenExperienceTool.exe: {FormatXfsetTool(s.Xfset)}");
            sb.AppendLine($"- PhysPanelCS.exe: {FormatPhysPanel(s.Xfset)}");
            sb.AppendLine();
            sb.AppendLine("**Hardware**");
            sb.AppendLine();
            sb.AppendLine($"- Model: {s.Hardware.SystemManufacturer} / {s.Hardware.SystemProductName}");
            sb.AppendLine($"- Motherboard: {s.Hardware.BaseboardManufacturer} / {s.Hardware.BaseboardProduct}");
            sb.AppendLine($"- CPU: {s.Hardware.CpuName} ({FormatMhz(s.Hardware.CpuMhz)}, {s.Hardware.CpuPhysicalCores}C/{s.Hardware.CpuLogicalCores}T)");
            sb.AppendLine($"- RAM: {FormatBytes(s.Hardware.RamTotalBytes)}");
            if (s.Hardware.Gpus.Count == 0)
            {
                sb.AppendLine($"- GPU: {Unknown}");
            }
            else
            {
                foreach (var g in s.Hardware.Gpus)
                {
                    sb.AppendLine($"- GPU: {g.Name} ({FormatBytes(g.VramBytes)} VRAM, driver {g.DriverVersion} / {g.DriverDate})");
                }
            }
            sb.AppendLine();
            sb.AppendLine("**System**");
            sb.AppendLine();
            sb.AppendLine($"- OS: {s.WindowsBuild}");
            sb.AppendLine($"- FSE: {s.FseState}");
            sb.AppendLine($"- DeviceForm: {s.DeviceForm}");
            sb.AppendLine($"- MaxTouchPoints: {s.MaxTouchPoints}{(s.MaxTouchPoints == 0 ? " (no touch screen)" : "")}");
            sb.AppendLine($"- Locale: {s.Locale}");
            sb.AppendLine($"- CountryRegion: {s.CountryRegion}");
            sb.AppendLine($"- DeviceRegion: {s.DeviceRegion}");
            return sb.ToString();
        }

        // ── 版本讀取 ────────────────────────────────────────────────────

        /// <summary>
        /// 讀取指定檔案的 FileVersion 字串。檔案不存在回 NotInstalled、其他錯誤回 Unknown。
        /// </summary>
        private static string ReadFileVersion(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    DebugLogger.Log($"[AboutInfoService] ReadFileVersion: file not found: {path}");
                    return NotInstalled;
                }
                var ver = FileVersionInfo.GetVersionInfo(path).FileVersion ?? Unknown;
                DebugLogger.Log($"[AboutInfoService] ReadFileVersion: {path} = {ver}");
                return ver;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] ReadFileVersion failed for {path}: {ex.Message}");
                return Unknown;
            }
        }

        /// <summary>
        /// 從 PackageManager 取得已安裝 PhantomLink 套件的 Package.Id.Version 字串（Major.Minor.Build.Revision）。
        /// </summary>
        private static string GetPhantomLinkVersion()
        {
            try
            {
                var pm = new PackageManager();
                var pkg = pm.FindPackagesForUser("", UpdateCheckService.PhantomLinkFamilyName).FirstOrDefault();
                if (pkg == null)
                {
                    DebugLogger.Log("[AboutInfoService] GetPhantomLinkVersion: package not found");
                    return NotInstalled;
                }
                var v = pkg.Id.Version;
                var ver = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
                DebugLogger.Log($"[AboutInfoService] GetPhantomLinkVersion: {ver}, InstalledLocation={pkg.InstalledLocation.Path}");
                return ver;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetPhantomLinkVersion failed: {ex.Message}");
                return Unknown;
            }
        }

        // ── 硬體資訊收集 ────────────────────────────────────────────────

        private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
        private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        /// <summary>
        /// 收集主機 / 主機板 / CPU / RAM / GPU 資訊，組成 <see cref="HardwareInfo"/>。
        /// </summary>
        private static HardwareInfo GetHardwareInfo()
        {
            string sysMfg = ReadRegString(Registry.LocalMachine, BiosKey, "SystemManufacturer");
            string sysProduct = ReadRegString(Registry.LocalMachine, BiosKey, "SystemProductName");
            string boardMfg = ReadRegString(Registry.LocalMachine, BiosKey, "BaseBoardManufacturer");
            string boardProduct = ReadRegString(Registry.LocalMachine, BiosKey, "BaseBoardProduct");
            string cpuName = ReadRegString(Registry.LocalMachine, CpuKey, "ProcessorNameString").Trim();
            int cpuMhz = ReadRegInt(Registry.LocalMachine, CpuKey, "~MHz");

            ulong ramBytes = 0;
            try
            {
                var mem = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(mem)) ramBytes = mem.ullTotalPhys;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GlobalMemoryStatusEx failed: {ex.Message}");
            }

            var gpus = GetGpus();
            int physicalCores = GetPhysicalCoreCount();

            DebugLogger.Log($"[AboutInfoService] Hardware: System='{sysMfg}/{sysProduct}', Board='{boardMfg}/{boardProduct}', CPU='{cpuName}' {cpuMhz}MHz {physicalCores}C/{Environment.ProcessorCount}T, RAM={ramBytes}, GPU count={gpus.Count}");
            foreach (var g in gpus)
            {
                DebugLogger.Log($"[AboutInfoService]   GPU '{g.Name}' VRAM={g.VramBytes}, Driver={g.DriverVersion} ({g.DriverDate})");
            }

            return new HardwareInfo(
                SystemManufacturer: sysMfg,
                SystemProductName: sysProduct,
                BaseboardManufacturer: boardMfg,
                BaseboardProduct: boardProduct,
                CpuName: cpuName,
                CpuMhz: cpuMhz,
                CpuPhysicalCores: physicalCores,
                CpuLogicalCores: Environment.ProcessorCount,
                RamTotalBytes: ramBytes,
                Gpus: gpus);
        }

        // ── CPU 實體核心數 ────────────────────────────────────────────────

        /// <summary>
        /// 透過 GetLogicalProcessorInformationEx 取得 CPU 實體核心數；失敗時降級回傳邏輯核心數。
        /// </summary>
        private static int GetPhysicalCoreCount()
        {
            try
            {
                uint length = 0;

                GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref length);
                if (length == 0)
                {
                    DebugLogger.Log("[AboutInfoService] GetLogicalProcessorInformationEx length=0, fallback to logical count");
                    return Environment.ProcessorCount;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref length))
                    {
                        DebugLogger.Log($"[AboutInfoService] GetLogicalProcessorInformationEx failed err={Marshal.GetLastWin32Error()}");
                        return Environment.ProcessorCount;
                    }

                    int cores = 0;
                    IntPtr p = buffer;
                    IntPtr end = IntPtr.Add(buffer, (int)length);
                    while ((long)p < (long)end)
                    {
                        uint size = (uint)Marshal.ReadInt32(p, 4);
                        if (size == 0) break;
                        cores++;
                        p = IntPtr.Add(p, (int)size);
                    }
                    return cores > 0 ? cores : Environment.ProcessorCount;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetPhysicalCoreCount failed: {ex.Message}");
                return Environment.ProcessorCount;
            }
        }

        /// <summary>
        /// GetLogicalProcessorInformationEx 的 RelationshipType 列舉。
        /// </summary>
        private enum LOGICAL_PROCESSOR_RELATIONSHIP : uint
        {
            RelationProcessorCore = 0,
            RelationNumaNode = 1,
            RelationCache = 2,
            RelationProcessorPackage = 3,
            RelationGroup = 4,
            RelationAll = 0xFFFF,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            LOGICAL_PROCESSOR_RELATIONSHIP RelationshipType,
            IntPtr Buffer,
            ref uint ReturnedLength);

        /// <summary>
        /// 讀取 Registry 字串值；找不到、空字串或例外時回傳 Unknown。
        /// </summary>
        private static string ReadRegString(RegistryKey root, string subKey, string name)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return (key?.GetValue(name) as string)?.Trim() is { Length: > 0 } v ? v : Unknown;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] ReadRegString {subKey}\\{name} failed: {ex.Message}");
                return Unknown;
            }
        }

        /// <summary>
        /// 讀取 Registry DWORD 值；找不到或例外時回傳 0。
        /// </summary>
        private static int ReadRegInt(RegistryKey root, string subKey, string name)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return key?.GetValue(name) is int i ? i : 0;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] ReadRegInt {subKey}\\{name} failed: {ex.Message}");
                return 0;
            }
        }

        // ── GPU 偵測（取主顯示卡名稱與 VRAM） ────────

        /// <summary>
        /// 透過 DXGI Factory1 列舉所有非軟體 GPU 介面卡，取名稱、VRAM 與驅動程式資訊；
        /// 同名同 VRAM 視為重複僅收一次。
        /// </summary>
        private static IReadOnlyList<GpuInfo> GetGpus()
        {
            var result = new List<GpuInfo>();
            IntPtr factoryPtr = IntPtr.Zero;
            try
            {
                var iidFactory = new Guid(IID_IDXGIFactory1);
                int hr = CreateDXGIFactory1(ref iidFactory, out factoryPtr);
                if (hr < 0 || factoryPtr == IntPtr.Zero)
                {
                    DebugLogger.Log($"[AboutInfoService] CreateDXGIFactory1 failed hr=0x{hr:X8}");
                    return result;
                }

                IntPtr vtable = Marshal.ReadIntPtr(factoryPtr);
                IntPtr enumAdapters1Slot = Marshal.ReadIntPtr(vtable, 12 * IntPtr.Size);
                var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Slot);

                var seenKeys = new HashSet<(string Name, ulong Vram)>();
                for (uint i = 0; ; i++)
                {
                    IntPtr adapterPtr = IntPtr.Zero;
                    hr = enumAdapters1(factoryPtr, i, out adapterPtr);
                    if (hr < 0 || adapterPtr == IntPtr.Zero) break;

                    try
                    {
                        IntPtr adapterVtable = Marshal.ReadIntPtr(adapterPtr);
                        IntPtr getDesc1Slot = Marshal.ReadIntPtr(adapterVtable, 10 * IntPtr.Size);
                        var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Slot);

                        var desc = new DXGI_ADAPTER_DESC1();
                        if (getDesc1(adapterPtr, ref desc) < 0) continue;

                        const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
                        if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;

                        string name = desc.Description?.Trim() ?? Unknown;
                        ulong vram = desc.DedicatedVideoMemory.ToUInt64();
                        if (!seenKeys.Add((name, vram))) continue;

                        var (drvVer, drvDate) = GetGpuDriverInfo(name);
                        result.Add(new GpuInfo(name, vram, drvVer, drvDate));
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetGpus failed: {ex.Message}");
            }
            finally
            {
                if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            }
            return result;
        }

        private const string IID_IDXGIFactory1 = "770aae78-f26f-4dba-a829-253c83d1b387";

        [DllImport("dxgi.dll", PreserveSig = true)]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int EnumAdapters1Delegate(IntPtr self, uint adapter, out IntPtr ppAdapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDesc1Delegate(IntPtr self, ref DXGI_ADAPTER_DESC1 pDesc);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        // ── GPU 驅動程式版本/日期 ────────────────────────────────────────

        /// <summary>
        /// 從 Display 類別 Registry 鍵取對應 GPU 的 DriverVersion 與 DriverDate；
        /// 找不到精確比對時退回第一筆有版本的子鍵。
        /// </summary>
        private static (string Version, string Date) GetGpuDriverInfo(string gpuName)
        {
            const string displayClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(displayClassPath);
                if (classKey == null) return (Unknown, Unknown);

                string? matchVersion = null;
                string? matchDate = null;
                string? firstVersion = null;
                string? firstDate = null;

                foreach (var sub in classKey.GetSubKeyNames())
                {
                    if (!int.TryParse(sub, out _)) continue;
                    using var subKey = classKey.OpenSubKey(sub);
                    if (subKey == null) continue;

                    var driverDesc = subKey.GetValue("DriverDesc") as string;
                    var driverVersion = subKey.GetValue("DriverVersion") as string;
                    var driverDate = subKey.GetValue("DriverDate") as string;

                    if (string.IsNullOrEmpty(driverVersion)) continue;

                    firstVersion ??= driverVersion;
                    firstDate ??= driverDate;

                    if (!string.IsNullOrEmpty(driverDesc) &&
                        !string.IsNullOrEmpty(gpuName) &&
                        driverDesc.Equals(gpuName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchVersion = driverVersion;
                        matchDate = driverDate;
                        break;
                    }
                }

                return (
                    matchVersion ?? firstVersion ?? Unknown,
                    matchDate ?? firstDate ?? Unknown);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetGpuDriverInfo failed: {ex.Message}");
                return (Unknown, Unknown);
            }
        }

        // ── 格式化輔助方法 ───────────────────────────────────────────────

        /// <summary>
        /// 把位元組數格式化為可讀字串（≥1 GiB 用 GB，否則用 MB）；0 視為未知回 Unknown。
        /// </summary>
        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return Unknown;
            const double GiB = 1024.0 * 1024.0 * 1024.0;
            double gib = bytes / GiB;
            return gib >= 1.0
                ? gib.ToString("0.# GB", CultureInfo.InvariantCulture)
                : (bytes / (1024.0 * 1024.0)).ToString("0 MB", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 將 CPU 頻率（MHz）格式化為字串：≥1000 顯示 GHz，否則顯示 MHz；非正值回 Unknown。
        /// </summary>
        private static string FormatMhz(int mhz)
        {
            if (mhz <= 0) return Unknown;
            return mhz >= 1000
                ? (mhz / 1000.0).ToString("0.00 GHz", CultureInfo.InvariantCulture)
                : $"{mhz} MHz";
        }

        /// <summary>
        /// 把 Uptime TimeSpan 格式化為「Xd Yh Zm」風格，依量級裁切顯示精度。
        /// </summary>
        private static string FormatUptime(TimeSpan ts)
        {
            if (ts <= TimeSpan.Zero) return Unknown;
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            if (ts.TotalHours >= 1) return $"{ts.Hours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }

        /// <summary>
        /// PhantomKey 健康分級的描述（含延遲毫秒）。
        /// </summary>
        private static string FormatResponsiveness(PhantomKeyHealth h)
        {
            return h.Responsiveness switch
            {
                PhantomKeyResponsiveness.Responsive => $"responsive ({h.PingLagMs}ms)",
                PhantomKeyResponsiveness.Busy => $"busy ({h.PingLagMs}ms)",
                PhantomKeyResponsiveness.Stuck => $"stuck ({h.PingLagMs}ms — main loop not advancing)",
                PhantomKeyResponsiveness.Hung => "not responding (SendMessageTimeout failed)",
                PhantomKeyResponsiveness.NoPingWindow => "no ping window (older PhantomKey or just started)",
                PhantomKeyResponsiveness.NotRunning => "not running",
                _ => Unknown,
            };
        }

        // ── XFSET touchservice 偵測（檢查 master mutex） ────────────────

        // PhysPanelCS 採 Master/Worker 架構，Master 在 SYSTEM session 0 跑、
        // 啟動時用 CreateMutexW 拿名稱為 "Global\XFEST_TouchSvc_Master_Lock" 的命名 mutex；
        // 只要 master 還活著、touchservice 就在運作中（worker 透過此 mutex 偵測 master 存活，
        // master 退出則 mutex 自動釋放）。
        //
        // 用 OpenMutexW(SYNCHRONIZE) 試開該 mutex —— Global\ 前綴 + SYNCHRONIZE 權限。
        //
        // PhysPanelCPP/TouchManager.cpp::MASTER_MUTEX_NAME

        private const uint MUTEX_SYNCHRONIZE = 0x00100000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenMutexW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 試開 PhysPanelCS master mutex 來判斷 touchservice 是否在跑：
        /// 開成功 → Running；ERROR_FILE_NOT_FOUND → NotConfigured；ERROR_ACCESS_DENIED → Running（物件存在）；其他 → Unknown。
        /// </summary>
        private static TouchServiceState QueryTouchServiceState()
        {
            try
            {
                IntPtr h = OpenMutexW(MUTEX_SYNCHRONIZE, false, PhysPanelMasterMutexName);
                if (h != IntPtr.Zero)
                {
                    CloseHandle(h);
                    DebugLogger.Log($"[AboutInfoService] TouchService: master mutex '{PhysPanelMasterMutexName}' open OK → Running");
                    return TouchServiceState.Running;
                }

                int err = Marshal.GetLastWin32Error();

                // 區分「物件不存在」與「物件存在但 ACL 拒絕」：
                //  - ERROR_FILE_NOT_FOUND (2) = 命名物件根本不存在 → master 沒跑 → NotConfigured
                //  - ERROR_ACCESS_DENIED (5) = 物件存在但 ACL 拒絕（master 由 SYSTEM 建立，
                //    預設 ACL 不放給封裝 App 的 LowIL token）。這仍是「物件存在」的證據。
                if (err == 2)
                {
                    DebugLogger.Log("[AboutInfoService] TouchService: master mutex not found (ERROR_FILE_NOT_FOUND) → NotConfigured");
                    return TouchServiceState.NotConfigured;
                }
                if (err == 5)
                {
                    DebugLogger.Log("[AboutInfoService] TouchService: master mutex exists but ACL denied (ERROR_ACCESS_DENIED) → Running");
                    return TouchServiceState.Running;
                }

                // 其他未預期的錯誤保守判讀為 Unknown
                DebugLogger.Log($"[AboutInfoService] TouchService: OpenMutexW failed with Win32 error {err} → Unknown");
                return TouchServiceState.Unknown;
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] QueryTouchServiceState failed: {ex.Message}");
                return TouchServiceState.Unknown;
            }
        }

        // ── 環境資訊 ────────────────────────────────────────────────────

        /// <summary>
        /// 從 Registry 組出「ProductName DisplayVersion (Build N.UBR)」字串；
        /// CurrentBuildNumber ≥ 22000 時把 ProductName 中的「Windows 10」覆寫為「Windows 11」。
        /// </summary>
        private static string GetWindowsBuild()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                if (key == null)
                {
                    DebugLogger.Log("[AboutInfoService] GetWindowsBuild: registry key null");
                    return Unknown;
                }

                var productName = key.GetValue("ProductName") as string ?? "Windows";
                var displayVersion = key.GetValue("DisplayVersion") as string;
                var build = key.GetValue("CurrentBuildNumber") as string;
                var ubr = key.GetValue("UBR");

                DebugLogger.Log($"[AboutInfoService] GetWindowsBuild raw: productName='{productName}', displayVersion='{displayVersion}', build='{build}', ubr='{ubr}'");

                // 由 build 號判斷實際主版本，覆寫 ProductName 中過時的 "Windows 10" 字眼
                // CurrentBuildNumber >= 22000 即為 Windows 11
                string osLabel = productName;
                if (int.TryParse(build, out int buildNum))
                {
                    if (buildNum >= 22000)
                        osLabel = productName.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
                }

                var sb = new StringBuilder();
                sb.Append(osLabel);
                if (!string.IsNullOrEmpty(displayVersion))
                    sb.Append(' ').Append(displayVersion);
                if (!string.IsNullOrEmpty(build))
                {
                    sb.Append(" (Build ").Append(build);
                    if (ubr is int ubrInt)
                        sb.Append('.').Append(ubrInt);
                    sb.Append(')');
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[AboutInfoService] GetWindowsBuild failed: {ex.Message}");
                return Unknown;
            }
        }

        /// <summary>
        /// 把 FseService 三個獨立訊號（IsSupported / IsHandheldFseAvailable / IsActive）合併為一行診斷字串。
        /// </summary>
        private static string GetFseStateText()
        {
            // 三個獨立訊號全部報出，便於診斷：
            //  - IsSupported = OS 是否支援 FSE
            //  - IsHandheldFseAvailable = 是否為「掌機完整版 FSE」（原生掌機 FSE 或 XFSET 已啟用）
            //  - IsActive = 此刻 OmniConsole 是否在 FSE 中執行
            try
            {
                bool supported = FseService.IsSupported();
                bool handheld = FseService.IsHandheldFseAvailable();
                bool active = FseService.IsActive();
                return $"Supported={(supported ? "yes" : "no")}, Handheld={(handheld ? "yes" : "no")}, Active={(active ? "yes" : "no")}";
            }
            catch
            {
                return Unknown;
            }
        }

        /// <summary>
        /// 讀 HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\OEM\DeviceForm；同時顯示 16 進位與 10 進位（46 = 已套 XFSET 或原生 OEM 掌機）。
        /// </summary>
        private static string GetDeviceFormText()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\OEM");
                return key?.GetValue("DeviceForm") is int form
                    ? $"0x{form:X2} ({form})"
                    : Unknown;
            }
            catch
            {
                return Unknown;
            }
        }

        // ── XFSET Markdown 格式化 ───────────────────────────────────────

        /// <summary>
        /// 把 XfsetInfo.Tool* 欄位格式化成「installed (版本)」或「not installed」。
        /// </summary>
        private static string FormatXfsetTool(XfsetInfo x)
        {
            return x.ToolInstalled
                ? $"installed ({x.ToolVersion})"
                : "not installed";
        }

        /// <summary>
        /// 把 XfsetInfo.PhysPanel* 與 TouchService 欄位格式化為單行字串（安裝狀態 + touchservice 執行狀態）。
        /// </summary>
        private static string FormatPhysPanel(XfsetInfo x)
        {
            if (!x.PhysPanelInstalled) return "not installed";

            string touchState = x.TouchService switch
            {
                TouchServiceState.Running => "touchservice running",
                TouchServiceState.NotConfigured => "touchservice not running",
                _ => "touchservice state unknown",
            };

            return $"installed ({x.PhysPanelVersion}), {touchState}";
        }
    }
}
