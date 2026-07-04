using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using OmniConsole.Services;
using OmniConsole.Startup;
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

            // 語言：PrimaryLanguageOverride 必須在「任何資源載入前」設定（故置於 Main 最早處）。
            // 太晚設的話，x:Uid 繫結的 PRI 資源已用啟動當下的語言解析完、來不及改（要重啟才正確）。
            // 偏好讀自共用設定（空＝跟隨系統）。此設定只影響官方語言的 PRI 解析（外掛語言另由翻譯層處理）。
            try
            {
                var uiLang = SettingsService.GetUiLanguage();
                // 覆寫設定會持久化：「跟隨系統」（空偏好）時必須主動設空字串「清除」殘留（非「不設」），否則回不到系統語言。
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = uiLang ?? string.Empty;
                DebugLogger.Log($"PrimaryLanguageOverride set early = '{uiLang}' (empty = cleared, follow system)");
            }
            catch (Exception ex) { DebugLogger.Log($"PrimaryLanguageOverride FAIL: {ex.Message}"); }

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

                        // edit-gamepad-profile 進設定頁前先暫存待編輯的 appId / displayName
                        if (ActivationRouter.IsEditGamepadProfile(protocolArgs.Uri.Host))
                            PendingEditProfileService.Stash(protocolArgs.Uri);

                        var route = ActivationRouter.RouteProtocol(protocolArgs.Uri.Host, uriStr);
                        DebugLogger.Log($"→ route = {route?.Kind.ToString() ?? "(null, unmatched)"}");
                        switch (route?.Kind)
                        {
                            case ActivationRouteKind.ShowSettings:
                                isSettingsEntry = true;
                                break;
                            case ActivationRouteKind.Passthrough:
                                DebugLogger.Log($"→ PASSTHROUGH to {route.Value.PassthroughUri}");
                                Windows.System.Launcher.LaunchUriAsync(new Uri(route.Value.PassthroughUri!)).AsTask().GetAwaiter().GetResult();
                                return 0;
                                // NormalLaunch / null（未匹配）：繼續往下走正常流程
                        }
                    }
                    else
                    {
                        DebugLogger.Log("Protocol args cast failed");
                    }
                }

                // 3. 「重啟後導向設定頁」旗標（更新流程或語言切換等重啟場景會設）。
                //    必須「無條件消費」（先讀後清、勿加 !isSettingsEntry 短路）：否則旗標殘留會污染下次啟動、誤進設定頁。
                bool pendingSettingsRestart = SettingsService.GetPendingSettingsRestart();
                if (pendingSettingsRestart)
                {
                    SettingsService.SetPendingSettingsRestart(false);
                    isSettingsEntry = true;
                    DebugLogger.Log("PendingSettingsRestart = True → settings entry (consumed)");
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
                // Settings 入口 (AUMID) → 手動發送 Protocol 訊號。
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

                    // edit-gamepad-profile 進設定頁前先暫存待編輯的 appId / displayName
                    if (ActivationRouter.IsEditGamepadProfile(protocolArgs.Uri.Host))
                        PendingEditProfileService.Stash(protocolArgs.Uri);

                    var route = ActivationRouter.RouteProtocol(protocolArgs.Uri.Host, uriStr);
                    DebugLogger.Log($"→ Redirect route = {route?.Kind.ToString() ?? "(null, unmatched)"}");
                    switch (route?.Kind)
                    {
                        case ActivationRouteKind.ShowSettings:
                            App.ShowSettingsFromRedirect();
                            return;
                        case ActivationRouteKind.Passthrough:
                            DebugLogger.Log($"→ Redirect PASSTHROUGH to {route.Value.PassthroughUri}");
                            App.PassthroughFromRedirect(route.Value.PassthroughUri!);
                            return;
                            // NormalLaunch / null（未匹配）：落到下方 ReactivateFromRedirect
                    }
                }
            }

            DebugLogger.Log("→ Redirect: ReactivateFromRedirect()");
            App.ReactivateFromRedirect();
        }
    }
}
