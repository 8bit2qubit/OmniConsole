using System;

namespace OmniConsole.Models
{
    /// <summary>App 識別來源類別。</summary>
    public enum IdKind { Process, Aumid }

    /// <summary>
    /// App 識別（Win32 process 名稱或 UWP AUMID），跨 per-App store 共用。
    /// 與 C++ 端 OmniConsole.PhantomKey/GamepadProfiles.h 內的 struct AppId 一一對應。
    /// </summary>
    public sealed class AppId : IEquatable<AppId>
    {
        /// <summary>識別來源類別。</summary>
        public IdKind Kind { get; set; }

        /// <summary>識別值；Process=不含 .exe 的程式名稱（大小寫不敏感比對）、Aumid=完整 AUMID 字串。</summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// 完整路徑（含 .exe）；僅 Kind=Process 時有效，null 代表 name 通配。
        /// 由 PhantomKey ForegroundMonitor 的 QueryFullProcessImageNameW 取得。
        /// </summary>
        public string? FullPath { get; set; }

        /// <summary>比對是否指向同一 App（Kind 相同 + Value 不分大小寫相同）。FullPath 不參與相等性，由 store 層另行處理。</summary>
        public bool Matches(AppId other)
        {
            if (other == null) return false;
            return Kind == other.Kind &&
                   string.Equals(Value ?? string.Empty, other.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>路徑正規化：小寫 + 反斜線統一；null/空字串回 null。</summary>
        public static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            return path.Replace('/', '\\').ToLowerInvariant();
        }

        /// <summary>從完整路徑取倒數第二段（檔案的父資料夾名）；無分隔符或空字串回空字串。</summary>
        public static string ExtractFolderName(string? fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return string.Empty;
            string normalized = fullPath.Replace('/', '\\');
            int lastSlash = normalized.LastIndexOf('\\');
            if (lastSlash <= 0) return string.Empty;
            string parent = normalized.Substring(0, lastSlash);
            int parentSlash = parent.LastIndexOf('\\');
            return parentSlash >= 0 ? parent.Substring(parentSlash + 1) : parent;
        }

        /// <summary>FullPath 字元數上限。</summary>
        private const int MaxFullPathLength = 1024;

        /// <summary>FullPath 字元驗證：控制字元、`<>"|*?` 一律拒絕；反斜線與冒號允許（路徑必含）。</summary>
        public static bool IsValidFullPath(string? path)
        {
            if (path == null) return true;
            if (path.Length > MaxFullPathLength) return false;
            foreach (char c in path)
            {
                if (c < 0x20) return false;
                if (c == '<' || c == '>' || c == '"' || c == '|' || c == '*' || c == '?') return false;
            }
            return true;
        }

        /// <summary>Value 字元數上限，超過視為非法。</summary>
        private const int MaxValueLength = 256;

        /// <summary>
        /// 從 "process:xxx" / "aumid:xxx" 字串解析；格式不符、長度超過 MaxValueLength、
        /// value 含控制字元或路徑分隔符／引號類字元一律回 null。
        /// </summary>
        public static AppId? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            int colon = s.IndexOf(':');
            if (colon <= 0 || colon == s.Length - 1) return null;
            string kindStr = s.Substring(0, colon);
            string value = s.Substring(colon + 1);
            if (value.Length > MaxValueLength) return null;
            if (!IsValidValue(value)) return null;
            if (string.Equals(kindStr, "process", StringComparison.OrdinalIgnoreCase))
                return new AppId { Kind = IdKind.Process, Value = value };
            if (string.Equals(kindStr, "aumid", StringComparison.OrdinalIgnoreCase))
                return new AppId { Kind = IdKind.Aumid, Value = value };
            return null;
        }

        /// <summary>
        /// Value 字元 blacklist：控制字元（ASCII 0x00–0x1F）、路徑分隔符（反斜線、斜線）、
        /// 引號類（雙引號、單引號、左右角括號）一律拒絕；其他 Unicode 字元（含中日文、空白）放行。
        /// </summary>
        private static bool IsValidValue(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20) return false;
                if (c == '\\' || c == '/') return false;
                if (c == '"' || c == '\'' || c == '<' || c == '>') return false;
            }
            return true;
        }

        /// <summary>序列化為 "process:xxx" / "aumid:xxx"。</summary>
        public override string ToString()
        {
            string kindStr = Kind == IdKind.Aumid ? "aumid" : "process";
            return kindStr + ":" + (Value ?? string.Empty);
        }

        /// <summary>同 Matches，IEquatable 介面實作。</summary>
        public bool Equals(AppId? other) => other != null && Matches(other);

        /// <summary>覆寫 object.Equals 走 Matches 比對。</summary>
        public override bool Equals(object? obj) => obj is AppId other && Matches(other);

        /// <summary>Hash 取 Kind + Value 大小寫不敏感。</summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)Kind;
                if (Value != null) h = (h * 397) ^ Value.ToLowerInvariant().GetHashCode();
                return h;
            }
        }
    }
}
