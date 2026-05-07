using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Services;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 更新下載/安裝進度對話方塊。
    /// 無 Primary/Close 按鈕，B 鍵與 Esc 皆無作用，Closing 事件擋下所有關閉請求；
    /// 由呼叫端在更新流程結束（成功或失敗）後顯式呼叫 Hide。
    /// </summary>
    public sealed partial class UpdateProgressDialog : ContentDialog
    {
        private readonly ResourceLoader _resourceLoader;
        private GamepadNavigationService? _gamepadNav;
        private bool _allowClose;

        /// <summary>建立進度對話方塊。XamlRoot 由呼叫端注入。</summary>
        public UpdateProgressDialog(XamlRoot xamlRoot, ResourceLoader resourceLoader)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resourceLoader = resourceLoader;

            Title = resourceLoader.GetString("UpdateProgressDialog_Title");
            HintText.Text = resourceLoader.GetString("UpdateProgressDialog_Hint");
            StatusText.Text = resourceLoader.GetString("UpdateDownload_InProgress");
            ProgressText.Text = "0%";

            Opened += UpdateProgressDialog_Opened;
            Closed += UpdateProgressDialog_Closed;
            Closing += UpdateProgressDialog_Closing;
        }

        /// <summary>對話方塊開啟時啟動手把輪詢，A/B 兩鍵都不轉送。</summary>
        private void UpdateProgressDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // A 委派為 non-nullable 型別，傳 no-op；B 委派可為 null
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => { },
                onBButtonPressed: null);
            _gamepadNav.Start();
        }

        /// <summary>對話方塊關閉時停止手把輪詢。</summary>
        private void UpdateProgressDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
        }

        /// <summary>除呼叫端顯式 RequestClose() 之外，所有關閉請求一律拒絕。</summary>
        private void UpdateProgressDialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
        {
            if (!_allowClose) args.Cancel = true;
        }

        /// <summary>更新進度條（0~100，負值表示 indeterminate）。</summary>
        public void ReportProgress(double percent)
        {
            if (percent < 0)
            {
                ProgressBarControl.IsIndeterminate = true;
                ProgressText.Text = "";
            }
            else
            {
                ProgressBarControl.IsIndeterminate = false;
                ProgressBarControl.Value = percent;
                ProgressText.Text = $"{percent:F0}%";
            }
        }

        /// <summary>依階段識別字（Phase1Download/Phase1Install/Phase2Download/Phase2Install）更新狀態文字。</summary>
        public void ReportStatus(string phaseKey)
        {
            var resKey = phaseKey switch
            {
                "Phase1Download" => "UpdateProgress_Phase1Download",
                "Phase1Install" => "UpdateProgress_Phase1Install",
                "Phase2Download" => "UpdateProgress_Phase2Download",
                "Phase2Install" => "UpdateProgress_Phase2Install",
                // mainSkippable 路徑：未實際重裝主程式，只是請求重啟以套用 PhantomLink 安裝
                "Phase2RequestingRestart" => "UpdateProgress_Phase2Install",
                _ => "UpdateDownload_InProgress"
            };
            StatusText.Text = _resourceLoader.GetString(resKey);
        }

        /// <summary>由呼叫端顯式關閉對話方塊（更新成功或失敗後）。</summary>
        public void RequestClose()
        {
            _allowClose = true;
            Hide();
        }
    }
}
