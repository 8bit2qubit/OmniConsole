#include "Config.h"
#include "Log.h"
#include <shlobj.h>

// ============================================================================
// 共用 INI 設定讀取（PublisherCacheFolder\OmniConsoleShared\Shared.ini）
// ============================================================================
//
// 主程式 OmniConsole 與 PhantomLink 共同寫入 PublisherCacheFolder，
// 實體路徑為 %LOCALAPPDATA%\Publishers\<PublisherHash>\OmniConsoleShared\。
// PhantomKey 透過列舉 Publishers 下的子目錄找到共用 INI。
// ============================================================================

static const wchar_t* kSharedFolderName = L"OmniConsoleShared";
static const wchar_t* kSharedIniFileName = L"Shared.ini";

// ── 解析共用 INI 路徑 ─────────────────────────────────────────────────────

static std::wstring FindSharedIniPath() {
    wchar_t localAppData[MAX_PATH] = {};
    if (FAILED(SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData)))
        return L"";

    std::wstring pubBase = std::wstring(localAppData) + L"\\Publishers";
    std::wstring pattern = pubBase + L"\\*";

    WIN32_FIND_DATAW fd = {};
    HANDLE h = FindFirstFileW(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return L"";

    std::wstring result;
    do {
        if (!(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        if (wcscmp(fd.cFileName, L".") == 0 || wcscmp(fd.cFileName, L"..") == 0) continue;

        std::wstring candidate = pubBase + L"\\" + fd.cFileName + L"\\"
                                 + kSharedFolderName + L"\\" + kSharedIniFileName;
        DWORD attrs = GetFileAttributesW(candidate.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY)) {
            result = candidate;
            break;
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return result;
}

static std::wstring GetSharedIniPath() {
    static std::wstring cached;
    if (cached.empty()) cached = FindSharedIniPath();
    return cached;
}

unsigned long long GetSharedIniLastWriteTime() {
    auto path = GetSharedIniPath();
    if (path.empty()) return 0;
    WIN32_FILE_ATTRIBUTE_DATA attr = {};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) return 0;
    return ((unsigned long long)attr.ftLastWriteTime.dwHighDateTime << 32)
         | attr.ftLastWriteTime.dwLowDateTime;
}

// ── 小工具：讀 INI 字串 / 整數 ────────────────────────────────────────────

static std::wstring ReadString(const wchar_t* section, const wchar_t* key,
                               const wchar_t* defaultVal) {
    auto path = GetSharedIniPath();
    if (path.empty()) return std::wstring(defaultVal);
    WCHAR buf[256] = {};
    GetPrivateProfileStringW(section, key, defaultVal, buf, ARRAYSIZE(buf), path.c_str());
    return std::wstring(buf);
}

static int ReadInt(const wchar_t* section, const wchar_t* key, int defaultVal) {
    auto path = GetSharedIniPath();
    if (path.empty()) return defaultVal;
    return (int)GetPrivateProfileIntW(section, key, defaultVal, path.c_str());
}

// ── 內建手把映射偵測 ─────────────────────────────────────────────────────
//
// 偵測裝置是否內建廠商手把映射軟體（與 Mouse Mode 衝突需停用）。
// 目前僅涵蓋 ROG Ally / Ally X / Xbox Ally 家族（Armoury Crate SE）。
// 主程式、PhantomKey、PhantomLink 三處各自獨立偵測，不經 INI；
// 機型清單更新時必須三處同步修改：
//   - OmniConsole/Services/SettingsService.cs (HasBuiltInGamepadMapping)
//   - OmniConsole.PhantomKey/Config.cpp (此函式)
//   - OmniConsole.PhantomLink/Services/HardwareDetection.cs (HasBuiltInGamepadMapping)
static bool DetectBuiltInGamepadMappingImpl();

static bool DetectBuiltInGamepadMapping() {
    // 硬體型號執行期不變，首次偵測後快取
    static bool cached = DetectBuiltInGamepadMappingImpl();
    return cached;
}

static bool DetectBuiltInGamepadMappingImpl() {
    HKEY hKey = nullptr;
    if (RegOpenKeyExW(HKEY_LOCAL_MACHINE,
                      L"HARDWARE\\DESCRIPTION\\System\\BIOS",
                      0, KEY_READ, &hKey) != ERROR_SUCCESS) return false;

    wchar_t buf[256] = {};
    DWORD cb = sizeof(buf), type = 0;
    LONG rc = RegQueryValueExW(hKey, L"SystemProductName", nullptr, &type,
                               reinterpret_cast<LPBYTE>(buf), &cb);
    RegCloseKey(hKey);
    if (rc != ERROR_SUCCESS || type != REG_SZ) return false;

    std::wstring upper(buf);
    for (auto& c : upper) c = (wchar_t)towupper(c);
    // ROG Ally 家族
    static const wchar_t* kKeywords[] = {
        L"RC71L", L"RC72L", L"RC72LA", L"RC73XA", L"RC73YA"
    };
    for (auto kw : kKeywords)
        if (upper.find(kw) != std::wstring::npos) return true;
    return false;
}

static MouseModeState ParseMouseMode(const std::wstring& s) {
    if (_wcsicmp(s.c_str(), L"Off") == 0)     return MouseModeState::Off;
    if (_wcsicmp(s.c_str(), L"ForceOn") == 0) return MouseModeState::ForceOn;
    return MouseModeState::Auto;
}

static const wchar_t* MouseModeToStr(MouseModeState m) {
    switch (m) {
        case MouseModeState::Off:     return L"Off";
        case MouseModeState::ForceOn: return L"ForceOn";
        default:                      return L"Auto";
    }
}

// ============================================================================
// 公開介面
// ============================================================================

AppConfig ReadConfig() {
    AppConfig cfg = {};

    cfg.defaultPlatform = ReadString(L"General", L"DefaultPlatform", L"");

    cfg.steamOverlayEnabled = ReadInt(L"PhantomKey", L"SteamInGameOverlayEnabled", 1) != 0;

    cfg.mouseMode = ParseMouseMode(ReadString(L"PhantomKey", L"MouseMode", L"Auto"));

    std::wstring layout = ReadString(L"PhantomKey", L"MouseModeLayout", L"OmniNav");
    if (_wcsicmp(layout.c_str(), L"Classic") != 0) layout = L"OmniNav";
    cfg.mouseModeLayout = layout;

    int rawPct = ReadInt(L"PhantomKey", L"CursorSpeedPercent", 100);
    static const int kValidPercents[] = { 25, 50, 75, 100, 125, 150, 175, 200 };
    cfg.cursorSpeedPercent = 100;
    for (int p : kValidPercents) if (p == rawPct) { cfg.cursorSpeedPercent = p; break; }

    cfg.hasBuiltInGamepadMapping = DetectBuiltInGamepadMapping();

    Log(L"[Config] DefaultPlatform=%s, SteamOverlay=%d, MouseMode=%s, Layout=%s, CursorSpeed=%d%%, BuiltInMapping=%d",
        cfg.defaultPlatform.c_str(), (int)cfg.steamOverlayEnabled,
        MouseModeToStr(cfg.mouseMode), cfg.mouseModeLayout.c_str(),
        cfg.cursorSpeedPercent, (int)cfg.hasBuiltInGamepadMapping);
    return cfg;
}
