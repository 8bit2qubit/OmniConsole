#pragma once
#include <string>

// ============================================================================
// Steam 路徑偵測 + Overlay 設定讀取
// ============================================================================

struct SteamOverlayConfig {
    bool overlayEnabled = true;                         // EnableGameOverlay（不存在=啟用，"0"=停用）
    std::wstring overlayShortcut = L"Shift+Tab";        // InGameOverlayShortcutKey（不存在=Shift+Tab）
};

// 偵測 Steam 安裝路徑，讀取目前使用者的 Overlay 設定
// 順帶快取：成功定位 localconfig.vdf 後，路徑會被快取供 GetSteamLocalConfigLastWriteTime() 使用
SteamOverlayConfig ReadSteamOverlayConfig();

// 取得目前使用者 localconfig.vdf 的 LastWriteTime（FILETIME 合併為 uint64）
// 路徑尚未確立（Steam 未安裝 / 未登入 / 首次未呼叫過 ReadSteamOverlayConfig）時回傳 0
unsigned long long GetSteamLocalConfigLastWriteTime();
