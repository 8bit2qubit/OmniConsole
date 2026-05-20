#pragma once
#include <windows.h>
#include <string>
#include "Bindings.h"

// ============================================================================
// 玩家自訂 per-App 手把映射 profile
// ============================================================================
//
// GamepadProfiles.json 位於 %LOCALAPPDATA%\Publishers\<PublisherHash>\OmniConsoleShared\
//（與 Shared.ini 同目錄）。C++ 端僅讀，由 C# 端 GamepadProfileStore 寫。
//
// 比對流程：先試取前景 AUMID（ApplicationFrameHost 宿主走 CoreWindow 反查宿主 pid，
//           自跑 exe 的 packaged 直接對前景 pid 取）。
//   - 取到 AUMID → 比 kind=Aumid，未命中也不回退到 process 名稱。
//   - 取不到（Win32 桌面 process）→ 強綁定 path：procName + fullPath 雙件相符才命中，
//                                    舊 name-only profile（fullPath 空）一律忽略，
//                                    由主程式 Editor 開啟時自動升級為 path-bound。
//
// AppId 為跨 store 共用識別。
// ============================================================================

// ── 識別（跨 store 共用） ──────────────────────────────────────────────────
struct AppId {
    enum class Kind { Process, Aumid };
    Kind         kind  = Kind::Process;
    std::wstring value;
    std::wstring fullPath;  // 僅 Kind=Process 適用；空字串代表 name 通配
};

// ── 一份 gamepad profile ───────────────────────────────────────────────────
struct GamepadProfile {
    AppId        appId;
    std::wstring displayName;
    Bindings     bindings{};
};

// ── 讀取與比對 ──────────────────────────────────────────────────────────────

// 從 GamepadProfiles.json 讀取所有 profile；檔案不存在或解析失敗回空 vector
std::vector<GamepadProfile> LoadGamepadProfiles();

// 回傳 GamepadProfiles.json 的最後寫入時間（FILETIME 壓成 uint64_t）；不存在回 0
unsigned long long GetGamepadProfilesLastWriteTime();

// 取前景視窗的 AUMID — 只對 ApplicationFrameHost 宿主的 UWP 有效；
// 自跑 exe 的 packaged（Notepad / SnippingTool 等）回空字串
std::wstring GetForegroundAumid(HWND hwnd);

// 依前景 process 名稱、完整路徑與 HWND 找符合的 profile；未命中回 nullptr。
// fullPath 為空時直接回 nullptr（強綁定 path、不做 name 通配）。
const GamepadProfile* FindGamepadProfileForForeground(const std::vector<GamepadProfile>& profiles,
                                                      const std::wstring& procName,
                                                      const std::wstring& fullPath,
                                                      HWND fgHwnd);
