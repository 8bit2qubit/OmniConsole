using Windows.ApplicationModel.Resources;

namespace OmniConsole.Services
{
    /// <summary>
    /// ResourceLoader 的容錯 i18n 查詢 extension。把 .resw 查詢「key 可能裸寫或帶 /Text、/Content 後綴，
    /// 且查無時會擲出例外」的麻煩收斂成單一 <see cref="Loc"/>：自動試多個候選、查不到回 key 字面、不擲出例外。
    /// </summary>
    public static class ResourceLoaderExtensions
    {
        /// <summary>resw 查詢；依 key 本身 / .Text / .Content 三候選試查，皆查不到時回 key 字面，try/catch 包住例外。</summary>
        public static string Loc(this ResourceLoader resw, string key)
        {
            string[] candidates = { key, key + "/Text", key + "/Content" };
            foreach (var c in candidates)
            {
                try
                {
                    var s = resw.GetString(c);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                catch { }
            }
            return key;
        }
    }
}
