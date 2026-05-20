#pragma once
#include <windows.h>
#include <xinput.h>
#include "Config.h"
#include "Bindings.h"

// ============================================================================
// Gamepad Mouse Mode：手把映射為滑鼠＋鍵盤輸入（查 Bindings 表）
// ============================================================================
//
// 在 Mouse Mode 啟用且前景符合條件時，由 PhantomKey 主迴圈每 tick 呼叫：
//   - Tick()           ：套用內建版面（OmniNav / Classic，由 cfg.mouseModeLayout 選），DPad 路徑補 keydown
//   - TickWithBindings()：套用玩家自訂 profile 的 Bindings，DPad 路徑純鏡像按住
// 離開該前景時呼叫 Reset() 清除累積狀態。
//
// KeyTap / KeyHold / MouseButton 走鏡像按住（按下 keydown、放開 keyup）。
// DPad 整組 / Stick Arrows / Stick WASD 走鏡像按住，內建版面額外依 OS 鍵盤重複設定補 keydown。
// KeyCombo 與 MouseWheel 為邊緣觸發：按下時送一次，不鏡像。
//
// 兩套內建版面（MakeOmniNav / MakeClassic）：
//
//   Button      | OmniNav                      | Classic
//   ------------|------------------------------|------------------------------
//   Left Stick  | Cursor                       | Scroll
//   Right Stick | Scroll                       | Cursor
//   A           | Left Click                   | Enter
//   B           | Right Click                  | Esc
//   X           | Page Down                    | Page Down
//   Y           | Page Up                      | Page Up
//   LB          | Ctrl+Shift+Tab               | Tab
//   RB          | Ctrl+Tab                     | Left Click
//   LT          | Esc                          | Shift+Tab
//   RT          | Enter                        | Right Click
//   LS          | Shift+Tab                    | —
//   RS          | Tab                          | —
//   D-pad       | Arrow Keys (hold to repeat)  | Arrow Keys (hold to repeat)
//
// 註：skipDpad=true 時跳過 D-pad → 方向鍵映射（前景對 D-pad 已有原生反應，避免雙跳）。
// ============================================================================

namespace MouseMode {

    // 取得內建版面的 Bindings（layoutName 不分大小寫，非 "Classic" 一律當 OmniNav）
    const Bindings& BuiltInBindings(const wchar_t* layoutName);

    // 套用內建版面：每 tick 呼叫；DPad 走補 keydown 路徑服務導覽類前景
    void Tick(const XINPUT_GAMEPAD& pad, const AppConfig& cfg, bool skipDpad);

    // 套用任意 Bindings（玩家自訂 profile 用）；DPad 走純鏡像按住路徑服務遊戲
    void TickWithBindings(const XINPUT_GAMEPAD& pad, const Bindings& bindings,
                          int cursorSpeedPercent, bool skipDpad);

    // 離開目標前景時清除滾輪累積、游標累積與長按/鏡像按住狀態
    void Reset();

}  // namespace MouseMode
