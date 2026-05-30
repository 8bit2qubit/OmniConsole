using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 顯示「目前正鎖住 PhantomPaw dll 的應用程式清單」，並提供「重試／取消」兩動作。
    /// 重試會即時重新查詢、清單動態更新；清單變空時帶 Primary 結果關閉、呼叫端拿到 Primary 才繼續安裝流程。
    /// </summary>
    public sealed partial class AppsUsingPhantomPawDialog : ContentDialog
    {
        private GamepadNavigationService? _gamepadNav;
        private readonly ObservableCollection<LockingApp> _lockedApps = new();

        /// <summary>建構：接收初始鎖住清單、設定按鈕文字、掛上 Opened/Closed 事件、PrimaryButton 走重檢邏輯。</summary>
        public AppsUsingPhantomPawDialog(XamlRoot xamlRoot, IEnumerable<LockingApp> initialLockedApps)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            var loader = new ResourceLoader();
            PrimaryButtonText = loader.GetString("AppsUsingPhantomPawDialog_RetryButton");
            CloseButtonText = loader.GetString("AppsUsingPhantomPawDialog_CancelButton");

            foreach (var app in initialLockedApps) _lockedApps.Add(app);
            LockedAppsList.ItemsSource = _lockedApps;

            PrimaryButtonClick += OnRetryClick;
            Opened += OnOpened;
            Closed += OnClosed;
        }

        /// <summary>對話方塊開啟：啟動自帶手把輪詢（A=觸發焦點元素、B=關閉）。初始焦點落第一張卡片，無卡片時走 ContentDialog 預設。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: () => Hide());
            _gamepadNav.Start();

            DispatcherQueue.TryEnqueue(() =>
            {
                if (LockedAppsList.ItemsPanelRoot is { Children: { Count: > 0 } children }
                    && children[0] is Control firstCard)
                {
                    firstCard.Focus(FocusState.Programmatic);
                }
            });
        }

        /// <summary>對話方塊關閉：停止手把輪詢並釋放。</summary>
        private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
        }

        /// <summary>「重試」按下：重檢鎖住清單；仍鎖住則更新清單並 Cancel 留在對話方塊；已清空則允許帶 Primary 結果關閉。</summary>
        private void OnRetryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var pids = PhantomKeyService.GetProcessesLockingPawDlls();
            if (pids.Count == 0) return;

            args.Cancel = true;
            var apps = UpdateCheckService.ResolveLockingApps(pids);
            _lockedApps.Clear();
            foreach (var app in apps) _lockedApps.Add(app);
        }
    }
}
