#include "Config.h"
#include "Log.h"

// ============================================================================
// INI 設定讀取
// ============================================================================

static const wchar_t* PACKAGE_FAMILY = L"b5fbce6b-2d7d-4da0-b419-4beb30e2b808_n7gpkx2kypjte";

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

    Log(L"[Config] DefaultPlatform=%s, SteamOverlay=%d", cfg.defaultPlatform.c_str(), cfg.steamOverlayEnabled);
    return cfg;
}
