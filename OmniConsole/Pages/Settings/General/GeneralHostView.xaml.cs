using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using OmniConsole.Models;
using OmniConsole.Services;
using System;
using System.Threading.Tasks;

namespace OmniConsole.Pages.Settings.General
{
    /// <summary>
    /// 設定「一般」分頁的宿主：標題區 + System/User 分類索引標籤 + 內層 Frame。
    /// System/User 各為獨立子頁、由 InnerFrame 互相導覽（切標籤＝整頁橫滑、卡片不個別動）。
    /// 跨子頁共用的選取狀態（_selectedPlatformId/預設平台底色）由本宿主持有、子頁透過宿主參照讀寫。
    /// 由 SettingsHostView 透過 SettingsContentFrame.Navigate 載入；本頁不實作手把 scope，相關鍵分派仍在 SettingsHostView、經本宿主的 internal 命令方法轉入。
    /// </summary>
    public sealed partial class GeneralHostView : Page
    {
        private readonly ResourceLoader _resourceLoader = new();

        // 跨 System/User 共用：目前選取（焦點）的平台 Id（底色/預設平台），由子頁透過 SelectedPlatformId/SetSelectedPlatform 讀寫。
        private string _selectedPlatformId = "";
        // 目前分類索引標籤（System / User）。
        private string _currentCategoryTag = "System";

        /// <summary>主視窗 HWND，由 SettingsHostView 注入，供 User 子頁的舊式系統檔案選擇器定位視窗使用。</summary>
        internal IntPtr Hwnd { get; set; }

        /// <summary>子頁狀態變動（分類/同意/清單變）或切子頁後觸發，殼層據此重評底部手把提示列。</summary>
        internal event EventHandler? StateChanged;

        /// <summary>建立一般分頁宿主。</summary>
        public GeneralHostView()
        {
            InitializeComponent();
        }

        // ── 入頁初始化（由 SettingsHostView 每次進設定/進 General 頁呼叫）─────────────

        /// <summary>初始化：套標題/描述、依目前預設平台決定初始分類標籤並導覽對應子頁。</summary>
        internal void Initialize()
        {
            ApplyBrandedTitles();
            UpdateSettingsDescription();

            // 若目前選取的平台是使用者自訂的，初始進 User 索引，否則 System。
            var currentPlatform = SettingsService.GetDefaultPlatform();
            _selectedPlatformId = currentPlatform.Id;
            bool isUserPlatform = PlatformCatalog.FindById(currentPlatform.Id) == null
                && UserPlatformStore.FindById(currentPlatform.Id) != null;
            _currentCategoryTag = isUserPlatform ? "User" : "System";

            // 同步索引標籤選取 + 主動導覽一次：暫解 SelectionChanged 避免賦值連帶觸發第二次導覽（賦值是否觸發視值有無變化而定），
            // 改由下方明確呼叫 NavigateTo 單一導覽。
            PlatformCategoryNav.SelectionChanged -= PlatformCategoryNav_SelectionChanged;
            foreach (NavigationViewItem navItem in PlatformCategoryNav.MenuItems)
            {
                if (navItem.Tag is string t && t == _currentCategoryTag)
                {
                    PlatformCategoryNav.SelectedItem = navItem;
                    break;
                }
            }
            PlatformCategoryNav.SelectionChanged += PlatformCategoryNav_SelectionChanged;

            NavigateTo(_currentCategoryTag, playSound: false, notify: false);
        }

        /// <summary>設定含品牌名的標題（由 code-behind 設、注入品牌）。</summary>
        private void ApplyBrandedTitles()
        {
            SettingsTitleText.Text = _resourceLoader.Loc("SettingsTitle");
        }

        /// <summary>更新標題下方描述，顯示目前預設平台名稱。跨 tab 共用故由宿主持有。</summary>
        private void UpdateSettingsDescription()
        {
            var platform = SettingsService.GetDefaultPlatform();
            var name = ProcessLauncherService.GetPlatformDisplayName(platform);
            SettingsDescription.Text = string.Format(_resourceLoader.Loc("SettingsDescription"), name);
        }

        // ── 分類索引標籤切換 → Frame 導覽 ─────────────────────────────────────────

        /// <summary>分類索引標籤選項變更：依 Tag 導覽對應子頁。</summary>
        private void PlatformCategoryNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
                NavigateTo(tag, playSound: true);
        }

