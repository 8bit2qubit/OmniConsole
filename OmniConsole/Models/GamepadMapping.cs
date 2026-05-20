using System;
using System.Collections.Generic;

namespace OmniConsole.Models
{
    /// <summary>可映射的 XInput 輸入位識別碼（共 16 個：A/B/X/Y、LB/RB、LT/RT、LS/RS、DPad 4 向、LStick/RStick）。</summary>
    public enum GamepadInputId
    {
        A, B, X, Y,
        LB, RB,
        LT, RT,
        LS, RS,
        DPadUp, DPadDown, DPadLeft, DPadRight,
        LStick, RStick
    }

    /// <summary>動作型別（與 C++ ActionKind 一一對應）。</summary>
    public enum GamepadActionKind
    {
        None,
        KeyTap,
        KeyHold,
        KeyCombo,
        MouseButton,
        MouseWheel,
        StickCursor,
        StickScroll,
        StickArrows,
        StickWasd
    }

    /// <summary>滑鼠鍵位。</summary>
    public enum GamepadMouseWhich { Left, Right, Middle }

    /// <summary>滾輪方向。</summary>
    public enum GamepadWheelDir { Up, Down, Left, Right }

    /// <summary>組合鍵的修飾鍵旗標集（可任意子集組合）。</summary>
    [Flags]
    public enum GamepadModifier
    {
        None = 0,
        Ctrl = 1 << 0,
        Shift = 1 << 1,
        Alt = 1 << 2,
        Win = 1 << 3
    }

    /// <summary>單一輸入位的動作映射。</summary>
    public sealed class GamepadAction
    {
        /// <summary>動作型別。</summary>
        public GamepadActionKind Kind { get; set; } = GamepadActionKind.None;

        /// <summary>KeyTap / KeyHold / KeyCombo 的主鍵 VK code。</summary>
        public int Vk { get; set; }

        /// <summary>KeyCombo 的修飾鍵旗標集。</summary>
        public GamepadModifier Mods { get; set; } = GamepadModifier.None;

        /// <summary>MouseButton 的鍵位。</summary>
        public GamepadMouseWhich Which { get; set; } = GamepadMouseWhich.Left;

        /// <summary>MouseWheel 的方向。</summary>
        public GamepadWheelDir Dir { get; set; } = GamepadWheelDir.Up;

        /// <summary>產生獨立的深拷貝。</summary>
        public GamepadAction Clone()
        {
            return new GamepadAction
            {
                Kind = Kind,
                Vk = Vk,
                Mods = Mods,
                Which = Which,
                Dir = Dir
            };
        }
    }

    /// <summary>一份 per-App 手把映射 profile：識別 + 顯示名稱 + 16 鍵 bindings。</summary>
    public sealed class GamepadProfile
    {
        /// <summary>App 識別（共用 type，定義於 Models/AppId.cs）。</summary>
        public AppId AppId { get; set; } = new AppId();

        /// <summary>建立當下抓到的視窗 title（純顯示用，PhantomKey 不使用）。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>16 個輸入位的映射；缺項視為 None。</summary>
        public Dictionary<GamepadInputId, GamepadAction> Bindings { get; set; } = new Dictionary<GamepadInputId, GamepadAction>();

        /// <summary>取得指定輸入位的動作；缺項回 Kind=None 的動作但不改變本物件狀態。</summary>
        public GamepadAction Get(GamepadInputId id)
        {
            if (Bindings.TryGetValue(id, out var a) && a != null) return a;
            return new GamepadAction { Kind = GamepadActionKind.None };
        }

        /// <summary>判定 profile 是否「實際全為 None」（玩家清光時可提示）。</summary>
        public bool IsEffectivelyEmpty()
        {
            foreach (var kv in Bindings)
                if (kv.Value != null && kv.Value.Kind != GamepadActionKind.None) return false;
            return true;
        }

        /// <summary>產生獨立的深拷貝。</summary>
        public GamepadProfile Clone()
        {
            var clone = new GamepadProfile
            {
                AppId = new AppId { Kind = AppId.Kind, Value = AppId.Value },
                DisplayName = DisplayName,
                Bindings = new Dictionary<GamepadInputId, GamepadAction>(Bindings.Count)
            };
            foreach (var kv in Bindings)
                clone.Bindings[kv.Key] = kv.Value?.Clone() ?? new GamepadAction();
            return clone;
        }
    }

    /// <summary>內建版面：OmniNav 的 16 鍵預設。</summary>
    public static class GamepadBuiltInLayouts
    {
        // VK 常數
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_TAB = 0x09;
        private const int VK_NEXT = 0x22;  // PageDown
        private const int VK_PRIOR = 0x21;  // PageUp
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;

        /// <summary>內建 OmniNav 配置（與 C++ MakeOmniNav() 逐鍵相同；玩家新建 profile 用此當底）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> OmniNav()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_NEXT },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_PRIOR },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Ctrl | GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Ctrl, Vk = VK_TAB },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_ESCAPE },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN },
                [GamepadInputId.LS] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_UP },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_DOWN },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_LEFT },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RIGHT },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
            };
        }

        /// <summary>內建 Classic 配置（與 C++ MakeClassic() 逐鍵相同）。</summary>
        public static Dictionary<GamepadInputId, GamepadAction> Classic()
        {
            return new Dictionary<GamepadInputId, GamepadAction>
            {
                [GamepadInputId.A] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RETURN },
                [GamepadInputId.B] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_ESCAPE },
                [GamepadInputId.X] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_NEXT },
                [GamepadInputId.Y] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_PRIOR },
                [GamepadInputId.LB] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_TAB },
                [GamepadInputId.RB] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Left },
                [GamepadInputId.LT] = new GamepadAction { Kind = GamepadActionKind.KeyCombo, Mods = GamepadModifier.Shift, Vk = VK_TAB },
                [GamepadInputId.RT] = new GamepadAction { Kind = GamepadActionKind.MouseButton, Which = GamepadMouseWhich.Right },
                [GamepadInputId.LS] = new GamepadAction { Kind = GamepadActionKind.None },
                [GamepadInputId.RS] = new GamepadAction { Kind = GamepadActionKind.None },
                [GamepadInputId.DPadUp] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_UP },
                [GamepadInputId.DPadDown] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_DOWN },
                [GamepadInputId.DPadLeft] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_LEFT },
                [GamepadInputId.DPadRight] = new GamepadAction { Kind = GamepadActionKind.KeyTap, Vk = VK_RIGHT },
                [GamepadInputId.LStick] = new GamepadAction { Kind = GamepadActionKind.StickScroll },
                [GamepadInputId.RStick] = new GamepadAction { Kind = GamepadActionKind.StickCursor },
            };
        }
    }
}
