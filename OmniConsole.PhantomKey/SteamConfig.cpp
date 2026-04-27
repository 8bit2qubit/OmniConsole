#include "SteamConfig.h"
#include "VdfParser.h"
#include "Log.h"

// ============================================================================
// Steam 路徑偵測
// ============================================================================

// 嘗試以指定存取旗標讀取 Steam InstallPath
static std::wstring TryReadSteamPath(REGSAM extraFlags) {
    HKEY hKey = NULL;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, L"SOFTWARE\\Valve\\Steam", 0,
                      KEY_READ | extraFlags, &hKey) == ERROR_SUCCESS) {
        WCHAR buf[MAX_PATH] = {};
        DWORD size = sizeof(buf);
        DWORD type = 0;
        if (RegQueryValueExW(hKey, L"InstallPath", NULL, &type, (LPBYTE)buf, &size) == ERROR_SUCCESS &&
            (type == REG_SZ || type == REG_EXPAND_SZ)) {
            RegCloseKey(hKey);
            return buf;
        }
        RegCloseKey(hKey);
    }
    return L"";
}

// 從 Registry 讀取 Steam 安裝路徑（先 64-bit 登錄檔路徑，再回退 32-bit 登錄檔路徑）
static std::wstring GetSteamInstallPath() {
    // 現代 Steam（64-bit）寫在原生 64-bit 登錄檔路徑
    std::wstring path = TryReadSteamPath(KEY_WOW64_64KEY);
    if (!path.empty()) {
        Log(L"[SteamConfig] InstallPath (64-bit Registry): %s", path.c_str());
        return path;
    }

    // 回退：舊版 Steam 或 steamservice（32-bit）可能寫在 WOW6432Node
    path = TryReadSteamPath(KEY_WOW64_32KEY);
    if (!path.empty()) {
        Log(L"[SteamConfig] InstallPath (32-bit Registry): %s", path.c_str());
        return path;
    }

    Log(L"[SteamConfig] Steam install path not found in Registry.");
    return L"";
}

// ============================================================================
// 使用者 ID 偵測
// ============================================================================

// SteamID64 → SteamID32 轉換常數
static const uint64_t STEAM_ID_OFFSET = 76561197960265728ULL;

// 從 loginusers.vdf 找到 MostRecent 使用者，將 SteamID64 轉換為 SteamID32
static std::wstring FindActiveSteamId32(const std::wstring& steamPath) {
    std::wstring loginUsersPath = steamPath + L"\\config\\loginusers.vdf";
    VdfNode root = VdfParse(loginUsersPath);

    // 根節點下應有 "users" 區段
    const VdfNode* users = root.Navigate(L"users");
    if (!users) {
        Log(L"[SteamConfig] 'users' section not found in loginusers.vdf");
        return L"";
    }

    for (const auto& [id64Str, userNode] : users->children) {
        const VdfNode* mostRecent = userNode.Navigate(L"MostRecent");
        if (mostRecent && mostRecent->value == L"1") {
            // 將 SteamID64 字串轉為數字再算出 SteamID32
            uint64_t id64 = _wcstoui64(id64Str.c_str(), nullptr, 10);
            uint32_t id32 = (uint32_t)(id64 - STEAM_ID_OFFSET);
            WCHAR id32Str[32];
            wsprintfW(id32Str, L"%u", id32);
            Log(L"[SteamConfig] Active user: ID64=%s, ID32=%s", id64Str.c_str(), id32Str);
            return id32Str;
        }
    }

    Log(L"[SteamConfig] No MostRecent user found in loginusers.vdf");
    return L"";
}

// ============================================================================
// VDF 快速鍵格式轉換
// ============================================================================

// Steam VDF 的 InGameOverlayShortcutKey 格式為 Tab 分隔 + KEY_ 前綴
// 例如 "Shift\tKEY_TAB" → "Shift+Tab"
static std::wstring NormalizeOverlayShortcut(const std::wstring& raw) {
    std::wstring result;
    std::wstring token;

    for (size_t i = 0; i <= raw.size(); i++) {
        wchar_t c = (i < raw.size()) ? raw[i] : L'\t';
        if (c == L'\t') {
            // 去除前後空白
            while (!token.empty() && token.front() == L' ') token.erase(token.begin());
            while (!token.empty() && token.back() == L' ') token.pop_back();

            if (!token.empty()) {
                // 移除 KEY_ 前綴
                if (token.size() > 4 && _wcsnicmp(token.c_str(), L"KEY_", 4) == 0) {
                    token = token.substr(4);
                }
                // 首字母大寫，其餘小寫
                for (size_t j = 0; j < token.size(); j++) {
                    token[j] = (j == 0) ? towupper(token[j]) : towlower(token[j]);
                }

                if (!result.empty()) result += L"+";
                result += token;
            }
            token.clear();
        } else {
            token += c;
        }
    }

    return result.empty() ? L"Shift+Tab" : result;
}

// ============================================================================
// 公開介面
// ============================================================================

// 快取 localconfig.vdf 路徑：ReadSteamOverlayConfig() 成功定位後寫入；
// GetSteamLocalConfigLastWriteTime() 用此路徑做 mtime 監看。
// 為空時表示 Steam 未安裝 / 未登入 / 首次尚未成功讀取，mtime 查詢直接回 0。
static std::wstring g_localConfigPath;

SteamOverlayConfig ReadSteamOverlayConfig() {
    SteamOverlayConfig cfg;

    std::wstring steamPath = GetSteamInstallPath();
    if (steamPath.empty()) return cfg;

    std::wstring id32 = FindActiveSteamId32(steamPath);
    if (id32.empty()) return cfg;

    // 讀取 localconfig.vdf
    std::wstring localConfigPath = steamPath + L"\\userdata\\" + id32 + L"\\config\\localconfig.vdf";
    g_localConfigPath = localConfigPath;
    VdfNode root = VdfParse(localConfigPath);

    const VdfNode* system = root.Navigate(L"UserLocalConfigStore.system");
    if (!system) {
        Log(L"[SteamConfig] 'UserLocalConfigStore.system' not found, using defaults.");
        return cfg;
    }

    // EnableGameOverlay（不存在=啟用，"0"=停用）
    const VdfNode* enableNode = system->Navigate(L"EnableGameOverlay");
    if (enableNode) {
        cfg.overlayEnabled = (enableNode->value != L"0");
        Log(L"[SteamConfig] EnableGameOverlay=%s", enableNode->value.c_str());
    }

    // InGameOverlayShortcutKey（不存在=Shift+Tab）
    const VdfNode* shortcutNode = system->Navigate(L"InGameOverlayShortcutKey");
    if (shortcutNode && !shortcutNode->value.empty()) {
        cfg.overlayShortcut = NormalizeOverlayShortcut(shortcutNode->value);
        Log(L"[SteamConfig] OverlayShortcut raw='%s', normalized='%s'",
            shortcutNode->value.c_str(), cfg.overlayShortcut.c_str());
    }

    return cfg;
}

unsigned long long GetSteamLocalConfigLastWriteTime() {
    if (g_localConfigPath.empty()) return 0;
    WIN32_FILE_ATTRIBUTE_DATA attr = {};
    if (!GetFileAttributesExW(g_localConfigPath.c_str(), GetFileExInfoStandard, &attr)) return 0;
    return ((unsigned long long)attr.ftLastWriteTime.dwHighDateTime << 32)
         | attr.ftLastWriteTime.dwLowDateTime;
}
