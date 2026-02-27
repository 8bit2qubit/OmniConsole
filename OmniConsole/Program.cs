using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace OmniConsole
{
    /// <summary>
    /// 自訂進入點，實現單一實例機制。
    /// 透過 LocalSettings 旗標區分「Settings 入口 → 設定」與「FSE/Game Bar → 自動啟動」。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static async Task<int> Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            // 偵測是否從「OmniConsole 設定」入口啟動
            bool isSettingsEntry = false;
            try
            {
                // 每個 Application 入口的 AppUserModelId 不同：
                // 主程式: ...!App    設定: ...!Settings
                var aumid = Windows.ApplicationModel.AppInfo.Current.AppUserModelId;
                isSettingsEntry = aumid.EndsWith("!Settings", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 若 API 不可用，預設為非設定入口
            }

            // 確認是否已有主實例
            var mainInstance = AppInstance.FindOrRegisterForKey("OmniConsole");

            if (!mainInstance.IsCurrent)
            {
                // 已有主實例 → 設定旗標（若為設定入口）→ 重導 → 退出
                if (isSettingsEntry)
                {
                    ApplicationData.Current.LocalSettings.Values["_ShowSettings"] = true;
                }

                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                await mainInstance.RedirectActivationToAsync(activationArgs);
                return 0;
            }

            // 這是主實例
            mainInstance.Activated += OnRedirectedActivation;

            // 若主實例本身就是從設定入口冷啟動的，也設定旗標
            if (isSettingsEntry)
            {
                ApplicationData.Current.LocalSettings.Values["_ShowSettings"] = true;
            }

            // 正常啟動 WinUI App
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });

            return 0;
        }

        /// <summary>
        /// 當其他實例的啟動被重導到這裡時觸發。
        /// 根據 LocalSettings 旗標決定顯示設定介面或重新啟動平台。
        /// </summary>
        private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
        {
            bool showSettings = false;
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.ContainsKey("_ShowSettings"))
                {
                    values.Remove("_ShowSettings");
                    showSettings = true;
                }
            }
            catch { }

            if (showSettings)
            {
                App.ShowSettingsFromRedirect();
            }
            else
            {
                App.ReactivateFromRedirect();
            }
        }
    }
}
