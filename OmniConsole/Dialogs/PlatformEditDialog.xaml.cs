using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 新增/編輯使用者自訂平台的對話方塊。
    /// 傳入 <see langword="null"/> 為新增模式；傳入既有 <see cref="Models.UserPlatformEntry"/> 為編輯模式。
    /// 驗證通過後，結果項目存於 <see cref="ResultEntry"/>，待選圖示存於 <see cref="PendingIconFile"/>，
    /// 由 MainWindow 在 <c>ShowAsync</c> 返回後統一儲存。
    /// </summary>
    public sealed partial class PlatformEditDialog : GamepadDialog
    {
        private readonly ResourceLoader _resourceLoader;
        private readonly string _ownFamilyName;
        private readonly UserPlatformEntry? _existingEntry;

        private string? _pendingIconPath;
        private List<(string DisplayName, string PackageFamilyName)>? _packagedAppCache;
        private string _selectedPackageFamilyName;

        /// <summary>B 鍵不動作，避免誤觸關閉對話方塊遺失輸入（回 false = 不處理、不關閉）。</summary>
        public override bool OnB() => false;

        /// <summary>含 TextBox / AutoSuggestBox，啟用螢幕鍵盤閃避。</summary>
        protected override bool EnableKeyboardAvoidanceOnOpen => true;

        /// <summary>驗證通過後由對話方塊建立的平台項目，由呼叫端在 ShowAsync 返回後呼叫 <c>UserPlatformStore.Add</c> 儲存。</summary>
        public UserPlatformEntry? ResultEntry { get; private set; }

        /// <summary>使用者透過檔案選擇器選取的圖示路徑，由呼叫端轉換為 StorageFile 後匯入。</summary>
        public string? PendingIconPath => _pendingIconPath;

        /// <summary>瀏覽按鈕的目標類型。</summary>
        public enum BrowseTarget { None, Exe, Icon }

        /// <summary>是否需要開啟檔案選擇器（Browse 按鈕觸發 Hide 時設為 true）。</summary>
        public bool RequestFilePicker { get; private set; }

        /// <summary>檔案選擇器的設定選項。</summary>
        public FilePickerOptions? FilePickerRequest { get; private set; }

        /// <summary>目前的瀏覽目標（exe 或圖示）。</summary>
        public BrowseTarget ActiveBrowseTarget { get; private set; }

        /// <summary>
        /// 建立平台編輯對話方塊。
        /// 傳入 null 表示新增模式，傳入既有 entry 表示編輯模式。
        /// </summary>
        /// <param name="xamlRoot">呼叫端視窗的 XamlRoot，ContentDialog 顯示所需。</param>
        /// <param name="resourceLoader">在地化字串載入器。</param>
        /// <param name="existingEntry">既有平台項目（編輯模式）；null 為新增模式。</param>
        public PlatformEditDialog(XamlRoot xamlRoot, ResourceLoader resourceLoader,
                                  UserPlatformEntry? existingEntry = null)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resourceLoader = resourceLoader;
            _existingEntry = existingEntry;
            _ownFamilyName = Windows.ApplicationModel.Package.Current.Id.FamilyName;

            bool isEdit = existingEntry != null;
            bool isExecutable = existingEntry?.LaunchType == "Executable";
            bool isPackagedApp = existingEntry?.LaunchType == "PackagedApp";
            _selectedPackageFamilyName = existingEntry?.PackageFamilyName ?? "";

            // 在地化標題與按鈕
            Title = isEdit ? resourceLoader.GetString("PlatformDialog_EditTitle")
                           : resourceLoader.GetString("PlatformDialog_AddTitle");
            PrimaryButtonText = resourceLoader.GetString("PlatformDialog_Save");
            CloseButtonText = resourceLoader.GetString("PlatformDialog_Cancel");
            if (isEdit) SecondaryButtonText = resourceLoader.GetString("PlatformDialog_Delete");

            // 在地化靜態標籤
            NameLabel.Text = resourceLoader.GetString("PlatformDialog_NameLabel");
            LaunchTypeLabel.Text = resourceLoader.GetString("PlatformDialog_LaunchTypeLabel");
            ArgsLabel.Text = resourceLoader.GetString("PlatformDialog_ArgsLabel");
            PackagedAppLabel.Text = resourceLoader.GetString("PlatformDialog_PackagedAppLabel");
            PackagedAppWarning.Text = resourceLoader.GetString("PlatformDialog_PackagedAppWarning");
            PackagedAppSuggestBox.PlaceholderText = resourceLoader.GetString("PlatformDialog_PackagedAppPlaceholder");
            IconLabel.Text = resourceLoader.GetString("PlatformDialog_IconLabel");
            IconHint.Text = resourceLoader.GetString("PlatformDialog_IconHint");
            ConfigWarning.Text = resourceLoader.GetString("PlatformDialog_ConfigWarning");

            // ComboBox 選項（Protocol URI 不需在地化）
            LaunchTypeCombo.Items.Add("Protocol URI");
            LaunchTypeCombo.Items.Add(resourceLoader.GetString("PlatformDialog_Executable"));
            LaunchTypeCombo.Items.Add(resourceLoader.GetString("PlatformDialog_PackagedApp"));
            LaunchTypeCombo.SelectedIndex = isPackagedApp ? 2 : isExecutable ? 1 : 0;

            // 預填現有值
            NameBox.Text = existingEntry?.DisplayName ?? "";
            NameBox.PlaceholderText = resourceLoader.GetString("PlatformDialog_NamePlaceholder");
            TargetBox.Text = existingEntry?.LaunchTarget ?? "";
            ArgsBox.Text = existingEntry?.Arguments ?? "";
            ArgsBox.PlaceholderText = resourceLoader.GetString("PlatformDialog_ArgsPlaceholder");
            PackagedAppSuggestBox.Text = _selectedPackageFamilyName;
            IconFileNameText.Text = existingEntry?.IconFileName ?? "";

            // 依啟動類型設定初始可見性與文字
            UpdateControlVisibility(isExecutable, isPackagedApp);

            // 事件掛接
            LaunchTypeCombo.SelectionChanged += LaunchTypeCombo_SelectionChanged;
            BrowseExeButton.Click += BrowseExeButton_Click;
            BrowseIconButton.Click += BrowseIconButton_Click;
            PackagedAppSuggestBox.GotFocus += PackagedAppSuggestBox_GotFocus;
            PackagedAppSuggestBox.TextChanged += PackagedAppSuggestBox_TextChanged;
            PackagedAppSuggestBox.SuggestionChosen += PackagedAppSuggestBox_SuggestionChosen;
            PrimaryButtonClick += PlatformEditDialog_PrimaryButtonClick;
            Opened += PlatformEditDialog_Opened;
        }

        /// <summary>對話方塊開啟時設定 XYFocus。手把導航（A=觸發焦點元素、B 不動作）與螢幕鍵盤閃避由 GamepadDialog 基底類別自動提供。</summary>
        private void PlatformEditDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            // D-pad 向下離開內容區時，優先跳到「儲存」(Primary) 而非「取消」(Close)
            if (Content is FrameworkElement contentRoot
                && GetTemplateChild("PrimaryButton") is Button primaryButton)
            {
                contentRoot.XYFocusDown = primaryButton;
            }
            // 顯式把初始焦點設到內容區第一個可聚焦元素（NameBox）
            DispatcherQueue.TryEnqueue(() => NameBox.Focus(FocusStateHelper.Preferred));
        }

        // ── 控制項可見性 ────────────────────────────────────────────────────

        /// <summary>
        /// 依啟動類型更新控制項標籤、placeholder 與可見性。
        /// </summary>
        private void UpdateControlVisibility(bool isExe, bool isPackagedAppMode)
        {
            TargetLabel.Text = isExe
                ? _resourceLoader.GetString("PlatformDialog_PathLabel")
                : _resourceLoader.GetString("PlatformDialog_UriLabel");
            TargetBox.PlaceholderText = isExe
                ? _resourceLoader.GetString("PlatformDialog_PathPlaceholder")
                : _resourceLoader.GetString("PlatformDialog_UriPlaceholder");
            TargetBox.MaxLength = isExe ? 260 : 2048;

            TargetLabel.Visibility = isPackagedAppMode ? Visibility.Collapsed : Visibility.Visible;
            TargetRow.Visibility = isPackagedAppMode ? Visibility.Collapsed : Visibility.Visible;
            ArgsLabel.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;
            ArgsBox.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;
            BrowseExeButton.Visibility = isExe ? Visibility.Visible : Visibility.Collapsed;
            PackagedAppWarning.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;
            PackagedAppLabel.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;
            PackagedAppSuggestBox.Visibility = isPackagedAppMode ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 切換啟動類型時更新控制項狀態並清除先前的驗證錯誤。
        /// 首次切換至「封裝套件」模式時預載已安裝套件清單。
        /// </summary>
        private void LaunchTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            bool isExe = LaunchTypeCombo.SelectedIndex == 1;
            bool isPackagedAppMode = LaunchTypeCombo.SelectedIndex == 2;
            UpdateControlVisibility(isExe, isPackagedAppMode);
            if (isPackagedAppMode) EnsurePackagedAppCache();
            TargetError.Visibility = Visibility.Collapsed;
            ArgsError.Visibility = Visibility.Collapsed;
            PackagedAppError.Visibility = Visibility.Collapsed;
        }

        // ── 瀏覽按鈕 ────────────────────────────────────────────────────────

        /// <summary>
        /// 請求開啟 .exe 檔案選擇器。Hide 對話方塊，由 SettingsPage 協調顯示 FilePickerDialog。
        /// </summary>
        private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
        {
            FilePickerRequest = new FilePickerOptions
            {
                FileTypeFilters = [".exe"],
                InitialDirectory = GetDirectoryFromPath(TargetBox.Text),
                ShowImagePreview = false,
                FilterDisplayName = _resourceLoader.GetString("FilePickerDialog_FilterExe")
            };
            ActiveBrowseTarget = BrowseTarget.Exe;
            RequestFilePicker = true;
            Hide();
        }

        /// <summary>
        /// 請求開啟圖片檔案選擇器。Hide 對話方塊，由 SettingsPage 協調顯示 FilePickerDialog。
        /// </summary>
        private void BrowseIconButton_Click(object sender, RoutedEventArgs e)
        {
            FilePickerRequest = new FilePickerOptions
            {
                FileTypeFilters = [".png", ".jpg", ".jpeg", ".bmp"],
                InitialDirectory = null,
                ShowImagePreview = true,
                FilterDisplayName = _resourceLoader.GetString("FilePickerDialog_FilterImage")
            };
            ActiveBrowseTarget = BrowseTarget.Icon;
            RequestFilePicker = true;
            Hide();
        }

        /// <summary>
        /// 接收檔案選擇器的結果，將檔案路徑填入對應欄位。
        /// </summary>
        public void ApplyFilePickerResult(string? filePath)
        {
            if (filePath != null)
            {
                if (ActiveBrowseTarget == BrowseTarget.Exe)
                {
                    TargetBox.Text = filePath;
                }
                else if (ActiveBrowseTarget == BrowseTarget.Icon)
                {
                    _pendingIconPath = filePath;
                    IconFileNameText.Text = Path.GetFileName(filePath);
                }
            }

            RequestFilePicker = false;
            FilePickerRequest = null;
            ActiveBrowseTarget = BrowseTarget.None;
        }

        /// <summary>從檔案路徑取得目錄，用於設定檔案選擇器的初始目錄。</summary>
        private static string? GetDirectoryFromPath(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path))
                    return Path.GetDirectoryName(path);
            }
            catch { }
            return null;
        }

        // ── 封裝應用程式 AutoSuggestBox ─────────────────────────────────────

        /// <summary>
        /// 聚焦時顯示完整套件清單，讓使用者可直接瀏覽而無需先輸入。
        /// </summary>
        private void PackagedAppSuggestBox_GotFocus(object sender, RoutedEventArgs e)
        {
            var box = (AutoSuggestBox)sender;
            var cache = EnsurePackagedAppCache();
            box.ItemsSource = cache.Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})").ToList();
            box.IsSuggestionListOpen = true;
        }

        /// <summary>
        /// 使用者輸入時即時過濾已安裝套件清單（名稱或 PackageFamilyName 皆可搜尋）。
        /// </summary>
        private void PackagedAppSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var cache = EnsurePackagedAppCache();
            string query = sender.Text.Trim();
            sender.ItemsSource = string.IsNullOrEmpty(query)
                ? cache.Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})").ToList()
                : cache
                    .Where(p => p.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                             || p.PackageFamilyName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                    .Select(p => $"{p.DisplayName}  ({p.PackageFamilyName})")
                    .ToList();
        }

        /// <summary>
        /// 選取建議項目後記錄 PackageFamilyName，並在名稱欄位空白時自動填入套件顯示名稱。
        /// </summary>
        private void PackagedAppSuggestBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
        {
            string chosen = args.SelectedItem?.ToString() ?? "";
            var cache = EnsurePackagedAppCache();
            var match = cache.FirstOrDefault(p => $"{p.DisplayName}  ({p.PackageFamilyName})" == chosen);
            if (match != default)
            {
                _selectedPackageFamilyName = match.PackageFamilyName;
                sender.Text = $"{match.DisplayName}  ({match.PackageFamilyName})";
                // 自動填入平台名稱（僅在空白時）
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                    NameBox.Text = match.DisplayName;
            }
        }

        // ── 封裝應用程式清單 ─────────────────────────────────────────────────

        /// <summary>
        /// 載入已安裝封裝應用程式清單（延遲初始化快取）。
        /// 框架套件、資源套件、系統內建套件由此處篩除；名稱層級過濾（延伸模組、背景服務等）
        /// 委由 <see cref="PlatformFieldValidator.IsAllowedPackageFamilyName"/> 統一處理。
        /// </summary>
        private List<(string DisplayName, string PackageFamilyName)> EnsurePackagedAppCache()
        {
            if (_packagedAppCache != null) return _packagedAppCache;

            _packagedAppCache = [];
            try
            {
                var pm = new Windows.Management.Deployment.PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(string.Empty))
                {
                    try
                    {
                        if (pkg.IsFramework || pkg.IsResourcePackage || pkg.IsBundle) continue;
                        if (pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                        // 名稱層級過濾（延伸模組、背景服務、無進入點的執行時期套件等）由 PlatformFieldValidator 統一管理
                        if (!PlatformFieldValidator.IsAllowedPackageFamilyName(pkg.Id.FamilyName, _ownFamilyName)) continue;

                        string displayName = pkg.DisplayName;
                        if (string.IsNullOrWhiteSpace(displayName)) continue;
                        _packagedAppCache.Add((displayName, pkg.Id.FamilyName));
                    }
                    catch { /* 部分系統套件存取 DisplayName 會拋例外 */ }
                }
                _packagedAppCache = _packagedAppCache
                    .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[PackagedApp] Package enumeration failed: {ex.Message}");
            }
            return _packagedAppCache;
        }

        // ── 驗證 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 儲存前驗證所有欄位。不合法時設 args.Cancel = true 阻止對話方塊關閉並顯示紅字錯誤；
        /// 驗證通過時建立 <see cref="ResultEntry"/> 供 MainWindow 在 ShowAsync 返回後儲存。
        /// </summary>
        private async void PlatformEditDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                bool hasError = false;

                string? nameErr = ValidateName(NameBox.Text.Trim());
                if (nameErr != null) { NameError.Text = nameErr; NameError.Visibility = Visibility.Visible; hasError = true; }
                else NameError.Visibility = Visibility.Collapsed;

                bool isExe = LaunchTypeCombo.SelectedIndex == 1;
                bool isPackagedAppMode = LaunchTypeCombo.SelectedIndex == 2;

                if (isPackagedAppMode)
                {
                    if (string.IsNullOrWhiteSpace(_selectedPackageFamilyName))
                    {
                        PackagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppEmpty");
                        PackagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else if (_selectedPackageFamilyName == _ownFamilyName)
                    {
                        PackagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppSelf");
                        PackagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else if (_selectedPackageFamilyName == PlatformFieldValidator.GameBarFamilyName)
                    {
                        PackagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppGameBar");
                        PackagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else if (!PlatformFieldValidator.IsAllowedPackageFamilyName(_selectedPackageFamilyName, _ownFamilyName))
                    {
                        PackagedAppError.Text = _resourceLoader.GetString("PlatformDialog_ValidationPackagedAppNotAllowed");
                        PackagedAppError.Visibility = Visibility.Visible;
                        hasError = true;
                    }
                    else PackagedAppError.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string? targetErr = isExe ? ValidatePath(TargetBox.Text.Trim()) : ValidateUri(TargetBox.Text.Trim());
                    if (targetErr != null)
                    {
                        TargetError.Text = targetErr; TargetError.Visibility = Visibility.Visible; hasError = true;
                    }
                    else if (!isExe)
                    {
                        // 阻止自身 protocol URI（啟動自己會導致 FSE 循環重啟）
                        if (PlatformFieldValidator.IsSelfProtocolUri(TargetBox.Text.Trim()))
                        {
                            TargetError.Text = _resourceLoader.GetString("PlatformDialog_ValidationUriSelf");
                            TargetError.Visibility = Visibility.Visible;
                            hasError = true;
                        }
                        // URI 格式驗證通過後，檢查系統是否有對應的 URI handler
                        else if (!await ProcessLauncherService.IsUriSupportedAsync(TargetBox.Text.Trim()))
                        {
                            TargetError.Text = _resourceLoader.GetString("PlatformDialog_ValidationUriNotSupported");
                            TargetError.Visibility = Visibility.Visible;
                            hasError = true;
                        }
                        else
                        {
                            TargetError.Visibility = Visibility.Collapsed;
                        }
                    }
                    else
                    {
                        TargetError.Visibility = Visibility.Collapsed;
                    }

                    if (isExe)
                    {
                        string? argsErr = ValidateArgs(ArgsBox.Text.Trim());
                        if (argsErr != null) { ArgsError.Text = argsErr; ArgsError.Visibility = Visibility.Visible; hasError = true; }
                        else ArgsError.Visibility = Visibility.Collapsed;
                    }
                }

                if (hasError)
                {
                    args.Cancel = true;
                    return;
                }

                // 驗證通過：建立結果項目供 MainWindow 儲存
                ResultEntry = BuildResultEntry();
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>
        /// 從目前表單狀態建立或更新平台項目。
        /// 編輯模式下直接修改 <see cref="_existingEntry"/>；新增模式下建立新實例。
        /// </summary>
        private UserPlatformEntry BuildResultEntry()
        {
            var entry = _existingEntry ?? new UserPlatformEntry();
            entry.DisplayName = NameBox.Text.Trim();

            if (LaunchTypeCombo.SelectedIndex == 2)
            {
                entry.LaunchType = "PackagedApp";
                entry.LaunchTarget = "";
                entry.Arguments = "";
                entry.PackageFamilyName = _selectedPackageFamilyName;
            }
            else
            {
                entry.LaunchType = LaunchTypeCombo.SelectedIndex == 1 ? "Executable" : "ProtocolUri";
                entry.LaunchTarget = TargetBox.Text.Trim();
                entry.Arguments = LaunchTypeCombo.SelectedIndex == 1 ? ArgsBox.Text.Trim() : "";
                entry.PackageFamilyName = "";
            }

            return entry;
        }

        // ── 輸入驗證 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 驗證平台名稱是否合法。
        /// </summary>
        private string? ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (name.Length > 50)
                return _resourceLoader.GetString("PlatformDialog_ValidationNameTooLong");
            if (!PlatformFieldValidator.IsValidName(name))
                return _resourceLoader.GetString("PlatformDialog_ValidationNameInvalid");
            return null;
        }

        /// <summary>
        /// 驗證 Protocol URI 格式（委由 <see cref="PlatformFieldValidator"/> 執行）。
        /// </summary>
        private string? ValidateUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (uri.Length > 2048)
                return _resourceLoader.GetString("PlatformDialog_ValidationUriTooLong");
            if (!PlatformFieldValidator.IsValidUri(uri))
                return _resourceLoader.GetString("PlatformDialog_ValidationUriInvalid");
            return null;
        }

        /// <summary>
        /// 驗證執行檔路徑格式（委由 <see cref="PlatformFieldValidator"/> 執行）。
        /// </summary>
        private string? ValidatePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return _resourceLoader.GetString("PlatformDialog_ValidationTargetEmpty");
            if (path.Length > 260)
                return _resourceLoader.GetString("PlatformDialog_ValidationPathTooLong");
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return _resourceLoader.GetString("PlatformDialog_ValidationPathNotExe");
            if (!PlatformFieldValidator.IsValidExecutablePath(path))
                return _resourceLoader.GetString("PlatformDialog_ValidationPathInvalid");
            if (PlatformFieldValidator.IsConsoleApplication(path))
                return _resourceLoader.GetString("PlatformDialog_ValidationPathConsoleApp");
            return null;
        }

        /// <summary>
        /// 驗證啟動參數（委由 <see cref="PlatformFieldValidator"/> 執行）。
        /// </summary>
        private string? ValidateArgs(string args)
        {
            if (string.IsNullOrEmpty(args)) return null;
            if (args.Length > 500)
                return _resourceLoader.GetString("PlatformDialog_ValidationArgsTooLong");
            if (!PlatformFieldValidator.IsValidArguments(args))
                return _resourceLoader.GetString("PlatformDialog_ValidationArgsInvalid");
            return null;
        }
    }
}
