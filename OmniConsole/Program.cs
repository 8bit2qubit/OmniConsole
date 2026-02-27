using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmniConsole
{
    /// <summary>
    /// 自訂進入點，實現單一實例機制。
    /// 從始功能表再次啟動時，重導到已存在的主實例並顯示設定介面。
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static async Task<int> Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            // 確認是否已有主實例
            var mainInstance = AppInstance.FindOrRegisterForKey("OmniConsole");

            if (!mainInstance.IsCurrent)
            {
                // 已有主實例 → 將啟動事件重導過去，然後退出
                var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                await mainInstance.RedirectActivationToAsync(activationArgs);
                return 0;
            }

            // 這是主實例 → 訂閱來自其他實例的重導事件
            mainInstance.Activated += OnRedirectedActivation;

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
        /// 通知 MainWindow 顯示設定介面。
        /// </summary>
        private static void OnRedirectedActivation(object? sender, AppActivationArguments args)
        {
            // 在 UI 執行緒上顯示設定介面
            App.ShowSettingsFromRedirect();
        }
    }
}
