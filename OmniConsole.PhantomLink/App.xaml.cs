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
            // 語言：PrimaryLanguageOverride 必須在「任何資源載入前」設定（InitializeComponent 會載 App.xaml 資源），
            // 否則首次啟動 x:Uid PRI 用錯語言。故在建構式最早處設（讀共用 Shared.ini 偏好）。只影響官方語言的 PRI 解析。與主程式一致。
            try
            {
                var uiLang = Services.PhantomKeyStore.GetUiLanguage();
                if (!string.IsNullOrEmpty(uiLang))
                    Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = uiLang;
            }
            catch { }

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
                // 建立 UI 前套用語言（讀共用 Shared.ini 的主程式偏好；官方語言與外掛語言皆套）。
                ApplyUiLanguage();

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

        /// <summary>
        /// 套用 UI 語言：讀共用 Shared.ini 的主程式語言偏好（空＝跟系統），官方語言與外掛語言皆套用。
        /// widget 啟動與 LeavingBackground（回前景）都呼此法，達成「主程式改設定 widget 跟上」。
        /// 任一步驟失敗皆靜默降級回官方語言，不影響 widget 啟動。
        /// </summary>
        internal static void ApplyUiLanguage()
        {
            try
            {
                string lang = Services.PhantomKeyStore.GetUiLanguage();   // 空＝跟系統
                // PrimaryLanguageOverride 已在 App 建構式最早處設定（資源載入前），此處不重設、只處理外掛層。
                Services.PhantomLocalizer.Instance.LoadSharedFolder(lang);
                if (!string.IsNullOrEmpty(lang))
                    Services.PhantomLocalizer.Instance.SetLanguage(lang);
            }
            catch (Exception ex) { Services.DebugLogger.Log("[Localizer] widget ApplyUiLanguage FAIL: " + ex.Message); }
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
