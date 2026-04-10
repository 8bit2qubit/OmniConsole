#pragma once
#include <string>

// ============================================================================
// OmniConsole.ini 共用設定讀取
// ============================================================================

struct AppConfig {
    std::wstring defaultPlatform;           // [General] DefaultPlatform
    bool         steamOverlayEnabled;       // [PhantomKey] SteamInGameOverlayEnabled
    bool         mouseModeEnabled;          // [PhantomKey] MouseModeEnabled，預設 true
    std::wstring mouseModeLayout;           // [PhantomKey] MouseModeLayout，"OmniNavi"|"Classic"，預設 "OmniNavi"
    int          cursorSpeedPercent;        // [PhantomKey] CursorSpeedPercent，25/50/75/100/125/150/175/200，預設 100
    bool         hasBuiltInGamepadMapping;  // 啟動時偵測（內建廠商手把映射軟體 → Mouse Mode 自動停用），無 INI key
};

// 讀取 OmniConsole.ini（先嘗試 MSIX 沙箱路徑，再 fallback 至 %LocalAppData%）
AppConfig ReadConfig();