        /// <summary>依分類標籤導覽到對應子頁（System/User）；使用者切換時補 Invoke 音（NavigationViewItem 點選預設不播音）、入頁初始化導覽不播。
        /// notify=false（入頁初始導覽）時子頁不發 StateChanged：初始手把提示列由殼層進設定頁時統一主動驅動。</summary>
        private void NavigateTo(string tag, bool playSound, bool notify = true)
        {
            bool changed = InnerFrame.Content?.GetType() != (tag == "User" ? typeof(UserPlatformsView) : typeof(SystemPlatformsView));
            _currentCategoryTag = tag;
            if (tag == "User") NavigateToUser(notify);
            else NavigateToSystem(notify);

            // 切標籤點選預設不播音，於此補 Invoke 音讓滑鼠/觸控/手把路徑皆有回饋；走共用同輪去重避免連鎖播兩次。
            // 僅「使用者主動切換且真正換頁」時播（初始化導覽 playSound=false、已在該頁 changed=false 皆不播）。
            if (playSound && changed)
                GamepadNavigationService.PlaySound(ElementSoundKind.Invoke);
        }

        /// <summary>導覽到系統平台子頁；整頁橫滑動畫、用完即丟、切頁後排一次非阻塞背景回收。notify=false（入頁初始）時不發 StateChanged。</summary>
        internal void NavigateToSystem(bool notify = true)
        {
            _currentCategoryTag = "System";
            SyncTabSelection("System");
            // System 是左側標籤：往左回。
            NavigateChild(typeof(SystemPlatformsView), toRight: false, notify);
        }

        /// <summary>導覽到使用者平台子頁；同 NavigateToSystem。</summary>
        private void NavigateToUser(bool notify = true)
        {
            _currentCategoryTag = "User";
            SyncTabSelection("User");
            // User 是右側標籤：往右去。
            NavigateChild(typeof(UserPlatformsView), toRight: true, notify);
        }

