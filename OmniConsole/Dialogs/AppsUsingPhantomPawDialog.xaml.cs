using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
    public sealed partial class AppsUsingPhantomPawDialog : GamepadDialog
    {
        private readonly ObservableCollection<LockingApp> _lockedApps = new();
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>建構：接收初始鎖住清單、設定按鈕文字、PrimaryButton 走重檢邏輯、Opened 設初始焦點到清單第一張卡片。</summary>
        public AppsUsingPhantomPawDialog(XamlRoot xamlRoot, IEnumerable<LockingApp> initialLockedApps)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            PrimaryButtonText = _resourceLoader.Loc("AppsUsingPhantomPawDialog_RetryButton");
            CloseButtonText = _resourceLoader.Loc("AppsUsingPhantomPawDialog_CancelButton");

            foreach (var app in initialLockedApps) _lockedApps.Add(app);
            LockedAppsList.ItemsSource = _lockedApps;

            PrimaryButtonClick += OnRetryClick;
            Opened += OnOpened;
        }

        /// <summary>對話方塊開啟：初始焦點落清單第一張卡片，讓使用者用 D-pad 逐個核實鎖住的 App、往下自然移到重試/取消。
        /// 用 FindFirstFocusableElement（容器 IsTabStop=False 會被跳過、命中第一張卡片 ContentControl），
        /// 不走 WinAppSDK 2.x 不可靠的 ItemsPanelRoot。清單空時找不到元素 → 焦點留給框架 DefaultButton。排 dispatcher 待佈局完成。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (FocusManager.FindFirstFocusableElement(LockedAppsList) is Control firstCard)
                    firstCard.Focus(FocusStateHelper.Preferred);
            });
        }

        /// <summary>每次容器產生或重用時，只在「最後一項」設 XYFocusDown 指向「重試」(Primary) 按鈕，其餘清掉讓
        /// ListView 自己往下跳下一項。否則最後一張卡片 Down 會走幾何最近落到「取消」而非主動作「重試」。
        /// （第一項 Up 不另設：比照貓又 Grid 三段式版面後、清單上方無可聚焦容器，directional focus 自然卡住。）</summary>
        private void LockedAppsList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.ItemContainer is not Control item) return;
            item.XYFocusDown = args.ItemIndex == _lockedApps.Count - 1 ? GetTemplateChild("PrimaryButton") : null;
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
