using Windows.ApplicationModel.Resources;

namespace OmniConsole.Services
{
    /// <summary>
    /// ResourceLoader 的容錯 i18n 查詢 extension。把 .resw 查詢「key 可能裸寫或帶 /Text、/Content 後綴，
    /// 且查無時會擲出例外」的麻煩收斂成單一 <see cref="Loc"/>：自動試多個候選、查不到回 key 字面、不擲出例外。
    /// </summary>
    public static class ResourceLoaderExtensions
    {
        // 查詢順序：先查外掛翻譯覆寫層（社群語言）、miss 才走官方編譯 resw。
        // 兩條路徑末端都會把品牌佔位符 {OmniConsole} 換成品牌（官方 .resw 也用佔位、內外一致）。
        // 此檔提供 Loc：UWP / WinAppSDK 兩 loader 同名、靠 this 型別重載並存，查無回 key 字面、不擲出例外。
        // （widget 端另有帶回退參數的版本，供 code-behind 動態組字串防缺鍵。）

        /// <summary>resw 查詢：先查外掛翻譯，miss 才依 key / .Text / .Content 三候選試查官方 resw，皆查不到回 key 字面，try/catch 包住例外。</summary>
        public static string Loc(this ResourceLoader resw, string key)
        {
            if (PhantomLocalizer.Instance.TryGetString(key, out var plug)) return plug;

            string[] candidates = { key, key + "/Text", key + "/Content" };
            foreach (var c in candidates)
            {
                try
                {
                    var s = resw.GetString(c);
                    if (!string.IsNullOrEmpty(s)) return PhantomLocalizer.ApplyBrand(s);
                }
                catch { }
            }
            return key;
        }

        /// <summary>
        /// WinAppSDK ResourceLoader（Microsoft.Windows.ApplicationModel.Resources）版；
        /// 與上方 UWP loader 的 <see cref="Loc"/> 同名、靠 this 型別重載並存（兩 loader 型別同名不同 namespace），行為一致。
        /// </summary>
        public static string Loc(
            this Microsoft.Windows.ApplicationModel.Resources.ResourceLoader resw, string key)
        {
            if (PhantomLocalizer.Instance.TryGetString(key, out var plug)) return plug;

            string[] candidates = { key, key + "/Text", key + "/Content" };
            foreach (var c in candidates)
            {
                try
                {
                    var s = resw.GetString(c);
                    if (!string.IsNullOrEmpty(s)) return PhantomLocalizer.ApplyBrand(s);
                }
                catch { }
            }
            return key;
        }
    }
}
