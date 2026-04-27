#pragma once
#include <string>

// ============================================================================
// 共用設定讀取（PublisherCacheFolder\OmniConsoleShared\Shared.ini）
// ============================================================================

enum class MouseModeState { Off, Auto, ForceOn };

struct AppConfig {
    std::wstring   defaultPlatform;           // [General] DefaultPlatform
    bool           steamOverlayEnabled;       // [PhantomKey] SteamInGameOverlayEnabled
    MouseModeState mouseMode;                 // [PhantomKey] MouseMode，預設 Auto
    std::wstring   mouseModeLayout;           // [PhantomKey] MouseModeLayout，"OmniNav"|"Classic"，預設 "OmniNav"
    int            cursorSpeedPercent;        // [PhantomKey] CursorSpeedPercent，25/50/75/100/125/150/175/200，預設 100
    bool           hasBuiltInGamepadMapping;  // 讀取時獨立偵測 BIOS SystemProductName（ROG Ally 家族等）
};

AppConfig ReadConfig();

// 回傳 Shared.ini 的最後寫入時間（FILETIME 壓成 uint64_t）；檔案不存在回 0
unsigned long long GetSharedIniLastWriteTime();

// 將 Steam In-Game Overlay 快捷鍵寫入 Shared.ini [PhantomKey] SteamInGameOverlayShortcut；
// 內部以靜態快取比對，僅在值改變時實際寫檔，避免無謂 I/O 與 mtime 變動。
// 用途：PhantomLink Widget 透過 PhantomKeyStore 讀取此鍵，傳給 PhantomBridge 觸發 overlay。
void WriteSteamInGameOverlayShortcut(const std::wstring& shortcut);
