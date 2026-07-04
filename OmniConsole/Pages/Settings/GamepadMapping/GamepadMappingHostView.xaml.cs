using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using OmniConsole.Models;
using OmniConsole.Services;
using System;

namespace OmniConsole.Pages.Settings.GamepadMapping
{
    /// <summary>
    /// 手把映射頁的內層宿主：清單（GamepadProfileListView）與編輯器（GamepadProfileEditor）各為獨立 Page、
    /// 由 InnerFrame 互相導覽（切走即銷毀）。本宿主持有跨 Page 狀態（剛編輯過的 AppId），
    /// 並對外層 SettingsHostView 轉發手把命令與狀態（清單/編輯器各自實例不直接外露）。
    /// </summary>
    public sealed partial class GamepadMappingHostView : Page
    {
        // 編輯器返回清單時用以還原焦點的目標 AppId（清單/編輯器各自重建，狀態不能存在子 Page 內）
        private AppId? _lastEditedAppId;

        /// <summary>清單內容變動或清單/編輯器切換後觸發，外層據此重評底部手把提示列。</summary>
        internal event EventHandler? StateChanged;

        /// <summary>建立手把映射內層宿主。</summary>
        public GamepadMappingHostView()
        {
            InitializeComponent();
        }

        // ── 入頁初始化 ────────────────────────────────────────────────────────

        /// <summary>
        /// 外層導覽進來後呼叫：有 Protocol 帶入的待編輯 AppId（且非黑名單）則直開編輯器、否則顯示清單。
        /// </summary>
        internal void Initialize(AppId? pendingEditAppId, string pendingEditDisplayName)
        {
            if (pendingEditAppId != null && !GamepadProfileStore.IsBlacklisted(pendingEditAppId))
            {
                NavigateToEditor(pendingEditAppId, pendingEditDisplayName);
                return;
            }
            NavigateToList();
        }

        // ── 清單 / 編輯器切換 ─────────────────────────────────────────────────

        /// <summary>
        /// 導覽到清單頁，把剛編輯過的 AppId 當參數帶入供還原焦點，並重訂清單事件。
        /// 套用頁面切換動畫；不清 BackStack。
        /// 導覽後排一次非阻塞背景回收，紓解用完即丟丟棄舊頁的記憶體壓力。
        /// </summary>
        private void NavigateToList()
        {
            FocusNavHelper.NavigatePage(InnerFrame, typeof(GamepadProfileListView), _lastEditedAppId);
            PageLifecycleHelper.CollectDiscardedPage();
            if (InnerFrame.Content is GamepadProfileListView list)
            {
                list.NavigationCacheMode = NavigationCacheMode.Disabled;
                list.EditRequested += List_EditRequested;
                list.ItemsChanged += List_ItemsChanged;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>導覽到編輯器頁，記下目標 AppId 供返回還原焦點；套用頁面切換動畫、不清 BackStack 並排一次非阻塞背景回收（同 NavigateToList）。</summary>
        private void NavigateToEditor(AppId appId, string displayName)
        {
            _lastEditedAppId = appId;
            FocusNavHelper.NavigatePage(InnerFrame, typeof(GamepadProfileEditor),
                new GamepadProfileEditorParam(appId, displayName));
            PageLifecycleHelper.CollectDiscardedPage();
            if (InnerFrame.Content is GamepadProfileEditor editor)
            {
                editor.NavigationCacheMode = NavigationCacheMode.Disabled;
                editor.Closed += Editor_ClosedOrDeleted;
                editor.Deleted += Editor_ClosedOrDeleted;
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>清單發出編輯請求（A 鍵或滑鼠點列項）：記下目標並切到編輯器。</summary>
        private void List_EditRequested(object? sender, AppId appId) => NavigateToEditor(appId, string.Empty);

        /// <summary>清單內容變動：往外層轉發以重評提示列。</summary>
        private void List_ItemsChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>編輯器存檔/刪除後返回清單頁。</summary>
        private void Editor_ClosedOrDeleted(object? sender, EventArgs e) => NavigateToList();

        // ── 對外狀態（外層 UpdateGamepadHints 讀取）────────────────────────────

        /// <summary>目前是否在編輯器頁。</summary>
        internal bool IsEditorVisible => InnerFrame.Content is GamepadProfileEditor;

        /// <summary>目前是否在清單頁。</summary>
        internal bool IsListVisible => InnerFrame.Content is GamepadProfileListView;

        /// <summary>編輯器頁是否可刪除目前 profile；非編輯器頁回 false。</summary>
        internal bool CanDelete => InnerFrame.Content is GamepadProfileEditor e && e.CanDelete;

        /// <summary>清單頁是否有任何項目；非清單頁回 false。</summary>
        internal bool HasItems => InnerFrame.Content is GamepadProfileListView l && l.HasItems;

        /// <summary>清單頁搜尋方塊是否擁有焦點；非清單頁回 false。</summary>
        internal bool IsSearchBoxFocused => InnerFrame.Content is GamepadProfileListView l && l.IsSearchBoxFocused;

        // ── 對外命令（外層手把 handler 與提示列按鈕轉入）──────────────────────

        /// <summary>B 鍵：編輯器頁存檔並返回（返回由 Editor 的 Closed 事件驅動）。回傳是否已處理。</summary>
        internal bool TrySave()
        {
            if (InnerFrame.Content is GamepadProfileEditor e) { e.Save(); return true; }
            return false;
        }

        /// <summary>X 鍵：編輯器頁刪目前 profile、清單頁刪選中項。回傳是否已處理。</summary>
        internal bool TryDelete()
        {
            if (InnerFrame.Content is GamepadProfileEditor e) { if (e.CanDelete) e.DeleteCurrent(); return true; }
            if (InnerFrame.Content is GamepadProfileListView l) { _ = l.DeleteSelectedAsync(); return true; }
            return false;
        }

        /// <summary>A 鍵：清單頁編輯目前焦點列（編輯器頁不在此處理）。</summary>
        internal void EditSelected()
        {
            if (InnerFrame.Content is GamepadProfileListView l) l.EditSelected();
        }

        /// <summary>LB 鍵：清單頁往上跳一頁。</summary>
        internal void PageUp()
        {
            if (InnerFrame.Content is GamepadProfileListView l) l.PageUp();
        }

        /// <summary>RB 鍵：清單頁往下跳一頁。</summary>
        internal void PageDown()
        {
            if (InnerFrame.Content is GamepadProfileListView l) l.PageDown();
        }

        /// <summary>Y 鍵：清單頁在搜尋方塊與清單之間切換焦點。</summary>
        internal void ToggleSearchFocus()
        {
            if (InnerFrame.Content is not GamepadProfileListView l) return;
            if (l.IsSearchBoxFocused) l.FocusList();
            else l.FocusSearchBox();
        }

        /// <summary>把焦點程式化設給清單（外層在無 pending 編輯時呼叫）。</summary>
        internal void FocusList()
        {
            if (InnerFrame.Content is GamepadProfileListView l) l.FocusList();
        }
    }
}
