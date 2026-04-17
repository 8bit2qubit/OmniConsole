using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using OmniConsole.Services;
using System;
using System.Threading;
using Windows.ApplicationModel.Activation;

namespace OmniConsole
{
    /// <summary>
    /// 自訂進入點，實現單一實例機制。
    /// 透過 AUMID 或 Protocol 區分「Settings 入口 → 設定」與「FSE/Game Bar → 自動啟動」。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            DebugLogger.Log("=== Main() started ===");

            // 偵測是否透過特定方式啟動（Settings 入口或 Protocol URIs）
            bool isSettingsEntry = false;
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            DebugLogger.Log($"ActivationKind = {activationArgs.Kind}");

            try
            {
                // 1. 檢查是否從「OmniConsole 設定」入口啟動 (AUMID)
                var aumid = Windows.ApplicationModel.AppInfo.Current.AppUserModelId;
                isSettingsEntry = aumid.EndsWith("!Settings", StringComparison.OrdinalIgnoreCase);
                DebugLogger.Log($"AUMID = {aumid}, isSettingsEntry = {isSettingsEntry}");

                // 2. 檢查是否透過 Protocol 啟動 (例如 Win+R omniconsole://show-settings 或 Game Bar 按鈕)
                if (!isSettingsEntry && activationArgs.Kind == ExtendedActivationKind.Protocol)
                {
                    if (activationArgs.Data is IProtocolActivatedEventArgs protocolArgs)
                    {
                        var uriStr = protocolArgs.Uri.ToString();
                        DebugLogger.Log($"Protocol URI = {uriStr}");

                        if (protocolArgs.Uri.Host == "show-settings")
                        {
                            isSettingsEntry = true;
                            DebugLogger.Log("→ show-settings matched");
                        }
                        // Game Bar 媒體櫃按鈕
                        else if (uriStr.Equals("windows.gaming:///library", StringComparison.OrdinalIgnoreCase))
                        {
                            bool libForSettings = SettingsService.GetUseGameBarLibraryForSettings();
                            bool passthrough = SettingsService.GetEnablePassthrough();
                            DebugLogger.Log($"→ library matched. LibForSettings={libForSettings}, Passthrough={passthrough}");

                            // 優先順序 1：媒體櫃→設定介面
                            if (libForSettings)
                            {
                                isSettingsEntry = true;
                                DebugLogger.Log("→ library → settings (priority 1)");
                            }
                            // 優先順序 2：Passthrough 到平台媒體櫃
                            else if (passthrough)
                            {
                                var platform = SettingsService.GetDefaultPlatform();
                                DebugLogger.Log($"→ platform={platform.Id}, LibraryUri={platform.LibraryUri ?? "(null)"}");
                                if (platform.LibraryUri != null)
                                {
                                    DebugLogger.Log($"→ PASSTHROUGH to {platform.LibraryUri}");
                                    Windows.System.Launcher.LaunchUriAsync(new Uri(platform.LibraryUri)).AsTask().GetAwaiter().GetResult();
                                    DebugLogger.Log("→ LaunchUriAsync completed");
                                    return 0;
                                }
                                DebugLogger.Log("→ LibraryUri is null, fallthrough to normal");
                            }
                            // 優先順序 3：正常啟動流程（不做任何設定，繼續往下）
                        }
                        // Game Bar 首頁按鈕
                        else if (uriStr.Equals("windows.gaming:///home", StringComparison.OrdinalIgnoreCase))
                        {
                            bool passthrough = SettingsService.GetEnablePassthrough();
                            DebugLogger.Log($"→ home matched. Passthrough={passthrough}");

                            // Passthrough 到平台首頁
                            if (passthrough)
                            {
                                var platform = SettingsService.GetDefaultPlatform();
                                DebugLogger.Log($"→ platform={platform.Id}, HomeUri={platform.HomeUri ?? "(null)"}");
                                if (platform.HomeUri != null)
                                {
                                    DebugLogger.Log($"→ PASSTHROUGH to {platform.HomeUri}");
                                    Windows.System.Launcher.LaunchUriAsync(new Uri(platform.HomeUri)).AsTask().GetAwaiter().GetResult();
                                    DebugLogger.Log("→ LaunchUriAsync completed");
                                    return 0;
                                }
                                DebugLogger.Log("→ HomeUri is null, fallthrough to normal");
                            }
                            // 無 HomeUri 或 Passthrough 關閉：正常啟動流程
                        }
                        else
                        {
                            DebugLogger.Log($"→ URI not matched: {uriStr}");
                        }
                    }
                    else
                    {
                        DebugLogger.Log("Protocol args cast failed");
                    }
                }

                // 3. PhantomLink 安裝完成後透過 RequestRestartAsync 重啟，導向設定頁
                if (!isSettingsEntry && SettingsService.GetPendingSettingsRestart())
                {
                    SettingsService.SetPendingSettingsRestart(false);
                    isSettingsEntry = true;
                    DebugLogger.Log("PendingSettingsRestart = True → settings entry");
                }

                // 4. 檢查是否為首次啟動或更新後的首次啟動
                if (!isSettingsEntry)
                {
                    isSettingsEntry = SettingsService.IsFirstRunOrUpdate();
                    DebugLogger.Log($"IsFirstRunOrUpdate = {isSettingsEntry}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"EXCEPTION: {ex.Message}");
            }

            // 確認是否已有主實例
            var mainInstance = AppInstance.FindOrRegisterForKey("OmniConsole");
            DebugLogger.Log($"IsCurrent = {mainInstance.IsCurrent}, isSettingsEntry = {isSettingsEntry}");

            if (!mainInstance.IsCurrent)
            {
                DebugLogger.Log("→ Not current instance, redirecting...");
                // 副實例：重導訊號給主實例後退出。
                // Protocol 啟動 → 直接 Redirect，OnRedirectedActivation 會處理。
                // Settings 入口 (AUMID) → 手動發送 Protocol 訊號以利統一處理。
                try
                {
                    if (isSettingsEntry && activationArgs.Kind != ExtendedActivationKind.Protocol)
                    {
                        var uri = new Uri($"{PlatformFieldValidator.OwnProtocolScheme}://show-settings");
                        Windows.System.Launcher.LaunchUriAsync(uri).AsTask().GetAwaiter().GetResult();
                    }
                    else
                    {
                        // 主實例若已退出，RedirectActivationToAsync 會無限期卡住，加 timeout 防殭屍
                        var redirectTask = mainInstance.RedirectActivationToAsync(activationArgs).AsTask();
                        if (!redirectTask.Wait(5000))
                            DebugLogger.Log("→ RedirectActivationToAsync timed out (main instance may have exited)");
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log($"→ Redirect failed: {ex.Message}");
                }
                // Environment.Exit(0) 確保副實例一定結束
                Environment.Exit(0);
            }

            // 這是主實例
            mainInstance.Activated += OnRedirectedActivation;
            DebugLogger.Log($"→ Main instance. Starting WinUI App (isSettingsEntry={isSettingsEntry})");

            // 正常啟動 WinUI App
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App(isSettingsEntry);
            });

            Environment.Exit(0);
            return 0; // unreachable，僅滿足編譯器
        }

