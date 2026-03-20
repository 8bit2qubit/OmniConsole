using OmniConsole.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace OmniConsole.Services
{
    /// <summary>
    /// 管理使用者自訂平台的 JSON 持久化與新增、查詢、更新、刪除操作。
    /// LocalFolder 位置：%LOCALAPPDATA%\Packages\{PackageFamilyName}\LocalState\
    /// 資料儲存於 LocalFolder/CustomPlatforms.json。
    /// 圖示儲存於 LocalFolder/PlatformIcons/。
    /// </summary>
    public static class UserPlatformStore
    {
        private const string FileName = "CustomPlatforms.json";

        private static List<UserPlatformEntry> _entries = [];
        private static bool _loaded = false;

        /// <summary>
        /// 取得所有使用者自訂平台（轉換為 PlatformDefinition）。
        /// </summary>
        public static IReadOnlyList<PlatformDefinition> GetAllDefinitions()
        {
            EnsureLoaded();
            return _entries.Select(e => e.ToPlatformDefinition()).ToList();
        }

        /// <summary>
        /// 取得所有使用者自訂平台的原始資料。
        /// </summary>
        public static IReadOnlyList<UserPlatformEntry> GetAllEntries()
        {
            EnsureLoaded();
            return _entries.AsReadOnly();
        }

        /// <summary>
        /// 以 Id 查找使用者平台定義。
        /// </summary>
        public static PlatformDefinition? FindById(string id)
        {
            EnsureLoaded();
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            return entry?.ToPlatformDefinition();
        }

        /// <summary>
        /// 以 Id 查找使用者平台原始資料。
        /// </summary>
        public static UserPlatformEntry? FindEntryById(string id)
        {
            EnsureLoaded();
            return _entries.FirstOrDefault(e => e.Id == id);
        }

        /// <summary>
        /// 新增使用者自訂平台，自動產生唯一 Id。
        /// </summary>
        public static void Add(UserPlatformEntry entry)
        {
            EnsureLoaded();

            if (string.IsNullOrEmpty(entry.Id))
            {
                entry.Id = $"user_{Guid.NewGuid():N}";
            }

            _entries.Add(entry);
            Save();
        }

        /// <summary>
        /// 更新既有的使用者自訂平台。
        /// </summary>
        public static void Update(UserPlatformEntry entry)
        {
            EnsureLoaded();

            int index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index >= 0)
            {
                _entries[index] = entry;
                Save();
            }
        }

        /// <summary>
        /// 刪除指定 Id 的使用者自訂平台，同時清除對應的圖示檔案。
        /// </summary>
        public static void Delete(string id)
        {
            EnsureLoaded();
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry != null && !string.IsNullOrEmpty(entry.IconFileName))
            {
                DeleteIconFile(entry.IconFileName);
            }
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }

        /// <summary>
        /// 將使用者圖示檔案縮放至 800x560 後儲存為 PNG 至 LocalFolder/PlatformIcons/。
        /// 直接拉伸至目標尺寸，避免過大圖檔造成問題。
        /// </summary>
        public static async Task<string> ImportIconAsync(StorageFile sourceFile)
        {
            var localFolder = ApplicationData.Current.LocalFolder;
            var iconFolder = await localFolder.CreateFolderAsync("PlatformIcons", CreationCollisionOption.OpenIfExists);

            string fileName = $"{Guid.NewGuid():N}.png";
            var destFile = await iconFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            // 解碼來源圖片
            using (IRandomAccessStream sourceStream = await sourceFile.OpenAsync(FileAccessMode.Read))
            {
                var decoder = await BitmapDecoder.CreateAsync(sourceStream);

                // 縮放至 800x560 並編碼為 PNG
                using (IRandomAccessStream destStream = await destFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(destStream, decoder);
                    encoder.BitmapTransform.ScaledWidth = 800;
                    encoder.BitmapTransform.ScaledHeight = 560;
                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                    await encoder.FlushAsync();
                }
            }

            return fileName;
        }

        /// <summary>
        /// 刪除 LocalFolder/PlatformIcons/ 中的指定圖示檔案。
        /// </summary>
        public static void DeleteIconFile(string iconFileName)
        {
            try
            {
                string iconPath = Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, "PlatformIcons", iconFileName);
                if (File.Exists(iconPath))
                    File.Delete(iconPath);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[UserPlatformStore] Delete icon failed: {ex.Message}");
            }
        }

        // ── 內部方法 ──────────────────────────────────────────────────────────

        /// <summary>延遲初始化：首次呼叫時從 LocalFolder 讀取 JSON 並反序列化至 <see cref="_entries"/>；後續呼叫直接返回。</summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                string filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var entries = JsonSerializer.Deserialize(json, UserPlatformJsonContext.Default.UserPlatformEntryArray);
                    _entries = entries?.ToList() ?? [];
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[UserPlatformStore] Load failed: {ex.Message}");
                _entries = [];
            }
        }

        /// <summary>將目前 <see cref="_entries"/> 序列化為 JSON 並寫入 LocalFolder；失敗時僅記錄 Debug 訊息，不拋例外。</summary>
        private static void Save()
        {
            try
            {
                string filePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_entries.ToArray(), UserPlatformJsonContext.Default.UserPlatformEntryArray);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                DebugLogger.Log($"[UserPlatformStore] Save failed: {ex.Message}");
            }
        }
    }
}