        /// <summary>
        /// 內層 Frame 導覽到指定子頁：依標籤左右相對位置做整頁橫向滑動。
        /// <paramref name="toRight"/>=true 表示切往右側分類、false 表示切往左側。
        /// 子頁設 NavigationCacheMode.Disabled（切走即銷毀）、把宿主自身當參數傳入供子頁讀寫共用狀態、切頁後排一次非阻塞回收。
        /// <paramref name="notify"/>=false（僅入頁初始導覽）時不發 StateChanged：初始手把提示列由殼層進設定頁時統一主動驅動，
        /// 避免與殼層明確更新重複（切索引等執行中導覽仍發，殼層據此重評提示列）。
        /// </summary>
        private void NavigateChild(Type pageType, bool toRight, bool notify)
        {
            if (InnerFrame.Content?.GetType() == pageType) return;

            FocusNavHelper.NavigateCategory(InnerFrame, pageType, toRight, this);
            PageLifecycleHelper.CollectDiscardedPage();
            if (InnerFrame.Content is Page p)
                p.NavigationCacheMode = NavigationCacheMode.Disabled;
            UpdateImportButtonVisibility();
            if (notify)
                StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>同步索引標籤選取狀態（手把 LB/RB 經 NavigateToSystem/User 觸發時需要）；暫解 SelectionChanged 避免重入導覽。</summary>
        private void SyncTabSelection(string tag)
        {
            foreach (NavigationViewItem navItem in PlatformCategoryNav.MenuItems)
            {
                if (navItem.Tag is string t && t == tag)
                {
                    if (!ReferenceEquals(PlatformCategoryNav.SelectedItem, navItem))
                    {
                        PlatformCategoryNav.SelectionChanged -= PlatformCategoryNav_SelectionChanged;
                        PlatformCategoryNav.SelectedItem = navItem;
                        PlatformCategoryNav.SelectionChanged += PlatformCategoryNav_SelectionChanged;
                    }
                    break;
                }
            }
        }

        // ── 共用狀態（子頁透過宿主參照讀寫）───────────────────────────────────────

        /// <summary>目前選取（焦點）的平台 Id；子頁載入後據此還原 GridView 選取底色。</summary>
        internal string SelectedPlatformId => _selectedPlatformId;

        /// <summary>子頁更新選取平台時寫回宿主（並重新整理描述列的預設平台名）。</summary>
        internal void SetSelectedPlatform(string id)
        {
            _selectedPlatformId = id;
            UpdateSettingsDescription();
        }

        /// <summary>子頁通知宿主狀態變動（同意/清單變），轉發給殼層重評提示列。</summary>
        internal void NotifyStateChanged()
        {
            UpdateImportButtonVisibility();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // ── 對殼層暴露的狀態與命令（手把 handler 與提示列按鈕轉入）──────────────────

        /// <summary>目前是否在使用者平台子頁（殼層 UpdateGamepadHints/Y/X/Menu/LB/RB 計算時讀取）。
        /// 以實際 Frame 內容為準，不讀 _currentCategoryTag 欄位。</summary>
        internal bool IsCurrentCategoryUser => InnerFrame.Content is UserPlatformsView;

        /// <summary>LB/RB 肩鍵切換分類索引標籤（殼層手把 handler 呼叫）。</summary>
        internal void SwitchCategoryTabInternal(string tag) => NavigateTo(tag, playSound: true);

        /// <summary>Y 鍵新增平台：轉發目前 User 子頁。</summary>
        internal Task AddPlatformInternal() =>
            (InnerFrame.Content as UserPlatformsView)?.AddPlatformInternal() ?? Task.CompletedTask;

        /// <summary>提示列 X 按鈕（滑鼠路徑）：編輯選取的使用者平台，轉發目前 User 子頁。</summary>
        internal Task EditSelectedPlatformInternal() =>
            (InnerFrame.Content as UserPlatformsView)?.EditSelectedPlatformInternal() ?? Task.CompletedTask;

        /// <summary>X 鍵（手把路徑）：編輯焦點所在的使用者平台，轉發目前 User 子頁。</summary>
        internal Task EditFocusedPlatformInternal(object? focused) =>
            (InnerFrame.Content as UserPlatformsView)?.EditFocusedPlatformInternal(focused) ?? Task.CompletedTask;

        /// <summary>A 鍵：焦點在分類索引標籤時宿主自己走 NavigateTo 切 tab；焦點在卡片時轉發目前子頁。回傳是否已處理。</summary>
        internal bool TryHandleAButton(object? focused)
        {
            // 焦點在本宿主的分類索引標籤上：自己切 tab（同原始 GeneralView.TryHandleAButton 的 NavigationViewItem case）。
            if (focused is NavigationViewItem navItem && PlatformCategoryNav.MenuItems.Contains(navItem))
            {
                if (navItem.Tag is string tag)
                    NavigateTo(tag, playSound: true);
                return true;
            }
            if (InnerFrame.Content is SystemPlatformsView s) return s.TryHandleAButtonInternal(focused);
            if (InnerFrame.Content is UserPlatformsView u) return u.TryHandleAButtonInternal(focused);
            return false;
        }

        /// <summary>Menu 鍵：確認焦點卡片選取並回傳是否有可啟動目標，轉發目前子頁。</summary>
        internal bool TrySelectFocusedCardForLaunch()
        {
            if (InnerFrame.Content is SystemPlatformsView s) return s.TrySelectFocusedCardForLaunch();
            if (InnerFrame.Content is UserPlatformsView u) return u.TrySelectFocusedCardForLaunch();
            return false;
        }

        /// <summary>匯入按鈕點選：轉發目前 User 子頁開啟匯入（貼 JSON）對話方塊；子頁未把焦點移到卡片時把白框還原回本按鈕。</summary>
        private async void ImportPlatformButton_Click(object sender, RoutedEventArgs e)
        {
            if (InnerFrame.Content is not UserPlatformsView user) return;
            bool focusMoved = await user.ImportInternal();
            if (!focusMoved) ImportPlatformButton.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>社群平台按鈕點選：轉發目前 User 子頁開啟社群平台瀏覽對話方塊；子頁未把焦點移到卡片時把白框還原回本按鈕。</summary>
        private async void CommunityPlatformsButton_Click(object sender, RoutedEventArgs e)
        {
            if (InnerFrame.Content is not UserPlatformsView user) return;
            bool focusMoved = await user.CommunityInternal();
            if (!focusMoved) CommunityPlatformsButton.Focus(FocusStateHelper.Preferred);
        }

        /// <summary>社群平台按鈕與匯入按鈕顯隱：僅 User 子頁且已同意自訂平台免責聲明時顯示（社群資料本質是別人給的自訂平台，同一張同意書管轄）。</summary>
        private void UpdateImportButtonVisibility()
        {
            bool show = InnerFrame.Content is UserPlatformsView
                && SettingsService.GetCustomPlatformConsentAccepted();
            ImportPlatformButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            CommunityPlatformsButton.Visibility = ImportPlatformButton.Visibility;
        }
    }
}
