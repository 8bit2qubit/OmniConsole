using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>
    /// 自製檔案選擇器的設定選項。
    /// </summary>
    public record FilePickerOptions
    {
        /// <summary>
        /// 允許的副檔名篩選清單（例如 [".exe"] 或 [".png", ".jpg", ".jpeg", ".bmp"]）。
        /// </summary>
        public IReadOnlyList<string> FileTypeFilters { get; init; } = [];

        /// <summary>
        /// 初始目錄路徑。若為 null 則使用預設位置。
        /// </summary>
        public string? InitialDirectory { get; init; }

        /// <summary>
        /// 是否在右側面板顯示圖片預覽。
        /// </summary>
        public bool ShowImagePreview { get; init; }

        /// <summary>
        /// 篩選器的顯示名稱（例如 "Executables (*.exe)"），用於底部檔案類型顯示。
        /// </summary>
        public string? FilterDisplayName { get; init; }
    }
}
