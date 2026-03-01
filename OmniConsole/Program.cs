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

            // 偵測是否透過特定方式啟動（Settings 入口或 Protocol URIs）
            bool isSettingsEntry = false;
            var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();

            try
            {
                // 1. 檢查是否從「OmniConsole 設定」入口啟動 (AUMID)
                var aumid = Windows.ApplicationModel.AppInfo.Current.AppUserModelId;
                isSettingsEntry = aumid.EndsWith("!Settings", StringComparison.OrdinalIgnoreCase);

                // 2. 檢查是否透過 Protocol 啟動 (例如 Win+R omniconsole://show-settings)
                if (!isSettingsEntry && activationArgs.Kind == ExtendedActivationKind.Protocol)
                {
                    if (activationArgs.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
                    {
                        if (protocolArgs.Uri.Host == "show-settings")
                        {
                            isSettingsEntry = true;
                        }
                    }
                }
            }
            catch
            {
                // 若 API 不可用，維持現狀
            }

            // 確認是否已有主實例
            var mainInstance = AppInstance.FindOrRegisterForKey("OmniConsole");

            if (!mainInstance.IsCurrent)
            {
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
            if (args.Kind == ExtendedActivationKind.Protocol)
            {
                if (args.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs protocolArgs)
                {
                    if (protocolArgs.Uri.Host == "show-settings")
                    {
                        App.ShowSettingsFromRedirect();
                        return;
                    }
                }
            }

            App.ReactivateFromRedirect();
        }
    }
}
