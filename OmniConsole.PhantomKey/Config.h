#pragma once
#include <string>

// ============================================================================
// OmniConsole.ini 共用設定讀取
// ============================================================================

struct AppConfig {
    std::wstring defaultPlatform;     // [General] DefaultPlatform
    bool steamOverlayEnabled;         // [PhantomKey] SteamInGameOverlayEnabled
};

// 讀取 OmniConsole.ini（先嘗試 MSIX 沙箱路徑，再 fallback 至 %LocalAppData%）
AppConfig ReadConfig();
