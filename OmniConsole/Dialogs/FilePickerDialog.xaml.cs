using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OmniConsole.Dialogs
{
    /// <summary>
    /// 支援手把操作的自製檔案選擇器，取代系統 FileOpenPicker（不支援手把）。
    /// 支援 D-pad 導覽、鍵盤、滑鼠與觸控操作。
    /// </summary>
    public sealed partial class FilePickerDialog : ContentDialog
    {
        private readonly ResourceLoader _resourceLoader;
        private readonly FilePickerOptions _options;
        private readonly List<SidebarItem> _sidebarItems = [];
        private GamepadNavigationService? _gamepadNav;
        private Action? _keyboardAvoidanceCleanup;
        private CancellationTokenSource? _previewCts;

        private string _currentPath = "";
        private FileSystemBrowserService.FileSystemItem? _selectedFile;
        /// <summary>使用者選取的檔案完整路徑。若取消則為 null。</summary>
        public string? SelectedFilePath { get; private set; }

        /// <summary>使用者要求使用系統 FileOpenPicker。SettingsPage 檢查此旗標後協調開啟。</summary>
        public bool RequestLegacyPicker { get; private set; }

        /// <summary>
        /// 初始化檔案選擇器，設定版面配置模式、側邊欄、在地化文字與事件繫結。
        /// </summary>
        public FilePickerDialog(XamlRoot xamlRoot, ResourceLoader resourceLoader, FilePickerOptions options)
        {
            InitializeComponent();
            XamlRoot = xamlRoot;
            _resourceLoader = resourceLoader;
            _options = options;

            Title = resourceLoader.GetString("FilePickerDialog_Title");
            PrimaryButtonText = resourceLoader.GetString("FilePickerDialog_Select");
            CloseButtonText = resourceLoader.GetString("PlatformDialog_Cancel");
            IsPrimaryButtonEnabled = false;

            FileTypeFilterText.Text = options.FilterDisplayName ?? string.Join("; ", options.FileTypeFilters.Select(f => $"*{f}"));

            // 根據模式設定寬度：有預覽時加寬
            if (options.ShowImagePreview)
            {
                RootGrid.Width = 780;
                PreviewColumn.Width = new GridLength(180);
                PreviewPanel.Visibility = Visibility.Visible;
            }
            else
            {
                RootGrid.Width = 600;
            }

            BuildSidebar();

            ToolTipService.SetToolTip(NavigateUpButton, resourceLoader.GetString("FilePickerDialog_NavigateUp"));
            LegacyPickerButtonText.Text = resourceLoader.GetString("FilePickerDialog_LegacyPicker");

            // 事件
            NavigateUpButton.Click += NavigateUpButton_Click;
            LegacyPickerButton.Click += LegacyPickerButton_Click;
            SidebarListView.ItemClick += SidebarListView_ItemClick;
            FileListView.ItemClick += FileListView_ItemClick;
            FileListView.SelectionChanged += FileListView_SelectionChanged;
            PrimaryButtonClick += FilePickerDialog_PrimaryButtonClick;
            Opened += FilePickerDialog_Opened;
            Closed += FilePickerDialog_Closed;
        }

        // ── 側邊欄 ────────────────────────────────────────────────────────────

        /// <summary>建立側邊欄：快速存取資料夾 + 分隔線 + 磁碟清單。</summary>
        private void BuildSidebar()
        {
            // 快速存取
            var quickAccess = FileSystemBrowserService.GetQuickAccessPaths();
            foreach (var (name, path) in quickAccess)
            {
                _sidebarItems.Add(new SidebarItem(
                    GetLocalizedFolderName(name), path, GetFolderGlyph(name)));
            }

            // 分隔線（用空項目表示）
            _sidebarItems.Add(new SidebarItem("", "", ""));

            // 磁碟
            var drives = FileSystemBrowserService.GetAvailableDrives();
            foreach (var drive in drives)
            {
                var label = string.IsNullOrEmpty(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                _sidebarItems.Add(new SidebarItem(label, drive.RootDirectory.FullName, "\uEDA2")); // HardDrive
            }

            SidebarListView.ItemsSource = _sidebarItems;
            SidebarListView.ContainerContentChanging += SidebarListView_ContainerContentChanging;
        }

        /// <summary>分隔線項目渲染為不可見、不可聚焦的間距。</summary>
        private void SidebarListView_ContainerContentChanging(ListViewBase sender,
            ContainerContentChangingEventArgs args)
        {
            if (args.Item is SidebarItem item && string.IsNullOrEmpty(item.Path))
            {
                // 分隔線項目：不可聚焦、不可點選、縮小高度
                args.ItemContainer.IsHitTestVisible = false;
                args.ItemContainer.IsTabStop = false;
                args.ItemContainer.MinHeight = 0;
                args.ItemContainer.Height = 0;
                args.ItemContainer.Padding = new Thickness(0);
                args.ItemContainer.Margin = new Thickness(0, 4, 0, 4);
            }
        }

        /// <summary>將快速存取資料夾英文名稱轉為在地化顯示名稱。</summary>
        private string GetLocalizedFolderName(string englishName) => englishName switch
        {
            "Desktop" => _resourceLoader.GetString("FilePickerDialog_Desktop"),
            "Downloads" => _resourceLoader.GetString("FilePickerDialog_Downloads"),
            "Documents" => _resourceLoader.GetString("FilePickerDialog_Documents"),
            "Pictures" => _resourceLoader.GetString("FilePickerDialog_Pictures"),
            "Music" => _resourceLoader.GetString("FilePickerDialog_Music"),
            "Videos" => _resourceLoader.GetString("FilePickerDialog_Videos"),
            _ => englishName
        };

        /// <summary>依資料夾名稱回傳對應的 Segoe Fluent Icons 字元。</summary>
        private static string GetFolderGlyph(string name) => name switch
        {
            "Desktop" => "\uE8FC",    // Monitor
            "Downloads" => "\uE896",  // Download
            "Documents" => "\uE8A5",  // Document
            "Pictures" => "\uEB9F",   // Photo
            "Music" => "\uEC4F",      // MusicNote
            "Videos" => "\uE8B2",     // Video
            _ => "\uE8B7"             // Folder
        };

        // ── 導覽 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 導覽至指定目錄，列舉內容並更新檔案清單。
        /// 若指定 focusItemName，焦點會定位到該項目（用於 B 鍵返回上層）。
        /// </summary>
        private void NavigateTo(string path, string? focusItemName = null)
        {
            DebugLogger.Log($"[NavigateTo] path={path}, focusItemName={focusItemName}");
            _currentPath = path;
            CurrentPathText.Text = path;
            _selectedFile = null;
            SelectedFilePath = null;
            IsPrimaryButtonEnabled = false;
            SelectedFileText.Text = "";

            // 清除預覽
            PreviewImage.Source = null;
            PreviewFileName.Text = "";
            PreviewDimensions.Text = "";

            // 嘗試列舉
            try
            {
                var (dirs, files) = FileSystemBrowserService.GetDirectoryContents(path, _options.FileTypeFilters);

                var items = new List<FileListItem>();
                foreach (var d in dirs)
                    items.Add(new FileListItem(d.Name, d.FullPath, true, "",
                        d.LastModified?.ToString("yyyy/MM/dd") ?? "", "\uE8B7"));
                foreach (var f in files)
                    items.Add(new FileListItem(f.Name, f.FullPath, false,
                        f.SizeBytes.HasValue ? FileSystemBrowserService.FormatFileSize(f.SizeBytes.Value) : "",
                        f.LastModified?.ToString("yyyy/MM/dd") ?? "",
                        GetFileGlyph(f.Name)));

                FileListView.ItemsSource = items;
                FileListView.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                EmptyFolderText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                AccessDeniedText.Visibility = Visibility.Collapsed;

                if (EmptyFolderText.Visibility == Visibility.Visible)
                    EmptyFolderText.Text = _resourceLoader.GetString("FilePickerDialog_EmptyFolder");

                // 強制版面配置後把焦點設到目標項目（返回上層時定位到剛才的資料夾）
                if (items.Count == 0)
                {
                    // 延遲一幀再設焦點，避免 ItemClick 事件處理尚未結束時焦點被搶回
                    DispatcherQueue.TryEnqueue(() =>
                        NavigateUpButton.Focus(FocusState.Programmatic));
                }
                else
                {
                    var targetIndex = 0;
                    if (focusItemName != null)
                    {
                        var idx = items.FindIndex(i => i.Name == focusItemName);
                        if (idx >= 0) targetIndex = idx;
                    }

                    FocusFileListItem(targetIndex);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                DebugLogger.Log($"[NavigateTo] UnauthorizedAccessException: {ex.Message}");
                FileListView.ItemsSource = null;
                FileListView.Visibility = Visibility.Collapsed;
                EmptyFolderText.Visibility = Visibility.Collapsed;
                AccessDeniedText.Visibility = Visibility.Visible;
                AccessDeniedText.Text = _resourceLoader.GetString("FilePickerDialog_AccessDenied");
            }

            // 上層按鈕狀態
            NavigateUpButton.IsEnabled = FileSystemBrowserService.GetParentDirectory(path) != null;
        }

        /// <summary>依副檔名回傳對應的 Segoe Fluent Icons 字元。</summary>
        private string GetFileGlyph(string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".exe" => "\uE756",  // AppIconDefault
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" => "\uEB9F", // Photo
                _ => "\uE8A5"        // Document
            };
        }

        /// <summary>強制版面配置後將焦點設到指定索引的檔案清單項目。容器尚未生成時透過 ContainerContentChanging 等待生成。</summary>
        private void FocusFileListItem(int index)
        {
            var items = FileListView.ItemsSource as List<FileListItem>;
            if (items == null || index >= items.Count) return;

            // 先捲動到目標項目，確保虛擬化容器被生成
            FileListView.ScrollIntoView(items[index]);
            FileListView.UpdateLayout();

            var c1 = FileListView.ContainerFromIndex(index);
            DebugLogger.Log($"[FocusFile] after ScrollIntoView+UpdateLayout: index={index}, container={c1?.GetType().Name}");

            if (c1 is ListViewItem container)
            {
                container.Focus(FocusState.Programmatic);
                return;
            }

            // 容器尚未生成：等 ContainerContentChanging 觸發
            void OnContainerReady(ListViewBase sender, ContainerContentChangingEventArgs args)
            {
                if (args.ItemIndex == index)
                {
                    FileListView.ContainerContentChanging -= OnContainerReady;
                    args.ItemContainer.Focus(FocusState.Programmatic);
                }
            }
            FileListView.ContainerContentChanging += OnContainerReady;
        }

        /// <summary>上層按鈕點選：返回上層目錄。</summary>
        private void NavigateUpButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateUp();
        }

        /// <summary>側邊欄項目點選：導覽至該路徑。</summary>
        private void SidebarListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SidebarItem item && !string.IsNullOrEmpty(item.Path))
                NavigateTo(item.Path);
        }

        /// <summary>
        /// 檔案清單項目點選：資料夾→進入；檔案第一次→選取；同一檔案第二次→確認關閉。
        /// </summary>
        private void FileListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is FileListItem item)
            {
                if (item.IsDirectory)
                {
                    NavigateTo(item.FullPath);
                }
                else if (SelectedFilePath == item.FullPath)
                {
                    // 已選取同一檔案，再按 A = 確認
                    if (GetTemplateChild("PrimaryButton") is Button btn)
                    {
                        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(btn);
                        var invokeProvider = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)
                            peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
                        invokeProvider.Invoke();
                    }
                }
                else
                {
                    // 第一次點選檔案 = 選取
                    _selectedFile = new FileSystemBrowserService.FileSystemItem(
                        item.Name, item.FullPath, false, null, null);
                    SelectedFilePath = item.FullPath;
                    IsPrimaryButtonEnabled = true;
                    DefaultButton = ContentDialogButton.Primary;
                    SelectedFileText.Text = item.Name;
                }
            }
        }

        /// <summary>檔案清單選取變更：更新選取狀態、按鈕醒目提示，圖片模式下載入預覽。</summary>
        private void FileListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileListView.SelectedItem is FileListItem item && !item.IsDirectory)
            {
                _selectedFile = new FileSystemBrowserService.FileSystemItem(
                    item.Name, item.FullPath, false, null, null);
                SelectedFilePath = item.FullPath;
                IsPrimaryButtonEnabled = true;
                DefaultButton = ContentDialogButton.Primary;
                SelectedFileText.Text = item.Name;

                // 圖片預覽
                if (_options.ShowImagePreview)
                    _ = LoadPreviewAsync(item.FullPath);
            }
            else
            {
                _selectedFile = null;
                SelectedFilePath = null;
                IsPrimaryButtonEnabled = false;
                DefaultButton = ContentDialogButton.None;
                SelectedFileText.Text = "";

                PreviewImage.Source = null;
                PreviewFileName.Text = "";
                PreviewDimensions.Text = "";
            }
        }

        // ── 圖片預覽 ──────────────────────────────────────────────────────────

        /// <summary>非同步載入圖片預覽，支援取消（快速切換時取消前一張）。</summary>
        private async Task LoadPreviewAsync(string filePath)
        {
            _previewCts?.Cancel();
            _previewCts = new CancellationTokenSource();
            var token = _previewCts.Token;

            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                if (token.IsCancellationRequested) return;

                using var stream = await file.OpenReadAsync();
                if (token.IsCancellationRequested) return;

                var bitmap = new BitmapImage { DecodePixelWidth = 200 };
                await bitmap.SetSourceAsync(stream);
                if (token.IsCancellationRequested) return;

                PreviewImage.Source = bitmap;
                PreviewFileName.Text = Path.GetFileName(filePath);

                // 讀取原始解析度
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                PreviewDimensions.Text = $"{decoder.PixelWidth} x {decoder.PixelHeight}";
            }
            catch
            {
                if (!token.IsCancellationRequested)
                {
                    PreviewImage.Source = null;
                    PreviewFileName.Text = "";
                    PreviewDimensions.Text = "";
                }
            }
        }

        // ── 手把 ──────────────────────────────────────────────────────────────

        /// <summary>對話方塊開啟時：設定 XY 焦點導覽、啟動手把輪詢、螢幕鍵盤閃避，並導覽至初始目錄。</summary>
        private void FilePickerDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            _gamepadNav = new GamepadNavigationService(
                searchRoot: this,
                dispatcherQueue: Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
                onAButtonPressed: () => GamepadNavigationService.ActivateFocusedElement(XamlRoot),
                onBButtonPressed: NavigateUp);
            _gamepadNav.Start();

            _keyboardAvoidanceCleanup = GamepadNavigationService.EnableKeyboardAvoidance(
                GetTemplateChild("BackgroundElement") as FrameworkElement, XamlRoot);

            // 初始導覽
            var initialPath = _options.InitialDirectory;
            if (string.IsNullOrEmpty(initialPath) || !Directory.Exists(initialPath))
            {
                var drives = FileSystemBrowserService.GetAvailableDrives();
                initialPath = drives.Count > 0 ? drives[0].RootDirectory.FullName : @"C:\";
            }
            NavigateTo(initialPath);

            // 預設焦點到檔案清單
            FileListView.Focus(FocusState.Programmatic);
        }

        /// <summary>對話方塊關閉時：停止手把輪詢、清理螢幕鍵盤閃避與預覽取消 token。</summary>
        private void FilePickerDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _gamepadNav?.Stop();
            _gamepadNav = null;
            _keyboardAvoidanceCleanup?.Invoke();
            _keyboardAvoidanceCleanup = null;
            _previewCts?.Cancel();
            _previewCts?.Dispose();
            _previewCts = null;
        }

        /// <summary>
        /// B 鍵：返回上層目錄。若已在根目錄則不動作（不關閉對話方塊）。
        /// </summary>
        private void NavigateUp()
        {
            var parent = FileSystemBrowserService.GetParentDirectory(_currentPath);
            DebugLogger.Log($"[NavigateUp] currentPath={_currentPath}, parent={parent}");
            if (parent != null)
            {
                // 記住目前資料夾名稱，返回上層後焦點定位到它
                var currentDirName = Path.GetFileName(_currentPath.TrimEnd('\\'));
                NavigateTo(parent, currentDirName);
            }
        }

        /// <summary>選取按鈕點選：未選取檔案時取消關閉。</summary>
        private void FilePickerDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_selectedFile == null || string.IsNullOrEmpty(SelectedFilePath))
                args.Cancel = true;
        }

        /// <summary>「瀏覽 (Windows)」按鈕點選：設旗標後 Hide，由 SettingsPage 協調開啟系統 FileOpenPicker。</summary>
        private void LegacyPickerButton_Click(object sender, RoutedEventArgs e)
        {
            RequestLegacyPicker = true;
            Hide();
        }

        // ── 資料模型 ──────────────────────────────────────────────────────────

        /// <summary>側邊欄項目。</summary>
        private record SidebarItem(string DisplayName, string Path, string Glyph);

        /// <summary>檔案清單項目。</summary>
        private record FileListItem(string Name, string FullPath, bool IsDirectory,
            string Size, string Date, string Glyph);
    }
}
