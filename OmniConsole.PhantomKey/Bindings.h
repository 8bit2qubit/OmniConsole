#pragma once
#include <array>
#include <vector>

// ============================================================================
// 手把映射資料模型（Bindings）
// ============================================================================
//
// 16 個可映射的 XInput 輸入位（KeyId；DPad 拆成 4 方向）各對應一個 Action；
// Action 由 ActionKind 與其附帶欄位（vk / mods / which / dir）構成。內建版面
// （OmniNav / Classic）以兩張 const Bindings 表呈現，玩家自訂 profile 由
// GamepadProfiles 從 JSON 讀入。
// 與 C# 端 OmniConsole/Models/GamepadMapping.cs 一一對應。
// ============================================================================

// ── KeyId：16 個可映射的 XInput 輸入位 ──────────────────────────────────────
enum class KeyId : int {
    A = 0, B, X, Y,
    LB, RB,
    LT, RT,
    LS, RS,
    DPadUp, DPadDown, DPadLeft, DPadRight,
    LStick, RStick,
    Count
};

constexpr int kKeyIdCount = static_cast<int>(KeyId::Count);  // 16

// ── ActionKind：動作型別 ────────────────────────────────────────────────────
enum class ActionKind : int {
    None = 0,        // 停用此鍵
    KeyTap,          // 單一鍵 tap
    KeyHold,         // 單一鍵按住式（press → keydown、release → keyup）
    KeyCombo,        // 任意修飾鍵子集 + 一個 vk，tap
    MouseButton,     // 滑鼠左/中/右鍵，按住式
    MouseWheel,      // 滾輪四向（上/下/左/右），每次觸發送一格 WHEEL_DELTA
    StickCursor,     // 此搖桿 → 游標移動
    StickScroll,     // 此搖桿 → 垂直/水平捲動
    StickArrows,     // 此搖桿 → 方向鍵
    StickWasd        // 此搖桿 → WASD
};

enum class MouseWhich : int { Left = 0, Right, Middle };
enum class WheelDir   : int { Up = 0, Down, Left, Right };

// ── Action：一個輸入位的映射 ─────────────────────────────────────────────────
struct Action {
    ActionKind        kind  = ActionKind::None;
    std::vector<WORD> mods;                          // KeyCombo 用：VK_CONTROL / VK_SHIFT / VK_MENU / VK_LWIN 子集
    WORD              vk    = 0;                     // KeyTap / KeyHold / KeyCombo 用
    MouseWhich        which = MouseWhich::Left;      // MouseButton 用
    WheelDir          dir   = WheelDir::Up;          // MouseWheel 用
};

using Bindings = std::array<Action, kKeyIdCount>;

// ── 存取小工具 ───────────────────────────────────────────────────────────────

// 回傳 b[k] 的可變參考
inline Action& At(Bindings& b, KeyId k) { return b[static_cast<int>(k)]; }

// 回傳 b[k] 的唯讀參考
inline const Action& At(const Bindings& b, KeyId k) { return b[static_cast<int>(k)]; }

// ── 建構小工具 ───────────────────────────────────────────────────────────────

inline Action ActNone()              { return Action{}; }
inline Action ActKeyTap(WORD vk)     { Action a; a.kind = ActionKind::KeyTap;  a.vk = vk; return a; }
inline Action ActKeyHold(WORD vk)    { Action a; a.kind = ActionKind::KeyHold; a.vk = vk; return a; }
inline Action ActKeyCombo(std::vector<WORD> mods, WORD vk) { Action a; a.kind = ActionKind::KeyCombo; a.mods = std::move(mods); a.vk = vk; return a; }
inline Action ActMouseBtn(MouseWhich w) { Action a; a.kind = ActionKind::MouseButton; a.which = w; return a; }
inline Action ActMouseWheel(WheelDir d) { Action a; a.kind = ActionKind::MouseWheel; a.dir = d; return a; }
inline Action ActStickCursor()       { Action a; a.kind = ActionKind::StickCursor; return a; }
inline Action ActStickScroll()       { Action a; a.kind = ActionKind::StickScroll; return a; }
inline Action ActStickArrows()       { Action a; a.kind = ActionKind::StickArrows; return a; }
inline Action ActStickWasd()         { Action a; a.kind = ActionKind::StickWasd;   return a; }
