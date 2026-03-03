using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>
    /// 描述一個遊戲平台的完整定義，包含識別資訊、UI 資源與啟動策略。
    /// 所有平台實例由 <see cref="PlatformCatalog"/> 統一管理。
    /// </summary>
    public record PlatformDefinition
    {
        /// <summary>
        /// 穩定的平台識別字串，用於設定儲存與查找。
        /// 不應在平台存在期間更改此值，否則既有使用者設定將遺失。
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// 對應 .resw 資源檔的在地化名稱索引鍵（例如 "Platform_SteamBigPicture"）。
        /// </summary>
        public required string DisplayNameKey { get; init; }

        /// <summary>
        /// 平台圖示的資源路徑，格式為 ms-appx:///Assets/Platforms/xxx.png。
        /// </summary>
        public required string IconAsset { get; init; }

        /// <summary>
        /// 按優先順序排列的啟動策略清單。
        /// 執行時依序嘗試，第一個成功的策略即停止後續嘗試。
        /// </summary>
        public required IReadOnlyList<LaunchStrategy> LaunchStrategies { get; init; }

        /// <summary>
        /// 用於「可用性檢查」的策略。僅查詢是否已安裝，不觸發啟動。
        /// </summary>
        public required LaunchStrategy AvailabilityStrategy { get; init; }
    }
}
