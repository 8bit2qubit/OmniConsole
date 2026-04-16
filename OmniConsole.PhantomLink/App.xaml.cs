using Microsoft.Gaming.XboxGameBar;
using System;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace OmniConsole.PhantomLink
{
    sealed partial class App : Application
    {
        private XboxGameBarWidget _phantomLinkWidget;

        /// <summary>
        /// 目前 Widget 實例（供 PhantomLinkWidget 做主題同步；
        /// Page 預設不跟隨 XboxGameBarWidget.RequestedTheme，須手動橋接）。
        /// </summary>
        public static XboxGameBarWidget CurrentWidget { get; private set; }

        // ── 生命週期與初始化 ─────────────────────────────────────────────────

        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        /// <summary>
        /// Game Bar 透過 ms-gamebarwidget: protocol 啟用本 widget；
        /// 僅在 IsLaunchActivation（首次啟動）時建立 Frame 並 Navigate 到 PhantomLinkWidget。
        /// </summary>
        protected override void OnActivated(IActivatedEventArgs args)
        {
            XboxGameBarWidgetActivatedEventArgs widgetArgs = null;
            if (args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as IProtocolActivatedEventArgs;
                if (protocolArgs != null && protocolArgs.Uri.Scheme.Equals("ms-gamebarwidget"))
                {
                    widgetArgs = args as XboxGameBarWidgetActivatedEventArgs;
                }
            }

            if (widgetArgs == null) return;

            if (widgetArgs.IsLaunchActivation)
            {
                var rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;

                _phantomLinkWidget = new XboxGameBarWidget(
                    widgetArgs,
                    Window.Current.CoreWindow,
                    rootFrame);
                CurrentWidget = _phantomLinkWidget;
                rootFrame.Navigate(typeof(PhantomLinkWidget));

                Window.Current.Closed += PhantomLinkWindow_Closed;
                Window.Current.Activate();
            }
        }

        // ── 生命週期清理 ─────────────────────────────────────────────────────

        /// <summary>
        /// 使用者關閉 Widget → 釋放參考，避免主題事件送到已銷毀的 Page。
        /// </summary>
        private void PhantomLinkWindow_Closed(object sender, Windows.UI.Core.CoreWindowEventArgs e)
        {
            _phantomLinkWidget = null;
            CurrentWidget = null;
            Window.Current.Closed -= PhantomLinkWindow_Closed;
        }

        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            _phantomLinkWidget = null;
            deferral.Complete();
        }
    }
}
