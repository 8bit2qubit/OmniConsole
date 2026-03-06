using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmniConsole
{
    /// <summary>
    /// 自訂進入點，實現單一實例機制。
    /// 透過 AUMID 或 Protocol 區分「Settings 入口 → 設定」與「FSE/Game Bar → 自動啟動」。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static async Task<int> Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            Services.DebugLogger.Log("=== Main() started ===");

            // 偵測是否透過特定方式啟動（Settings 入口或 Protocol URIs）
            bool isSettingsEntry = false;
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
            Services.DebugLogger.Log($"ActivationKind = {activationArgs.Kind}");

            try
            {
                // 1. 檢查是否從「OmniConsole 設定」入口啟動 (AUMID)
                var aumid = Windows.ApplicationModel.AppInfo.Current.AppUserModelId;
                isSettingsEntry = aumid.EndsWith("!Settings", StringComparison.OrdinalIgnoreCase);
                Services.DebugLogger.Log($"AUMID = {aumid}, isSettingsEntry = {isSettingsEntry}");

                // 2. 檢查是否透過 Protocol 啟動 (例如 Win+R omniconsole://show-settings 或 Game Bar 按鈕)
                if (!isSettingsEntry && activationArgs.Kind == ExtendedActivationKind.Protocol)
                {
                    if (activationArgs.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
                    {
                        var uriStr = protocolArgs.Uri.ToString();
                        Services.DebugLogger.Log($"Protocol URI = {uriStr}");

                        if (protocolArgs.Uri.Host == "show-settings")
                        {
                            isSettingsEntry = true;
                            Services.DebugLogger.Log("→ show-settings matched");
                        }
                        // Game Bar 媒體櫃按鈕
                        else if (uriStr.Equals("windows.gaming:///library", StringComparison.OrdinalIgnoreCase))
                        {
                            bool libForSettings = Services.SettingsService.GetUseGameBarLibraryForSettings();
                            bool passthrough = Services.SettingsService.GetEnablePassthrough();
                            Services.DebugLogger.Log($"→ library matched. LibForSettings={libForSettings}, Passthrough={passthrough}");

                            // 優先順序 1：媒體櫃→設定介面
                            if (libForSettings)
                            {
                                isSettingsEntry = true;
                                Services.DebugLogger.Log("→ library → settings (priority 1)");
                            }
                            // 優先順序 2：Passthrough 到平台媒體櫃
                            else if (passthrough)
                            {
                                var platform = Services.SettingsService.GetDefaultPlatform();
                                Services.DebugLogger.Log($"→ platform={platform.Id}, LibraryUri={platform.LibraryUri ?? "(null)"}");
                                if (platform.LibraryUri != null)
                                {
                                    Services.DebugLogger.Log($"→ PASSTHROUGH to {platform.LibraryUri}");
                                    await Windows.System.Launcher.LaunchUriAsync(new Uri(platform.LibraryUri));
                                    Services.DebugLogger.Log("→ LaunchUriAsync completed");
                                    return 0;
                                }
                                Services.DebugLogger.Log("→ LibraryUri is null, fallthrough to normal");
                            }
                            // 優先順序 3：正常啟動流程（不做任何設定，繼續往下）
                        }
                        // Game Bar 首頁按鈕
                        else if (uriStr.Equals("windows.gaming:///home", StringComparison.OrdinalIgnoreCase))
                        {
                            bool passthrough = Services.SettingsService.GetEnablePassthrough();
                            Services.DebugLogger.Log($"→ home matched. Passthrough={passthrough}");

                            // Passthrough 到平台首頁
                            if (passthrough)
                            {
                                var platform = Services.SettingsService.GetDefaultPlatform();
                                Services.DebugLogger.Log($"→ platform={platform.Id}, HomeUri={platform.HomeUri ?? "(null)"}");
                                if (platform.HomeUri != null)
                                {
                                    Services.DebugLogger.Log($"→ PASSTHROUGH to {platform.HomeUri}");
                                    await Windows.System.Launcher.LaunchUriAsync(new Uri(platform.HomeUri));
                                    Services.DebugLogger.Log("→ LaunchUriAsync completed");
                                    return 0;
                                }
                                Services.DebugLogger.Log("→ HomeUri is null, fallthrough to normal");
                            }
                            // 無 HomeUri 或 Passthrough 關閉：正常啟動流程
                        }
                        else
                        {
                            Services.DebugLogger.Log($"→ URI not matched: {uriStr}");
                        }
                    }
                    else
                    {
                        Services.DebugLogger.Log("Protocol args cast failed");
                    }
                }

                // 3. 檢查是否為首次啟動或更新後的首次啟動
                if (!isSettingsEntry)
                {
                    isSettingsEntry = Services.SettingsService.IsFirstRunOrUpdate();
                    Services.DebugLogger.Log($"IsFirstRunOrUpdate = {isSettingsEntry}");
                }
            }
            catch (Exception ex)
            {
                Services.DebugLogger.Log($"EXCEPTION: {ex.Message}");
            }

            // 確認是否已有主實例
            var mainInstance = AppInstance.FindOrRegisterForKey("OmniConsole");
            Services.DebugLogger.Log($"IsCurrent = {mainInstance.IsCurrent}, isSettingsEntry = {isSettingsEntry}");

            if (!mainInstance.IsCurrent)
            {
                Services.DebugLogger.Log("→ Not current instance, redirecting...");
                // 已有主實例 → 重導訊號 → 退出
                // 注意：如果本身就是 Protocol 啟動，直接 Redirect 即可，OnRedirectedActivation 會處理
                // 如果是 Settings 入口啟動 (AUMID)，則手動發送 Protocol 訊號以利統一處理
                if (isSettingsEntry && activationArgs.Kind != ExtendedActivationKind.Protocol)
                {
                    var uri = new Uri("omniconsole://show-settings");
                    await Windows.System.Launcher.LaunchUriAsync(uri);
                }
                else
                {
                    await mainInstance.RedirectActivationToAsync(activationArgs);
                }
                return 0;
            }

            // 這是主實例
            mainInstance.Activated += OnRedirectedActivation;
            Services.DebugLogger.Log($"→ Main instance. Starting WinUI App (isSettingsEntry={isSettingsEntry})");

            // 正常啟動 WinUI App
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App(isSettingsEntry);
            });

            return 0;
        }

        /// <summary>
        /// 當其他實例的啟動被重導到這裡時觸發。
        /// 根據啟動參數 (Activation Arguments) 決定顯示設定介面或重新啟動平台。
        /// </summary>
        private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
        {
            Services.DebugLogger.Log($"=== OnRedirectedActivation: Kind={args.Kind} ===");

            if (args.Kind == ExtendedActivationKind.Protocol)
            {
                if (args.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protocolArgs)
                {
                    var uriStr = protocolArgs.Uri.ToString();
                    Services.DebugLogger.Log($"Redirect Protocol URI = {uriStr}");

                    if (protocolArgs.Uri.Host == "show-settings")
                    {
                        Services.DebugLogger.Log("→ Redirect: show-settings");
                        App.ShowSettingsFromRedirect();
                        return;
                    }
                    // Game Bar 媒體櫃按鈕
                    else if (uriStr.Equals("windows.gaming:///library", StringComparison.OrdinalIgnoreCase))
                    {
                        bool libForSettings = Services.SettingsService.GetUseGameBarLibraryForSettings();
                        bool passthrough = Services.SettingsService.GetEnablePassthrough();
                        Services.DebugLogger.Log($"→ Redirect library. LibForSettings={libForSettings}, Passthrough={passthrough}");

                        if (libForSettings)
                        {
                            Services.DebugLogger.Log("→ Redirect: library → settings");
                            App.ShowSettingsFromRedirect();
                            return;
                        }
                        else if (passthrough)
                        {
                            var platform = Services.SettingsService.GetDefaultPlatform();
                            Services.DebugLogger.Log($"→ Redirect platform={platform.Id}, LibraryUri={platform.LibraryUri ?? "(null)"}");
                            if (platform.LibraryUri != null)
                            {
                                Services.DebugLogger.Log($"→ Redirect PASSTHROUGH to {platform.LibraryUri}");
                                App.PassthroughFromRedirect(platform.LibraryUri);
                                return;
                            }
                        }
                    }
                    // Game Bar 首頁按鈕
                    else if (uriStr.Equals("windows.gaming:///home", StringComparison.OrdinalIgnoreCase))
                    {
                        bool passthrough = Services.SettingsService.GetEnablePassthrough();
                        Services.DebugLogger.Log($"→ Redirect home. Passthrough={passthrough}");

                        if (passthrough)
                        {
                            var platform = Services.SettingsService.GetDefaultPlatform();
                            Services.DebugLogger.Log($"→ Redirect platform={platform.Id}, HomeUri={platform.HomeUri ?? "(null)"}");
                            if (platform.HomeUri != null)
                            {
                                Services.DebugLogger.Log($"→ Redirect PASSTHROUGH to {platform.HomeUri}");
                                App.PassthroughFromRedirect(platform.HomeUri);
                                return;
                            }
                        }
                    }
                    else
                    {
                        Services.DebugLogger.Log($"→ Redirect URI not matched: {uriStr}");
                    }
                }
            }

            Services.DebugLogger.Log("→ Redirect: ReactivateFromRedirect()");
            App.ReactivateFromRedirect();
        }
    }
}
