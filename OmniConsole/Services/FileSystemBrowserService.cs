using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OmniConsole.Services
{
    /// <summary>
    /// 提供檔案系統瀏覽功能，供自製檔案選擇器使用。
    /// 與 UI 分離，方便測試與共用。
    /// </summary>
    public static class FileSystemBrowserService
    {
        /// <summary>
        /// 檔案系統項目（資料夾或檔案）。
        /// </summary>
        public record FileSystemItem(
            string Name,
            string FullPath,
            bool IsDirectory,
            long? SizeBytes,
            DateTime? LastModified);

        /// <summary>
        /// 取得所有可用的本機磁碟（固定 + 卸除式，排除網路/光碟）。
        /// </summary>
        public static IReadOnlyList<DriveInfo> GetAvailableDrives()
        {
            try
            {
                return DriveInfo.GetDrives()
                    .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                    .ToArray();
            }
            catch
            {
                return Array.Empty<DriveInfo>();
            }
        }

        /// <summary>
        /// 取得指定目錄的內容（資料夾優先，再依副檔名篩選檔案，各自依名稱排序）。
        /// </summary>
        public static (IReadOnlyList<FileSystemItem> Directories, IReadOnlyList<FileSystemItem> Files)
            GetDirectoryContents(string path, IReadOnlyList<string>? fileTypeFilters)
        {
            var directories = new List<FileSystemItem>();
            var files = new List<FileSystemItem>();

            try
            {
                foreach (var dir in Directory.GetDirectories(path).Order())
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        // 跳過隱藏和系統資料夾
                        if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                            info.Attributes.HasFlag(FileAttributes.System))
                            continue;

                        directories.Add(new FileSystemItem(
                            info.Name, info.FullName, true, null, info.LastWriteTime));
                    }
                    catch { /* 跳過無法存取的資料夾 */ }
                }
            }
            catch { /* 無法列舉目錄 */ }

            try
            {
                foreach (var file in Directory.GetFiles(path).Order())
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Attributes.HasFlag(FileAttributes.Hidden) ||
                            info.Attributes.HasFlag(FileAttributes.System))
                            continue;

                        // 副檔名篩選
                        if (fileTypeFilters != null && fileTypeFilters.Count > 0)
                        {
                            if (!fileTypeFilters.Any(f =>
                                info.Extension.Equals(f, StringComparison.OrdinalIgnoreCase)))
                                continue;
                        }

                        files.Add(new FileSystemItem(
                            info.Name, info.FullName, false, info.Length, info.LastWriteTime));
                    }
                    catch { /* 跳過無法存取的檔案 */ }
                }
            }
            catch { /* 無法列舉檔案 */ }

            return (directories, files);
        }

        /// <summary>
        /// 取得上層目錄路徑。若已在根目錄則回傳 null。
        /// </summary>
        public static string? GetParentDirectory(string path)
        {
            return Directory.GetParent(path)?.FullName;
        }

        /// <summary>
        /// 取得快速存取路徑清單（桌面、下載、文件、圖片、音樂、影片）。
        /// </summary>
        public static IReadOnlyList<(string Name, string Path)> GetQuickAccessPaths()
        {
            var paths = new List<(string Name, string Path)>();

            TryAdd(paths, "Desktop", Environment.SpecialFolder.Desktop);
            TryAdd(paths, "Downloads", GetDownloadsPath());
            TryAdd(paths, "Documents", Environment.SpecialFolder.MyDocuments);
            TryAdd(paths, "Pictures", Environment.SpecialFolder.MyPictures);
            TryAdd(paths, "Music", Environment.SpecialFolder.MyMusic);
            TryAdd(paths, "Videos", Environment.SpecialFolder.MyVideos);

            return paths;
        }

        /// <summary>
        /// 格式化檔案大小為可讀格式。
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        /// <summary>嘗試將特殊資料夾加入快速存取清單，路徑不存在時靜默略過。</summary>
        private static void TryAdd(List<(string, string)> list, string name, Environment.SpecialFolder folder)
        {
            try
            {
                var path = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    list.Add((name, path));
            }
            catch { }
        }

        /// <summary>嘗試將指定路徑加入快速存取清單，路徑為空或不存在時靜默略過。</summary>
        private static void TryAdd(List<(string, string)> list, string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                list.Add((name, path));
        }

        /// <summary>取得使用者「下載」資料夾路徑，失敗時回傳 null。</summary>
        private static string? GetDownloadsPath()
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads");
            }
            catch { return null; }
        }
    }
}
