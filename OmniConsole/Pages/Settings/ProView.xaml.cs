using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Dialogs;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

namespace OmniConsole.Pages.Settings
{
    /// <summary>
    /// 設定的 Pro 分頁內容。未解鎖時提供贊助與輸入授權，已解鎖時顯示授權狀態與移除。
    /// 由 SettingsHostView 透過 Frame.Navigate 載入（切走即銷毀）；本頁不實作手把 scope，相關鍵分派仍在 SettingsHostView。
    /// </summary>
    public sealed partial class ProView : Page
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>主視窗 HWND，供系統檔案選擇器後備使用。由 SettingsHostView 於導覽後注入。</summary>
        private IntPtr Hwnd { get; set; }

        /// <summary>建立 Pro 分頁。</summary>
        public ProView()
        {
            InitializeComponent();
            ProTitleText.Text = _resourceLoader.Loc("ProTitle");
        }

        /// <summary>由 SettingsHostView 在導覽後注入主視窗 HWND。</summary>
        internal void SetHwnd(IntPtr hwnd) => Hwnd = hwnd;

        /// <summary>依目前授權狀態切換兩組版面並填入欄位，最後把焦點放到該狀態的第一顆可聚焦元素。</summary>
        /// <param name="preferredFocus">
        /// 希望聚焦的元素；該元素在本次狀態下不可見時退回預設目標。
        /// 逐項移除後原按鈕已不存在，呼叫端據此指定一個仍然在場的替代目標。
        /// </param>
        internal void Initialize(Control? preferredFocus = null)
        {
            RefreshState();
            Control target = preferredFocus != null && IsVisibleInTree(preferredFocus)
                ? preferredFocus
                : DefaultFocusTarget();
            DispatcherQueue.TryEnqueue(() => target.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>依目前授權狀態切換兩組版面、填入欄位並重建功能列，不動焦點。</summary>
        private void RefreshState()
        {
            LicenseService.LicenseInfo? license = LicenseService.Primary;
            bool unlocked = license != null;

            LockedGroup.Visibility = unlocked ? Visibility.Collapsed : Visibility.Visible;
            UnlockedGroup.Visibility = unlocked ? Visibility.Visible : Visibility.Collapsed;

            if (unlocked)
            {
                StatusEmailText.Text = license!.Value.Email;
                StatusSerialText.Text = "#" + license.Value.Serial;
                StatusIssuedText.Text = license.Value.Issued.ToLocalTime().ToString(
                    "yyyy-MM-dd HH:mm:ss zzz",
                    CultureInfo.InvariantCulture);
            }

            ShowRejectedNote(unlocked);
            BuildEntitlementRows();
        }

        /// <summary>
        /// 未解鎖但磁碟上留著一份已吊銷的授權時，在解鎖卡片上說明原因。
        /// 只認吊銷這一種：檔案損毀與版號超出的語意完全不同，一律顯示「已停用」會冤枉檔案剛好壞掉的人。
        /// </summary>
        private void ShowRejectedNote(bool unlocked)
        {
            bool revoked = !unlocked
                && LicenseService.RejectedReason == LicenseService.LicenseError.Revoked;

            if (revoked)
                ProRejectedNoteText.Text = _resourceLoader.Loc(
                    LicenseService.ErrorResourceKey(LicenseService.LicenseError.Revoked));

            ProRejectedNoteText.Visibility = revoked ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>該狀態下沒有更好選擇時的落腳處。成功啟用後刻意不落在有破壞性的移除按鈕上。</summary>
        private Control DefaultFocusTarget()
            => UnlockedGroup.Visibility == Visibility.Visible ? StatusCard : ProSponsorButton;

        /// <summary>元素本身與其各層父容器是否都未被收合。</summary>
        private static bool IsVisibleInTree(DependencyObject element)
        {
            for (DependencyObject? node = element; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement ui && ui.Visibility == Visibility.Collapsed)
                    return false;
            return true;
        }

        /// <summary>
        /// 逐項列出本版認得的功能與擁有狀態，已擁有者附移除按鈕。
        /// 以程式碼建列而非 DataTemplate 繫結，兩者在此無差異但前者不受 AOT 繫結限制。
        /// </summary>
        private void BuildEntitlementRows()
        {
            EntitlementRows.Children.Clear();

            foreach (LicenseService.Entitlement entitlement in LicenseService.AllEntitlements)
            {
                bool owned = LicenseService.HasEntitlement(entitlement);

                var row = new Grid { MinHeight = 32 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var icon = new FontIcon
                {
                    // 已擁有用勾號、未擁有用鎖頭。
                    Glyph = owned ? "\uE73E" : "\uE72E",
                    Style = (Style)Resources[owned ? "EntitlementOwnedIconStyle" : "EntitlementLockedIconStyle"],
                };
                Grid.SetColumn(icon, 0);

                var name = new TextBlock
                {
                    Text = _resourceLoader.Loc("ProEntitlement_" + entitlement),
                    Style = (Style)Resources["EntitlementNameStyle"],
                };
                Grid.SetColumn(name, 1);

                var status = new TextBlock
                {
                    Text = _resourceLoader.Loc(owned ? "ProEntitlement_Owned" : "ProEntitlement_NotOwned"),
                    Style = (Style)Resources["EntitlementStatusStyle"],
                };
                Grid.SetColumn(status, 2);

                row.Children.Add(icon);
                row.Children.Add(name);
                row.Children.Add(status);

                if (owned)
                {
                    var remove = new Button
                    {
                        Content = _resourceLoader.Loc("ProEntitlementRemove"),
                        Tag = entitlement,
                        FontSize = 14,
                        Padding = new Thickness(16, 4, 16, 4),
                        Margin = new Thickness(12, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    remove.Click += EntitlementRemove_Click;
                    Grid.SetColumn(remove, 3);
                    row.Children.Add(remove);
                }

                EntitlementRows.Children.Add(row);
            }
        }

        /// <summary>確認後移除單一功能項的授權。移除 Pro 會連帶讓附加功能失效，故另用一段說明。</summary>
        private async void EntitlementRemove_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not LicenseService.Entitlement entitlement) return;

            string bodyKey = entitlement == LicenseService.Entitlement.Pro
                ? "ProRemoveDialog_BodyPro"
                : "ProRemoveDialog_BodyOne";

            var confirm = new GamepadMessageDialog(
                XamlRoot,
                _resourceLoader.Loc("ProRemoveDialog_Title"),
                string.Format(_resourceLoader.Loc(bodyKey), _resourceLoader.Loc("ProEntitlement_" + entitlement)),
                _resourceLoader.Loc("ProRemoveDialog_Confirm"),
                _resourceLoader.Loc("ProRemoveDialog_Cancel"));
            await confirm.ShowAsync();

            if (confirm.Result)
            {
                // 焦點沿用清單的既有作法（同 GamepadProfileListView 刪除後的處理）：
                // 停在原本的位置，由遞補上來的那一項接手；越界就取最後一項，一項都不剩才往外跳。
                int removedIndex = RemoveButtons().FindIndex(b => (LicenseService.Entitlement)b.Tag! == entitlement);

                LicenseService.Remove(entitlement);
                RefreshState();

                var remaining = RemoveButtons();
                Control target = remaining.Count == 0
                    ? (IsVisibleInTree(ProAddLicenseButton) ? ProAddLicenseButton : DefaultFocusTarget())
                    : remaining[Math.Min(Math.Max(removedIndex, 0), remaining.Count - 1)];

                DispatcherQueue.TryEnqueue(() => target.Focus(FocusStateHelper.Preferred));

                // 版面與焦點就緒後才重啟 PhantomKey，讓它回到與新授權狀態相符的權限。
                await PhantomKeyService.ReconcileElevationAsync();
                return;
            }

            // 取消時該列未被重建，依權益重新找回同一顆按鈕還原焦點。
            DispatcherQueue.TryEnqueue(() =>
                (FindRemoveButton(entitlement) ?? ProAddLicenseButton).Focus(FocusStateHelper.Preferred));
        }

        /// <summary>功能列上的移除按鈕，依列的顯示順序排列。</summary>
        private List<Button> RemoveButtons()
        {
            var buttons = new List<Button>();
            foreach (UIElement child in EntitlementRows.Children)
                if (child is Grid row)
                    foreach (UIElement cell in row.Children)
                        if (cell is Button button && button.Tag is LicenseService.Entitlement)
                            buttons.Add(button);
            return buttons;
        }

        /// <summary>依權益找回功能列上的移除按鈕；該列不存在時回 null。</summary>
        private Button? FindRemoveButton(LicenseService.Entitlement entitlement)
            => RemoveButtons().Find(b => (LicenseService.Entitlement)b.Tag! == entitlement);

        /// <summary>開啟贊助頁。</summary>
        private async void ProSponsorButton_Click(object sender, RoutedEventArgs e)
            => await LicenseService.OpenSponsorPageAsync();

        /// <summary>
        /// 顯示授權輸入對話方塊，未解鎖與已解鎖兩種狀態共用（後者供附加功能與換發的金鑰進來）。
        /// 對話方塊按下匯入時會關閉自己，由此處協調顯示檔案選擇器後重新開啟，故以迴圈處理。
        /// </summary>
        private async void EnterLicense_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ProLicenseDialog(this.XamlRoot);
            ContentDialogResult result;

            while (true)
            {
                result = await dialog.ShowAsync();

                // 匯入按鈕會 Hide 自己，ShowAsync 因此回 None；判斷須看旗標而非 result。
                if (!dialog.RequestFilePicker) break;

                var pickerDialog = new FilePickerDialog(this.XamlRoot, dialog.FilePickerRequest!);
                var pickerResult = await pickerDialog.ShowAsync();

                string? selectedPath = null;
                if (pickerResult == ContentDialogResult.Primary)
                    selectedPath = pickerDialog.SelectedFilePath;
                else if (pickerDialog.RequestLegacyPicker)
                    selectedPath = await ShowLegacyFilePickerAsync(dialog.FilePickerRequest!);

                dialog.ApplyFilePickerResult(selectedPath);
            }

            if (result == ContentDialogResult.Primary && dialog.ResultInfo != null)
            {
                Initialize();
                await PhantomKeyService.ReconcileElevationAsync();
                return;
            }

            // 取消時還原焦點到觸發的那顆按鈕。依目前顯示中的那組挑具名元素。
            Control origin = UnlockedGroup.Visibility == Visibility.Visible
                ? ProAddLicenseButton
                : ProEnterKeyButton;
            DispatcherQueue.TryEnqueue(() => origin.Focus(FocusStateHelper.Preferred));
        }

        /// <summary>開啟系統 FileOpenPicker 作為舊式後備，回傳選取的檔案路徑或 null。</summary>
        private async Task<string?> ShowLegacyFilePickerAsync(FilePickerOptions options)
        {
            try
            {
                var picker = new FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Hwnd);
                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                foreach (var filter in options.FileTypeFilters)
                    picker.FileTypeFilter.Add(filter);

                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>確認後移除此裝置上的全部授權。</summary>
        private async void ProRemoveButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new GamepadMessageDialog(
                XamlRoot,
                _resourceLoader.Loc("ProRemoveDialog_Title"),
                _resourceLoader.Loc("ProRemoveDialog_Body"),
                _resourceLoader.Loc("ProRemoveDialog_Confirm"),
                _resourceLoader.Loc("ProRemoveDialog_Cancel"));
            await confirm.ShowAsync();

            if (confirm.Result)
            {
                LicenseService.RemoveAll();
                Initialize();
                await PhantomKeyService.ReconcileElevationAsync();
            }
            else
            {
                DispatcherQueue.TryEnqueue(() => ProRemoveButton.Focus(FocusStateHelper.Preferred));
            }
        }
    }
}
