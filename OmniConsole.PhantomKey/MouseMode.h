#pragma once
#include <windows.h>
#include <xinput.h>
#include "Config.h"

// ============================================================================
// Gamepad Mouse Mode：手把映射為滑鼠＋鍵盤輸入
// ============================================================================
//
// 在 Mouse Mode 啟用且前景為目標程式（瀏覽器、Epic）時，由 PhantomKey 主迴圈
// 每 tick 呼叫 Tick() 處理輸入。離開目標程式時呼叫 Reset() 清除累積狀態。
//
// 兩套版面配置透過 cfg.mouseModeLayout 切換：
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
// 註：D-pad 在前景對 XInput 已有原生反應的情境（如 Windows 11 檔案總管內建
// 手把導航）由呼叫端傳入 skipDpad=true 跳過，避免雙跳；其餘鍵仍由 Mouse Mode 處理。
// ============================================================================

namespace MouseMode {

    // 主迴圈每 tick 呼叫
    // skipDpad: 前景對 D-pad 已有原生反應時設為 true，跳過 D-pad → 方向鍵映射
    void Tick(const XINPUT_GAMEPAD& pad, const AppConfig& cfg, bool skipDpad);

    // 離開目標前景時清除滾輪累積與長按連發狀態
    void Reset();

}  // namespace MouseMode
