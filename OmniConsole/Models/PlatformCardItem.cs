namespace OmniConsole.Models
{
    /// <summary>
    /// 設定介面平台選擇卡片的資料模型。
    /// 刻意不實作 INotifyPropertyChanged，避免 Release 模式 IL Trimming 修剪事件訂閱基礎設施。
    /// IsAvailable 更新後需由外部重新指定 ItemsSource 來重新整理 OneTime 繫結。
    /// </summary>
    public class PlatformCardItem
    {
        /// <summary>對應的平台定義資料。</summary>
        public required PlatformDefinition Platform { get; init; }

        /// <summary>便捷存取平台 Id。</summary>
        public string Id => Platform.Id;

        /// <summary>便捷存取平台圖示路徑（ms-appx:///Assets/Platforms/xxx.png）。</summary>
        public string IconAsset => Platform.IconAsset;

        /// <summary>UI 顯示用的在地化名稱（由外部設定）。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>此平台是否已安裝於目前裝置上。</summary>
        public bool IsAvailable { get; set; } = true;

        /// <summary>
        /// 卡片透明度：已安裝為 1.0，未安裝為 0.2（視覺上呈現停用感）。
        /// </summary>
        public double CardOpacity => IsAvailable ? 1.0 : 0.2;
    }
}
