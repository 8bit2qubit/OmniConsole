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
