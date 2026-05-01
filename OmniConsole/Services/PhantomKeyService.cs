using System;
using System.Diagnostics;
using System.IO;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理 PhantomKey 背景手把輸入服務的啟動、停止與狀態查詢。
    /// PhantomKey 會自動偵測前景程式，將手把 View 按鈕映射為對應的鍵盤快速鍵。
    /// MSIX 沙箱內的程式無法正常使用 SendInput，
    /// 因此需複製到使用者目錄再啟動以脫離沙箱限制。
    /// </summary>
    public static class PhantomKeyService
    {
        private static readonly string _sourceExePath = Path.Combine(
            Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "Steam.exe");

        private static readonly string _targetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniConsole");

        private static readonly string _targetExePath = Path.Combine(_targetDir, "Steam.exe");

        /// <summary>套件內 PhantomKey 的完整路徑，供其他服務（如 AboutInfoService）讀版本號使用。</summary>
        public static string PackageExePath => _sourceExePath;

        /// <summary>使用者 LocalAppData 下實際執行的 PhantomKey 副本路徑，供 AboutInfoService 對比版本號使用。</summary>
        public static string DeployedExePath => _targetExePath;

        /// <summary>
        /// 從使用者目錄啟動 PhantomKey。
        /// 若 MSIX 套件內的版本較新則先覆蓋，確保更新後自動部署新版。
        /// 若已在執行中且健康（ping 通且主迴圈正在推進）則不重複啟動；
        /// 若在執行中但不健康（卡住、凍結、舊版無 ping window）則先終止再啟動（自癒）。
        /// </summary>
        public static void Start()
        {
            if (!File.Exists(_sourceExePath))
            {
                DebugLogger.Log($"[PhantomKeyService] .exe not found in package: {_sourceExePath}");
                return;
            }

            try
            {
                Directory.CreateDirectory(_targetDir);

                // 僅在套件版本與本機版本不同時複製（MSIX 更新後自動部署新版）
                if (NeedsCopy())
                {
                    // 舊版若仍在執行中會鎖定檔案，需先終止再覆蓋
                    Kill();
                    File.Copy(_sourceExePath, _targetExePath, overwrite: true);
                    DebugLogger.Log($"[PhantomKeyService] Copied to: {_targetExePath}");
                }
                else if (IsRunning())
                {
                    // 健康檢查：透過 ping window 量測主迴圈推進狀況
                    // 健康 → 跳過；不健康（卡住/凍結/舊版無 ping window）→ 終止並重啟（自癒）
                    var health = AboutInfoService.GetPhantomKeyHealth();
                    DebugLogger.Log($"[PhantomKeyService] Ping result: responsiveness={health.Responsiveness}, lag={health.PingLagMs}ms, uptime={health.Uptime}");

                    if (IsHealthyResponsiveness(health.Responsiveness))
                    {
                        DebugLogger.Log("[PhantomKeyService] Existing instance healthy, skipping start.");
                        return;
                    }

                    DebugLogger.Log($"[PhantomKeyService] Existing instance unhealthy ({health.Responsiveness}), self-healing: kill + restart.");
                    Kill();
                }

                Process.Start(new ProcessStartInfo(_targetExePath) { UseShellExecute = true });
                DebugLogger.Log($"[PhantomKeyService] Started: {_targetExePath}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PhantomKeyService] Start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 健康分級判定：Responsive / Busy 視為健康；Stuck / Hung / NoPingWindow / NotRunning 視為不健康。
        /// NoPingWindow 包含「使用者裝著不支援 ping 服務的舊版 PhantomKey」這類情境，藉此次啟動順帶完成版本汰換。
        /// </summary>
        private static bool IsHealthyResponsiveness(AboutInfoService.PhantomKeyResponsiveness r)
        {
            return r == AboutInfoService.PhantomKeyResponsiveness.Responsive
                || r == AboutInfoService.PhantomKeyResponsiveness.Busy;
        }

        /// <summary>
        /// 終止從使用者目錄執行的 .exe。
        /// </summary>
        public static void Kill()
        {
            foreach (var proc in Process.GetProcessesByName("Steam"))
            {
                try
                {
                    if (_targetExePath.Equals(proc.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        DebugLogger.Log($"[PhantomKeyService] Killing PID={proc.Id} Path={proc.MainModule.FileName}");
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"[PhantomKeyService] Kill failed for PID={proc.Id}: {ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        /// <summary>
        /// 比對套件內與使用者目錄的 .exe，判斷是否需要複製。
        /// </summary>
        private static bool NeedsCopy()
        {
            if (!File.Exists(_targetExePath))
            {
                DebugLogger.Log("[PhantomKeyService] NeedsCopy: target not found, need copy.");
                return true;
            }

            var sourceVer = FileVersionInfo.GetVersionInfo(_sourceExePath).FileVersion;
            var targetVer = FileVersionInfo.GetVersionInfo(_targetExePath).FileVersion;
            bool needsCopy = sourceVer != targetVer;
            DebugLogger.Log($"[PhantomKeyService] NeedsCopy: source={sourceVer}, target={targetVer}, needsCopy={needsCopy}");
            return needsCopy;
        }

        /// <summary>
        /// 檢查是否有從使用者目錄執行的 .exe 正在執行。
        /// </summary>
        public static bool IsRunning()
        {
            foreach (var proc in Process.GetProcessesByName("Steam"))
            {
                try
                {
                    if (_targetExePath.Equals(proc.MainModule?.FileName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
                finally
                {
                    proc.Dispose();
                }
            }
            return false;
        }
    }
}
