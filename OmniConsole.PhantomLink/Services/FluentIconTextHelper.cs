using System.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace OmniConsole.PhantomLink.Services
{
    /// <summary>
    /// 提供 TextBlock 的 AutoFormat attached property：將文字內 Unicode 私用區（PUA, U+E000–U+F8FF）字元拆出獨立 Run
    /// 並套上 Segoe Fluent Icons 字型，其餘字元保留預設字型，以正確顯示混排於句中的 Fluent Icons 字碼。
    /// 對應 OmniConsole (WinUI 3) 版本：OmniConsole/Services/FluentIconTextHelper.cs，兩份需同步維護。
    /// </summary>
    public static class FluentIconTextHelper
    {
        // PUA BMP 範圍：U+E000–U+F8FF，Segoe Fluent Icons 與 Segoe MDL2 Assets 字碼皆落於此區段。
        private const int PuaStart = 0xE000;
        private const int PuaEnd = 0xF8FF;

        private static readonly FontFamily FluentIconFont = new FontFamily("Segoe Fluent Icons");

        /// <summary>Attached property：是否自動拆解 PUA 字碼為帶 Fluent Icons 字型的 Run。</summary>
        public static readonly DependencyProperty AutoFormatProperty =
            DependencyProperty.RegisterAttached(
                "AutoFormat",
                typeof(bool),
                typeof(FluentIconTextHelper),
                new PropertyMetadata(false, OnAutoFormatChanged));

        /// <summary>讀取 AutoFormat attached property 值。</summary>
        public static bool GetAutoFormat(DependencyObject obj) => (bool)obj.GetValue(AutoFormatProperty);

        /// <summary>寫入 AutoFormat attached property 值。</summary>
        public static void SetAutoFormat(DependencyObject obj, bool value) => obj.SetValue(AutoFormatProperty, value);

        /// <summary>依 AutoFormat 新值掛上或解掛 TextBlock 的 Loaded handler。</summary>
        private static void OnAutoFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is TextBlock tb)) return;
            if ((bool)e.NewValue)
                tb.Loaded += OnTextBlockLoaded;
            else
                tb.Loaded -= OnTextBlockLoaded;
        }

        /// <summary>Loaded handler：對 TextBlock 套用 PUA 拆 Run 處理。</summary>
        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock tb)
                ApplyFluentIcons(tb);
        }

        /// <summary>掃 TextBlock.Text 將 PUA 字元與一般字元分組，重建 Inlines 為對應的 Run 序列（會清空既有 Inlines）。</summary>
        private static void ApplyFluentIcons(TextBlock tb)
        {
            string text = tb.Text;
            if (string.IsNullOrEmpty(text)) return;
            if (!ContainsPua(text)) return;

            tb.Inlines.Clear();
            var buffer = new StringBuilder();
            bool bufferIsIcon = IsPua(text[0]);

            foreach (char c in text)
            {
                bool currentIsIcon = IsPua(c);
                if (currentIsIcon != bufferIsIcon)
                {
                    FlushBuffer(tb, buffer, bufferIsIcon);
                    bufferIsIcon = currentIsIcon;
                }
                buffer.Append(c);
            }
            FlushBuffer(tb, buffer, bufferIsIcon);
        }

        /// <summary>把緩衝區內容輸出為一個 Run；isIcon 為 true 時套 Segoe Fluent Icons 字型。</summary>
        private static void FlushBuffer(TextBlock tb, StringBuilder buffer, bool isIcon)
        {
            if (buffer.Length == 0) return;
            var run = new Run { Text = buffer.ToString() };
            if (isIcon) run.FontFamily = FluentIconFont;
            tb.Inlines.Add(run);
            buffer.Clear();
        }

        /// <summary>判斷字元是否落在 Unicode 私用區 BMP 範圍（U+E000–U+F8FF）。</summary>
        private static bool IsPua(char c) => c >= PuaStart && c <= PuaEnd;

        /// <summary>判斷字串是否含任一 PUA 字元。</summary>
        private static bool ContainsPua(string s)
        {
            foreach (char c in s) if (IsPua(c)) return true;
            return false;
        }
    }
}
