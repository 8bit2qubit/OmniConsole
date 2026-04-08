#pragma once
#include <string>

// ============================================================================
// Steam 路徑偵測 + Overlay 設定讀取
// ============================================================================

struct SteamOverlayConfig {
    bool overlayEnabled = true;                         // EnableGameOverlay（不存在=啟用，"0"=停用）
    std::wstring overlayShortcut = L"Shift+Tab";        // InGameOverlayShortcutKey（不存在=Shift+Tab）
};

// 偵測 Steam 安裝路徑，讀取當前使用者的 Overlay 設定
SteamOverlayConfig ReadSteamOverlayConfig();