        /// <summary>
        /// 當其他實例的啟動被重導到這裡時觸發。
        /// 根據啟動參數 (Activation Arguments) 決定顯示設定介面或重新啟動平台。
        /// </summary>
        private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
        {
            DebugLogger.Log($"=== OnRedirectedActivation: Kind={args.Kind} ===");

            if (args.Kind == ExtendedActivationKind.Protocol)
            {
                if (args.Data is IProtocolActivatedEventArgs protocolArgs)
                {
                    var uriStr = protocolArgs.Uri.ToString();
                    DebugLogger.Log($"Redirect Protocol URI = {uriStr}");

                    if (protocolArgs.Uri.Host == "show-settings")
                    {
                        DebugLogger.Log("→ Redirect: show-settings");
                        App.ShowSettingsFromRedirect();
                        return;
                    }
                    // Game Bar 媒體櫃按鈕
                    else if (uriStr.Equals("windows.gaming:///library", StringComparison.OrdinalIgnoreCase))
                    {
                        bool libForSettings = SettingsService.GetUseGameBarLibraryForSettings();
                        bool passthrough = SettingsService.GetEnablePassthrough();
                        DebugLogger.Log($"→ Redirect library. LibForSettings={libForSettings}, Passthrough={passthrough}");

                        if (libForSettings)
                        {
                            DebugLogger.Log("→ Redirect: library → settings");
                            App.ShowSettingsFromRedirect();
                            return;
                        }
                        else if (passthrough)
                        {
                            var platform = SettingsService.GetDefaultPlatform();
                            DebugLogger.Log($"→ Redirect platform={platform.Id}, LibraryUri={platform.LibraryUri ?? "(null)"}");
                            if (platform.LibraryUri != null)
                            {
                                DebugLogger.Log($"→ Redirect PASSTHROUGH to {platform.LibraryUri}");
                                App.PassthroughFromRedirect(platform.LibraryUri);
                                return;
                            }
                        }
                    }
                    // Game Bar 首頁按鈕
                    else if (uriStr.Equals("windows.gaming:///home", StringComparison.OrdinalIgnoreCase))
                    {
                        bool passthrough = SettingsService.GetEnablePassthrough();
                        DebugLogger.Log($"→ Redirect home. Passthrough={passthrough}");

                        if (passthrough)
                        {
                            var platform = SettingsService.GetDefaultPlatform();
                            DebugLogger.Log($"→ Redirect platform={platform.Id}, HomeUri={platform.HomeUri ?? "(null)"}");
                            if (platform.HomeUri != null)
                            {
                                DebugLogger.Log($"→ Redirect PASSTHROUGH to {platform.HomeUri}");
                                App.PassthroughFromRedirect(platform.HomeUri);
                                return;
                            }
                        }
                    }
                    else
                    {
                        DebugLogger.Log($"→ Redirect URI not matched: {uriStr}");
                    }
                }
            }

            DebugLogger.Log("→ Redirect: ReactivateFromRedirect()");
            App.ReactivateFromRedirect();
        }
    }
}
