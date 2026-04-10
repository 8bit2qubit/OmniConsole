#include "Config.h"
#include "Log.h"

// ============================================================================
// INI 設定讀取
// ============================================================================

static const wchar_t* PACKAGE_FAMILY = L"b5fbce6b-2d7d-4da0-b419-4beb30e2b808_n7gpkx2kypjte";

// 偵測裝置是否內建廠商手把映射軟體（與 Mouse Mode 衝突需停用）。
// 目前僅涵蓋 ROG Ally / Ally X / Xbox Ally 家族（Armoury Crate SE）；
// 未來新增其他家族只需擴充關鍵字清單。
static bool DetectBuiltInGamepadMapping() {
    // return false; // 測試用：略過內建映射偵測
    HKEY hKey = NULL;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
        L"HARDWARE\\DESCRIPTION\\System\\BIOS", 0, KEY_READ, &hKey) != ERROR_SUCCESS)
        return false;
    WCHAR buf[256] = {};
    DWORD size = sizeof(buf), type = 0;
    bool result = false;
    if (RegQueryValueExW(hKey, L"SystemProductName", NULL, &type, (LPBYTE)buf, &size) == ERROR_SUCCESS && type == REG_SZ) {
        // ROG Ally 家族
        static const wchar_t* kKeywords[] = { L"RC71L", L"RC72L", L"RC72LA", L"RC73XA", L"RC73YA" };
        std::wstring product(buf);
        for (auto& c : product) c = towupper(c);
        for (auto kw : kKeywords)
            if (product.find(kw) != std::wstring::npos) { result = true; break; }
        Log(L"[Config] SystemProductName=%s, hasBuiltInGamepadMapping=%d", buf, (int)result);
    }
    RegCloseKey(hKey);
    return result;
}

// MSIX 封裝的 OmniConsole 會將 INI 寫入沙箱重導路徑（Packages\...\LocalCache\Local\），
// Debug 版則寫入真正的 %LocalAppData%。先嘗試 MSIX 沙箱路徑，再 fallback 至一般路徑。
static std::wstring GetIniPath() {
    WCHAR localAppData[MAX_PATH];
    if (FAILED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, localAppData)))
        return L"";

    std::wstring base(localAppData);

    // 優先：MSIX 沙箱路徑
    std::wstring msixPath = base + L"\\Packages\\" + PACKAGE_FAMILY +
                            L"\\LocalCache\\Local\\OmniConsole\\OmniConsole.ini";
    if (GetFileAttributesW(msixPath.c_str()) != INVALID_FILE_ATTRIBUTES) {
        Log(L"[Config] Using MSIX INI: %s", msixPath.c_str());
        return msixPath;
    }

    // Fallback：非封裝（Debug）路徑
    std::wstring normalPath = base + L"\\OmniConsole\\OmniConsole.ini";
    Log(L"[Config] Using normal INI: %s", normalPath.c_str());
    return normalPath;
}

// ============================================================================
// 公開介面
// ============================================================================

AppConfig ReadConfig() {
    AppConfig cfg = {};
    cfg.steamOverlayEnabled = false;

    std::wstring iniPath = GetIniPath();
    if (iniPath.empty()) {
        Log(L"[Config] Cannot resolve INI path.");
        return cfg;
    }

    WCHAR buf[256] = {};
    GetPrivateProfileStringW(L"General", L"DefaultPlatform", L"", buf, _countof(buf), iniPath.c_str());
    cfg.defaultPlatform = buf;

    cfg.steamOverlayEnabled = GetPrivateProfileIntW(L"PhantomKey", L"SteamInGameOverlayEnabled", 1, iniPath.c_str()) != 0;

    cfg.mouseModeEnabled = GetPrivateProfileIntW(L"PhantomKey", L"MouseModeEnabled", 1, iniPath.c_str()) != 0;

    WCHAR layoutBuf[32] = {};
    GetPrivateProfileStringW(L"PhantomKey", L"MouseModeLayout", L"OmniNav",
                             layoutBuf, _countof(layoutBuf), iniPath.c_str());
    cfg.mouseModeLayout = layoutBuf;
    if (_wcsicmp(cfg.mouseModeLayout.c_str(), L"Classic") != 0)
        cfg.mouseModeLayout = L"OmniNav";  // 未知值回退至預設

    int rawPct = GetPrivateProfileIntW(L"PhantomKey", L"CursorSpeedPercent", 100, iniPath.c_str());
    static const int kValidPercents[] = { 25, 50, 75, 100, 125, 150, 175, 200 };
    cfg.cursorSpeedPercent = 100;
    for (int p : kValidPercents) if (p == rawPct) { cfg.cursorSpeedPercent = p; break; }

    cfg.hasBuiltInGamepadMapping = DetectBuiltInGamepadMapping();

    Log(L"[Config] DefaultPlatform=%s, SteamOverlay=%d, MouseMode=%d, Layout=%s, CursorSpeed=%d%%, BuiltInMapping=%d",
        cfg.defaultPlatform.c_str(), (int)cfg.steamOverlayEnabled, (int)cfg.mouseModeEnabled,
        cfg.mouseModeLayout.c_str(), cfg.cursorSpeedPercent, (int)cfg.hasBuiltInGamepadMapping);
    return cfg;
}
