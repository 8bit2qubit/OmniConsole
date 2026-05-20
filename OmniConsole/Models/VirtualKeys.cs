using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>VK picker 顯示用的分組。</summary>
    public enum VirtualKeyGroup
    {
        Letters,
        Digits,
        FunctionKeys,
        EditNav,
        Control,
        Symbols,
        Numpad,
        Media,
        Modifiers
    }

    /// <summary>單一 VK 條目（顯示名稱 + 分組）。</summary>
    public sealed class VirtualKeyEntry
    {
        /// <summary>Win32 virtual-key code。</summary>
        public int Vk { get; }

        /// <summary>顯示分組。</summary>
        public VirtualKeyGroup Group { get; }

        /// <summary>resw key（用於在地化顯示名稱）；null 表示直接用 FallbackText 字面值。</summary>
        public string? ReswKey { get; }

        /// <summary>resw 取不到時的回退字面文字。</summary>
        public string FallbackText { get; }

        /// <summary>建立一個 VK 條目。</summary>
        public VirtualKeyEntry(int vk, VirtualKeyGroup group, string? reswKey, string fallbackText)
        {
            Vk = vk;
            Group = group;
            ReswKey = reswKey;
            FallbackText = fallbackText;
        }
    }

    /// <summary>
    /// 全鍵盤 VK 目錄（給「改鍵」picker 列出所有可選 VK）。
    /// 字母 / 數字 / F1–F24 不進 resw（直接用字面字元 / "F1"…），其他 VK 走 ReswKey。
    /// </summary>
    public static class VirtualKeys
    {
        /// <summary>依分組順序列出所有 VK 條目。</summary>
        public static readonly List<VirtualKeyEntry> All = BuildAll();

        private static List<VirtualKeyEntry> BuildAll()
        {
            var list = new List<VirtualKeyEntry>();

            // ── Letters A–Z（0x41–0x5A，字面字元）──
            for (int v = 0x41; v <= 0x5A; v++)
            {
                char c = (char)v;
                list.Add(new VirtualKeyEntry(v, VirtualKeyGroup.Letters, null, c.ToString()));
            }

            // ── Digits 0–9（主鍵盤上排 0x30–0x39，字面字元）──
            for (int v = 0x30; v <= 0x39; v++)
            {
                char c = (char)v;
                list.Add(new VirtualKeyEntry(v, VirtualKeyGroup.Digits, null, c.ToString()));
            }

            // ── F1–F24（VK_F1 = 0x70 起，字面 F# 不進 resw）──
            for (int i = 1; i <= 24; i++)
                list.Add(new VirtualKeyEntry(0x70 + (i - 1), VirtualKeyGroup.FunctionKeys, null, "F" + i.ToString()));

            // ── 編輯/導覽 ──
            list.Add(new VirtualKeyEntry(0x2D, VirtualKeyGroup.EditNav, "Vk_Insert", "Insert"));
            list.Add(new VirtualKeyEntry(0x2E, VirtualKeyGroup.EditNav, "Vk_Delete", "Delete"));
            list.Add(new VirtualKeyEntry(0x24, VirtualKeyGroup.EditNav, "Vk_Home", "Home"));
            list.Add(new VirtualKeyEntry(0x23, VirtualKeyGroup.EditNav, "Vk_End", "End"));
            list.Add(new VirtualKeyEntry(0x21, VirtualKeyGroup.EditNav, "Vk_PageUp", "Page Up"));
            list.Add(new VirtualKeyEntry(0x22, VirtualKeyGroup.EditNav, "Vk_PageDown", "Page Down"));
            list.Add(new VirtualKeyEntry(0x26, VirtualKeyGroup.EditNav, "Vk_ArrowUp", "↑"));
            list.Add(new VirtualKeyEntry(0x28, VirtualKeyGroup.EditNav, "Vk_ArrowDown", "↓"));
            list.Add(new VirtualKeyEntry(0x25, VirtualKeyGroup.EditNav, "Vk_ArrowLeft", "←"));
            list.Add(new VirtualKeyEntry(0x27, VirtualKeyGroup.EditNav, "Vk_ArrowRight", "→"));

            // ── 控制鍵 ──
            list.Add(new VirtualKeyEntry(0x0D, VirtualKeyGroup.Control, "Vk_Enter", "Enter"));
            list.Add(new VirtualKeyEntry(0x1B, VirtualKeyGroup.Control, "Vk_Escape", "Esc"));
            list.Add(new VirtualKeyEntry(0x09, VirtualKeyGroup.Control, "Vk_Tab", "Tab"));
            list.Add(new VirtualKeyEntry(0x08, VirtualKeyGroup.Control, "Vk_Backspace", "Backspace"));
            list.Add(new VirtualKeyEntry(0x20, VirtualKeyGroup.Control, "Vk_Space", "Space"));
            list.Add(new VirtualKeyEntry(0x2C, VirtualKeyGroup.Control, "Vk_PrintScreen", "PrtSc"));
            list.Add(new VirtualKeyEntry(0x13, VirtualKeyGroup.Control, "Vk_Pause", "Pause"));
            list.Add(new VirtualKeyEntry(0x14, VirtualKeyGroup.Control, "Vk_CapsLock", "Caps Lock"));
            list.Add(new VirtualKeyEntry(0x5D, VirtualKeyGroup.Control, "Vk_Apps", "Apps"));

            // ── 美式佈局符號鍵 ──
            list.Add(new VirtualKeyEntry(0xC0, VirtualKeyGroup.Symbols, null, "`"));
            list.Add(new VirtualKeyEntry(0xBD, VirtualKeyGroup.Symbols, null, "-"));
            list.Add(new VirtualKeyEntry(0xBB, VirtualKeyGroup.Symbols, null, "="));
            list.Add(new VirtualKeyEntry(0xDB, VirtualKeyGroup.Symbols, null, "["));
            list.Add(new VirtualKeyEntry(0xDD, VirtualKeyGroup.Symbols, null, "]"));
            list.Add(new VirtualKeyEntry(0xDC, VirtualKeyGroup.Symbols, null, "\\"));
            list.Add(new VirtualKeyEntry(0xBA, VirtualKeyGroup.Symbols, null, ";"));
            list.Add(new VirtualKeyEntry(0xDE, VirtualKeyGroup.Symbols, null, "'"));
            list.Add(new VirtualKeyEntry(0xBC, VirtualKeyGroup.Symbols, null, ","));
            list.Add(new VirtualKeyEntry(0xBE, VirtualKeyGroup.Symbols, null, "."));
            list.Add(new VirtualKeyEntry(0xBF, VirtualKeyGroup.Symbols, null, "/"));

            // ── 數字鍵盤 ──
            for (int i = 0; i <= 9; i++)
                list.Add(new VirtualKeyEntry(0x60 + i, VirtualKeyGroup.Numpad, null, "Numpad " + i.ToString()));
            list.Add(new VirtualKeyEntry(0x6E, VirtualKeyGroup.Numpad, "Vk_NumpadDecimal", "Numpad ."));
            list.Add(new VirtualKeyEntry(0x6B, VirtualKeyGroup.Numpad, null, "Numpad +"));
            list.Add(new VirtualKeyEntry(0x6D, VirtualKeyGroup.Numpad, null, "Numpad -"));
            list.Add(new VirtualKeyEntry(0x6A, VirtualKeyGroup.Numpad, null, "Numpad *"));
            list.Add(new VirtualKeyEntry(0x6F, VirtualKeyGroup.Numpad, null, "Numpad /"));

            // ── 媒體 / 瀏覽器 ──
            list.Add(new VirtualKeyEntry(0xAF, VirtualKeyGroup.Media, "Vk_VolumeUp", "Volume Up"));
            list.Add(new VirtualKeyEntry(0xAE, VirtualKeyGroup.Media, "Vk_VolumeDown", "Volume Down"));
            list.Add(new VirtualKeyEntry(0xAD, VirtualKeyGroup.Media, "Vk_VolumeMute", "Mute"));
            list.Add(new VirtualKeyEntry(0xB3, VirtualKeyGroup.Media, "Vk_MediaPlayPause", "Play / Pause"));
            list.Add(new VirtualKeyEntry(0xB2, VirtualKeyGroup.Media, "Vk_MediaStop", "Stop"));
            list.Add(new VirtualKeyEntry(0xB1, VirtualKeyGroup.Media, "Vk_MediaNext", "Next Track"));
            list.Add(new VirtualKeyEntry(0xB0, VirtualKeyGroup.Media, "Vk_MediaPrev", "Previous Track"));
            list.Add(new VirtualKeyEntry(0xA6, VirtualKeyGroup.Media, "Vk_BrowserBack", "Browser Back"));
            list.Add(new VirtualKeyEntry(0xA7, VirtualKeyGroup.Media, "Vk_BrowserForward", "Browser Forward"));
            list.Add(new VirtualKeyEntry(0xA8, VirtualKeyGroup.Media, "Vk_BrowserRefresh", "Browser Refresh"));
            list.Add(new VirtualKeyEntry(0xAC, VirtualKeyGroup.Media, "Vk_BrowserHome", "Browser Home"));
            list.Add(new VirtualKeyEntry(0xB4, VirtualKeyGroup.Media, "Vk_LaunchMail", "Launch Mail"));
            list.Add(new VirtualKeyEntry(0xB5, VirtualKeyGroup.Media, "Vk_LaunchMediaSelect", "Launch Media"));

            // ── 修飾鍵（給 KeyHold）──
            list.Add(new VirtualKeyEntry(0x10, VirtualKeyGroup.Modifiers, "Vk_Shift", "Shift"));
            list.Add(new VirtualKeyEntry(0x11, VirtualKeyGroup.Modifiers, "Vk_Ctrl", "Ctrl"));
            list.Add(new VirtualKeyEntry(0x12, VirtualKeyGroup.Modifiers, "Vk_Alt", "Alt"));
            list.Add(new VirtualKeyEntry(0x5B, VirtualKeyGroup.Modifiers, "Vk_Win", "Win"));
            list.Add(new VirtualKeyEntry(0xA0, VirtualKeyGroup.Modifiers, "Vk_LShift", "Left Shift"));
            list.Add(new VirtualKeyEntry(0xA1, VirtualKeyGroup.Modifiers, "Vk_RShift", "Right Shift"));
            list.Add(new VirtualKeyEntry(0xA2, VirtualKeyGroup.Modifiers, "Vk_LCtrl", "Left Ctrl"));
            list.Add(new VirtualKeyEntry(0xA3, VirtualKeyGroup.Modifiers, "Vk_RCtrl", "Right Ctrl"));
            list.Add(new VirtualKeyEntry(0xA4, VirtualKeyGroup.Modifiers, "Vk_LAlt", "Left Alt"));
            list.Add(new VirtualKeyEntry(0xA5, VirtualKeyGroup.Modifiers, "Vk_RAlt", "Right Alt"));
            list.Add(new VirtualKeyEntry(0x5C, VirtualKeyGroup.Modifiers, "Vk_RWin", "Right Win"));

            return list;
        }

        /// <summary>依 VK 數值查 entry；找不到回 null。</summary>
        public static VirtualKeyEntry? FindByVk(int vk)
        {
            foreach (var e in All)
                if (e.Vk == vk) return e;
            return null;
        }
    }
}
