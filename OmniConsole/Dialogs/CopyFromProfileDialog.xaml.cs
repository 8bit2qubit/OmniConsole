using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniConsole.Dialogs
{
    /// <summary>「從其他程式讀入」對話方塊：用 AutoSuggestBox 篩選並選一個現有 profile，回傳其 AppId 供呼叫端複製 bindings。</summary>
    public sealed partial class CopyFromProfileDialog : GamepadDialogBase
    {
        private readonly ResourceLoader _resourceLoader = new();

        /// <summary>label → AppId 對照（建構期建好；只收有 AppId 的 profile，無 AppId 無法當複製來源）。</summary>
        private readonly List<(string Label, AppId Id)> _profiles = [];

        /// <summary>使用者選到的來源 profile AppId；取消為 null。</summary>
        public AppId? SelectedAppId { get; private set; }

        /// <summary>含 AutoSuggestBox，啟用螢幕鍵盤閃避（比照 PlatformEditDialog）。</summary>
        protected override bool EnableKeyboardAvoidanceOnOpen => true;

        /// <summary>建立讀入對話方塊；others 為「除了目前 profile 外」的其餘 profile 集合。</summary>
        public CopyFromProfileDialog(XamlRoot xamlRoot, IEnumerable<GamepadProfile> others)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;

            Title = _resourceLoader.Loc("GamepadMappingCopyFromTitle");
            PrimaryButtonText = _resourceLoader.Loc("GamepadKeyPickerOk");
            CloseButtonText = _resourceLoader.Loc("GamepadKeyPickerCancel");
            HintText.Text = _resourceLoader.Loc("GamepadMappingCopyFromHint");
            // 刻意不預選：只設 placeholder、不設 Text 初值。預選第一個會誤導：清空輸入框看似沒選、按確定卻套用，很危險。
            ProfileSuggestBox.PlaceholderText = _resourceLoader.Loc("GamepadMappingCopyFromPlaceholder");

            foreach (var p in others)
            {
                if (p.AppId == null) continue; // 無 AppId 無法當複製來源、不列入
                string label = !string.IsNullOrEmpty(p.DisplayName) ? p.DisplayName : (p.AppId.Value ?? string.Empty);
                // path-bound profile 後綴接資料夾名稱
                if (p.AppId.Kind == IdKind.Process && !string.IsNullOrEmpty(p.AppId.FullPath))
                {
                    string folder = AppId.ExtractFolderName(p.AppId.FullPath);
                    if (!string.IsNullOrEmpty(folder))
                        label = label + " · " + folder;
                }
                _profiles.Add((label, p.AppId));
            }

            // 依 label 字母排序，與 PlatformEditDialog 的封裝套件清單一致
            _profiles.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));

            ProfileSuggestBox.GotFocus += ProfileSuggestBox_GotFocus;
            ProfileSuggestBox.TextChanged += ProfileSuggestBox_TextChanged;
            ProfileSuggestBox.SuggestionChosen += ProfileSuggestBox_SuggestionChosen;

            PrimaryButtonClick += OnPrimary;
            Opened += OnOpened;
        }

        /// <summary>聚焦時顯示完整 profile 清單，讓使用者可直接瀏覽而無需先輸入。</summary>
        private void ProfileSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var box = (AutoSuggestBox)sender;
            box.ItemsSource = _profiles.Select(p => p.Label).ToList();
            box.IsSuggestionListOpen = true;
        }

        /// <summary>使用者輸入時即時過濾 profile 清單（label 子字串、不分大小寫）。</summary>
        private void ProfileSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            string query = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(query)
                ? _profiles.Select(p => p.Label).ToList()
                : _profiles
                    .Where(p => p.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .Select(p => p.Label)
                    .ToList();
        }

        /// <summary>選取建議項目後把文字補齊為完整 label，讓 OnPrimary 能精確比對。</summary>
        private void ProfileSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            string chosen = args.SelectedItem?.ToString() ?? "";
            var match = _profiles.FirstOrDefault(p => p.Label == chosen);
            if (match.Label != null)
                sender.Text = match.Label;
        }

        /// <summary>
        /// 確定按鈕：只以「目前文字精確比對 label」放行（清空/部分輸入/沒精確選到一律不套用）。
        /// 刻意不留任何「上次選過」的 fallback——否則清空輸入框看似沒選、按確定卻套用殘留值，會誤套到非預期的程式。
        /// </summary>
        private void OnPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string text = ProfileSuggestBox.Text?.Trim() ?? string.Empty;
            var match = _profiles.FirstOrDefault(p => p.Label == text);
            if (match.Label != null)
                SelectedAppId = match.Id;
            else
                args.Cancel = true;
        }

        /// <summary>對話方塊開啟：初始焦點到 AutoSuggestBox（排 dispatcher 待佈局完成）。手把導航與螢幕鍵盤閃避由 GamepadDialogBase 基底類別自動提供。</summary>
        private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
                ProfileSuggestBox.Focus(FocusStateHelper.Preferred));
        }
    }
}
