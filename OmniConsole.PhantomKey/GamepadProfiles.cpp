#include "GamepadProfiles.h"
#include "Log.h"
#include <shlobj.h>
#include <propkey.h>
#include <appmodel.h>
#include <fstream>
#include <sstream>
#include <unordered_map>

// ============================================================================
// 自訂 per-App 手把映射 profile 讀取（自製 JSON 解析器）
// ============================================================================
//
// 檔案位置：%LOCALAPPDATA%\Publishers\<PublisherHash>\OmniConsoleShared\GamepadProfiles.json
// （與 Shared.ini 同目錄；重用 Config.cpp 的 Publishers 列舉寫法）
//
// C++ 端只讀；寫由 C# 端 GamepadProfileStore 負責。
// ============================================================================

static const wchar_t* kSharedFolderName = L"OmniConsoleShared";
static const wchar_t* kProfilesFileName = L"GamepadProfiles.json";

// ── 共用 INI 目錄列舉 ──────────────────────────────────────────────────────

static std::wstring FindProfilesPath() {
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
                                 + kSharedFolderName + L"\\" + kProfilesFileName;
        DWORD attrs = GetFileAttributesW(candidate.c_str());
        if (attrs != INVALID_FILE_ATTRIBUTES && !(attrs & FILE_ATTRIBUTE_DIRECTORY)) {
            result = candidate;
            break;
        }
    } while (FindNextFileW(h, &fd));

    FindClose(h);
    return result;
}

static std::wstring GetProfilesPath() {
    static std::wstring cached;
    if (cached.empty()) cached = FindProfilesPath();
    return cached;
}

// ── 自製 JSON parser ──────────────────────────────────────────────────────
//
// 支援：object / array / string / number / bool / null + 常見跳脫（\" \\ \/ \b \f \n \r \t \uXXXX）。
// 解析失敗（語法錯誤、未預期 token）回 nullptr。

namespace json {

    struct Value;
    using Object = std::unordered_map<std::wstring, std::shared_ptr<Value>>;
    using Array  = std::vector<std::shared_ptr<Value>>;

    enum class Type { Null, Bool, Number, String, Array, Object };

    struct Value {
        Type    type = Type::Null;
        bool    b    = false;
        double  n    = 0.0;
        std::wstring s;
        Array   a;
        Object  o;
    };

    class Parser {
    public:
        explicit Parser(const std::wstring& src) : m_src(src), m_pos(0) {}

        std::shared_ptr<Value> Parse() {
            SkipWs();
            auto v = ParseValue();
            SkipWs();
            if (!v || m_pos != m_src.size()) return nullptr;
            return v;
        }

    private:
        const std::wstring& m_src;
        size_t              m_pos;

        void SkipWs() {
            while (m_pos < m_src.size()) {
                wchar_t c = m_src[m_pos];
                if (c == L' ' || c == L'\t' || c == L'\n' || c == L'\r') ++m_pos;
                else break;
            }
        }

        bool Peek(wchar_t c) {
            SkipWs();
            return m_pos < m_src.size() && m_src[m_pos] == c;
        }

        bool Consume(wchar_t c) {
            SkipWs();
            if (m_pos < m_src.size() && m_src[m_pos] == c) { ++m_pos; return true; }
            return false;
        }

        bool ConsumeLiteral(const wchar_t* lit) {
            SkipWs();
            size_t len = wcslen(lit);
            if (m_pos + len > m_src.size()) return false;
            if (m_src.compare(m_pos, len, lit) != 0) return false;
            m_pos += len;
            return true;
        }

        std::shared_ptr<Value> ParseValue() {
            SkipWs();
            if (m_pos >= m_src.size()) return nullptr;
            wchar_t c = m_src[m_pos];
            if (c == L'{') return ParseObject();
            if (c == L'[') return ParseArray();
            if (c == L'"') return ParseString();
            if (c == L't' || c == L'f') return ParseBool();
            if (c == L'n') return ParseNull();
            if (c == L'-' || (c >= L'0' && c <= L'9')) return ParseNumber();
            return nullptr;
        }

        std::shared_ptr<Value> ParseObject() {
            if (!Consume(L'{')) return nullptr;
            auto v = std::make_shared<Value>();
            v->type = Type::Object;
            if (Consume(L'}')) return v;
            while (true) {
                SkipWs();
                if (!Peek(L'"')) return nullptr;
                auto key = ParseString();
                if (!key) return nullptr;
                if (!Consume(L':')) return nullptr;
                auto val = ParseValue();
                if (!val) return nullptr;
                v->o[key->s] = val;
                if (Consume(L',')) continue;
                if (Consume(L'}')) return v;
                return nullptr;
            }
        }

        std::shared_ptr<Value> ParseArray() {
            if (!Consume(L'[')) return nullptr;
            auto v = std::make_shared<Value>();
            v->type = Type::Array;
            if (Consume(L']')) return v;
            while (true) {
                auto val = ParseValue();
                if (!val) return nullptr;
                v->a.push_back(val);
                if (Consume(L',')) continue;
                if (Consume(L']')) return v;
                return nullptr;
            }
        }

        std::shared_ptr<Value> ParseString() {
            SkipWs();
            if (m_pos >= m_src.size() || m_src[m_pos] != L'"') return nullptr;
            ++m_pos;
            auto v = std::make_shared<Value>();
            v->type = Type::String;
            while (m_pos < m_src.size()) {
                wchar_t c = m_src[m_pos++];
                if (c == L'"') return v;
                if (c == L'\\') {
                    if (m_pos >= m_src.size()) return nullptr;
                    wchar_t esc = m_src[m_pos++];
                    switch (esc) {
                        case L'"':  v->s += L'"';  break;
                        case L'\\': v->s += L'\\'; break;
                        case L'/':  v->s += L'/';  break;
                        case L'b':  v->s += L'\b'; break;
                        case L'f':  v->s += L'\f'; break;
                        case L'n':  v->s += L'\n'; break;
                        case L'r':  v->s += L'\r'; break;
                        case L't':  v->s += L'\t'; break;
                        case L'u': {
                            if (m_pos + 4 > m_src.size()) return nullptr;
                            wchar_t cp = 0;
                            for (int i = 0; i < 4; ++i) {
                                wchar_t h = m_src[m_pos++];
                                cp <<= 4;
                                if      (h >= L'0' && h <= L'9') cp |= (h - L'0');
                                else if (h >= L'a' && h <= L'f') cp |= (h - L'a' + 10);
                                else if (h >= L'A' && h <= L'F') cp |= (h - L'A' + 10);
                                else return nullptr;
                            }
                            v->s += cp;
                            break;
                        }
                        default: return nullptr;
                    }
                } else {
                    v->s += c;
                }
            }
            return nullptr;
        }

        std::shared_ptr<Value> ParseBool() {
            if (ConsumeLiteral(L"true"))  { auto v = std::make_shared<Value>(); v->type = Type::Bool; v->b = true;  return v; }
            if (ConsumeLiteral(L"false")) { auto v = std::make_shared<Value>(); v->type = Type::Bool; v->b = false; return v; }
            return nullptr;
        }

        std::shared_ptr<Value> ParseNull() {
            if (ConsumeLiteral(L"null")) { auto v = std::make_shared<Value>(); v->type = Type::Null; return v; }
            return nullptr;
        }

        std::shared_ptr<Value> ParseNumber() {
            SkipWs();
            size_t start = m_pos;
            if (m_pos < m_src.size() && m_src[m_pos] == L'-') ++m_pos;
            while (m_pos < m_src.size()) {
                wchar_t c = m_src[m_pos];
                if ((c >= L'0' && c <= L'9') || c == L'.' || c == L'e' || c == L'E' || c == L'+' || c == L'-') ++m_pos;
                else break;
            }
            if (start == m_pos) return nullptr;
            auto v = std::make_shared<Value>();
            v->type = Type::Number;
            std::wstring num = m_src.substr(start, m_pos - start);
            v->n = _wtof(num.c_str());
            return v;
        }
    };

    // 取 object 內某 key 對應的 Value；obj 不是 Object 或 key 不存在回 nullptr
    std::shared_ptr<Value> Get(const std::shared_ptr<Value>& obj, const wchar_t* key) {
        if (!obj || obj->type != Type::Object) return nullptr;
        auto it = obj->o.find(key);
        if (it == obj->o.end()) return nullptr;
        return it->second;
    }

    std::wstring GetString(const std::shared_ptr<Value>& obj, const wchar_t* key, const wchar_t* defaultVal = L"") {
        auto v = Get(obj, key);
        if (!v || v->type != Type::String) return defaultVal;
        return v->s;
    }

    int GetInt(const std::shared_ptr<Value>& obj, const wchar_t* key, int defaultVal = 0) {
        auto v = Get(obj, key);
        if (!v || v->type != Type::Number) return defaultVal;
        return (int)v->n;
    }

}  // namespace json

// ── modifier 字串 → VK 對照 ───────────────────────────────────────────────

static WORD ModifierStringToVK(const std::wstring& s) {
    if (_wcsicmp(s.c_str(), L"Ctrl")  == 0) return VK_CONTROL;
    if (_wcsicmp(s.c_str(), L"Shift") == 0) return VK_SHIFT;
    if (_wcsicmp(s.c_str(), L"Alt")   == 0) return VK_MENU;
    if (_wcsicmp(s.c_str(), L"Win")   == 0) return VK_LWIN;
    return 0;
}

// ── KeyId 字串對照 ────────────────────────────────────────────────────────

static bool ParseKeyId(const std::wstring& s, KeyId& out) {
    static const struct { const wchar_t* name; KeyId id; } kMap[] = {
        { L"A", KeyId::A }, { L"B", KeyId::B }, { L"X", KeyId::X }, { L"Y", KeyId::Y },
        { L"LB", KeyId::LB }, { L"RB", KeyId::RB },
        { L"LT", KeyId::LT }, { L"RT", KeyId::RT },
        { L"LS", KeyId::LS }, { L"RS", KeyId::RS },
        { L"DPadUp",    KeyId::DPadUp    },
        { L"DPadDown",  KeyId::DPadDown  },
        { L"DPadLeft",  KeyId::DPadLeft  },
        { L"DPadRight", KeyId::DPadRight },
        { L"LStick", KeyId::LStick }, { L"RStick", KeyId::RStick }
    };
    for (const auto& m : kMap) {
        if (_wcsicmp(s.c_str(), m.name) == 0) { out = m.id; return true; }
    }
    return false;
}

// ── 一個 action 物件 → Action ─────────────────────────────────────────────

static Action ParseAction(const std::shared_ptr<json::Value>& obj) {
    Action a;
    std::wstring kind = json::GetString(obj, L"kind");
    if (kind.empty()) return a;

    if (_wcsicmp(kind.c_str(), L"None") == 0) {
        a.kind = ActionKind::None;
    } else if (_wcsicmp(kind.c_str(), L"KeyTap") == 0) {
        a.kind = ActionKind::KeyTap;
        a.vk = (WORD)json::GetInt(obj, L"vk");
    } else if (_wcsicmp(kind.c_str(), L"KeyHold") == 0) {
        a.kind = ActionKind::KeyHold;
        a.vk = (WORD)json::GetInt(obj, L"vk");
    } else if (_wcsicmp(kind.c_str(), L"KeyCombo") == 0) {
        a.kind = ActionKind::KeyCombo;
        a.vk = (WORD)json::GetInt(obj, L"vk");
        auto modsV = json::Get(obj, L"mods");
        if (modsV && modsV->type == json::Type::Array) {
            for (const auto& m : modsV->a) {
                if (m && m->type == json::Type::String) {
                    WORD vk = ModifierStringToVK(m->s);
                    if (vk) a.mods.push_back(vk);
                }
            }
        }
    } else if (_wcsicmp(kind.c_str(), L"MouseButton") == 0) {
        a.kind = ActionKind::MouseButton;
        std::wstring w = json::GetString(obj, L"which", L"Left");
        if      (_wcsicmp(w.c_str(), L"Right")  == 0) a.which = MouseWhich::Right;
        else if (_wcsicmp(w.c_str(), L"Middle") == 0) a.which = MouseWhich::Middle;
        else                                          a.which = MouseWhich::Left;
    } else if (_wcsicmp(kind.c_str(), L"MouseWheel") == 0) {
        a.kind = ActionKind::MouseWheel;
        std::wstring d = json::GetString(obj, L"dir", L"Up");
        if      (_wcsicmp(d.c_str(), L"Down")  == 0) a.dir = WheelDir::Down;
        else if (_wcsicmp(d.c_str(), L"Left")  == 0) a.dir = WheelDir::Left;
        else if (_wcsicmp(d.c_str(), L"Right") == 0) a.dir = WheelDir::Right;
        else                                         a.dir = WheelDir::Up;
    } else if (_wcsicmp(kind.c_str(), L"StickCursor") == 0) {
        a.kind = ActionKind::StickCursor;
    } else if (_wcsicmp(kind.c_str(), L"StickScroll") == 0) {
        a.kind = ActionKind::StickScroll;
    } else if (_wcsicmp(kind.c_str(), L"StickArrows") == 0) {
        a.kind = ActionKind::StickArrows;
    } else if (_wcsicmp(kind.c_str(), L"StickWasd") == 0) {
        a.kind = ActionKind::StickWasd;
    }
    return a;
}

// ── 公開介面 ──────────────────────────────────────────────────────────────

std::vector<GamepadProfile> LoadGamepadProfiles() {
    std::vector<GamepadProfile> result;
    auto path = GetProfilesPath();
    if (path.empty()) return result;

    // 讀檔（UTF-8）
    std::ifstream ifs(path, std::ios::binary);
    if (!ifs) return result;
    std::stringstream ss;
    ss << ifs.rdbuf();
    std::string utf8 = ss.str();
    if (utf8.empty()) return result;

    // UTF-8 → wstring
    int needed = MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), nullptr, 0);
    if (needed <= 0) return result;
    std::wstring src;
    src.resize(needed);
    MultiByteToWideChar(CP_UTF8, 0, utf8.data(), (int)utf8.size(), &src[0], needed);

    json::Parser parser(src);
    auto root = parser.Parse();
    if (!root || root->type != json::Type::Object) {
        Log(L"[GamepadProfiles] JSON parse failed: %s", path.c_str());
        return result;
    }

    auto profilesV = json::Get(root, L"profiles");
    if (!profilesV || profilesV->type != json::Type::Array) return result;

    for (const auto& p : profilesV->a) {
        if (!p || p->type != json::Type::Object) continue;
        GamepadProfile prof;

        auto appIdV = json::Get(p, L"appId");
        if (!appIdV || appIdV->type != json::Type::Object) continue;
        std::wstring kind  = json::GetString(appIdV, L"kind");
        std::wstring value = json::GetString(appIdV, L"value");
        if (value.empty()) continue;
        prof.appId.kind     = (_wcsicmp(kind.c_str(), L"aumid") == 0) ? AppId::Kind::Aumid : AppId::Kind::Process;
        prof.appId.value    = value;
        prof.appId.fullPath = json::GetString(appIdV, L"fullPath");  // 缺欄位回空字串 → 行為等同 name 通配

        prof.displayName = json::GetString(p, L"displayName");

        auto bindingsV = json::Get(p, L"bindings");
        if (bindingsV && bindingsV->type == json::Type::Object) {
            for (const auto& kv : bindingsV->o) {
                KeyId id;
                if (!ParseKeyId(kv.first, id)) continue;
                if (!kv.second || kv.second->type != json::Type::Object) continue;
                At(prof.bindings, id) = ParseAction(kv.second);
            }
        }
        result.push_back(std::move(prof));
    }
    Log(L"[GamepadProfiles] Loaded %d profile(s) from %s", (int)result.size(), path.c_str());
    return result;
}

unsigned long long GetGamepadProfilesLastWriteTime() {
    auto path = GetProfilesPath();
    if (path.empty()) return 0;
    WIN32_FILE_ATTRIBUTE_DATA attr = {};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &attr)) return 0;
    return ((unsigned long long)attr.ftLastWriteTime.dwHighDateTime << 32)
         | attr.ftLastWriteTime.dwLowDateTime;
}

// 取前景視窗的 AUMID — SHGetPropertyStoreForWindow + PKEY_AppUserModel_ID
// 注意：只對 ApplicationFrameHost 宿主 UWP 有效；自跑 exe 的 packaged（Notepad / SnippingTool 等）回空字串
std::wstring GetForegroundAumid(HWND hwnd) {
    if (!hwnd) return L"";
    IPropertyStore* store = nullptr;
    HRESULT hr = SHGetPropertyStoreForWindow(hwnd, IID_PPV_ARGS(&store));
    if (FAILED(hr) || !store) return L"";

    std::wstring result;
    PROPVARIANT pv;
    PropVariantInit(&pv);
    hr = store->GetValue(PKEY_AppUserModel_ID, &pv);
    if (SUCCEEDED(hr) && pv.vt == VT_LPWSTR && pv.pwszVal) {
        result = pv.pwszVal;
    }
    PropVariantClear(&pv);
    store->Release();
    return result;
}

// 由 process handle 直接取 AUMID（涵蓋自跑 exe 的 packaged，例 Notepad / SnippingTool / WindowsTerminal）
// 桌面 process 回 APPMODEL_ERROR_NO_PACKAGE，aumid 為空字串。
// 對 ApplicationFrameHost.exe 自己呼會回 host 自己的 AUMID 而非宿主 UWP；由 FindGamepadProfileForForeground 上層特殊處理。
static std::wstring GetAumidFromProcess(DWORD pid) {
    if (pid == 0) return L"";
    HANDLE hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (hp == nullptr) return L"";

    UINT32 len = 0;
    LONG rc = GetApplicationUserModelId(hp, &len, nullptr);
    if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0) { CloseHandle(hp); return L""; }
    std::wstring aumid(len, L'\0');
    rc = GetApplicationUserModelId(hp, &len, aumid.data());
    CloseHandle(hp);
    if (rc != ERROR_SUCCESS) return L"";
    while (!aumid.empty() && aumid.back() == L'\0') aumid.pop_back();
    return aumid;
}

// ApplicationFrameHost 宿主 UWP（Xbox / 設定 / 小算盤）：列舉子視窗找 CoreWindow，回它的 pid（0 = 沒找到）
struct FindCoreWindowCtx { DWORD pid = 0; };
static BOOL CALLBACK FindCoreWindowProc(HWND hwndChild, LPARAM lParam) {
    WCHAR cls[64] = {};
    GetClassNameW(hwndChild, cls, _countof(cls));
    if (_wcsicmp(cls, L"Windows.UI.Core.CoreWindow") == 0) {
        DWORD childPid = 0;
        GetWindowThreadProcessId(hwndChild, &childPid);
        if (childPid != 0) {
            auto* ctx = reinterpret_cast<FindCoreWindowCtx*>(lParam);
            ctx->pid = childPid;
            return FALSE;
        }
    }
    return TRUE;
}
static DWORD GetHostedUwpPid(HWND frameHwnd) {
    FindCoreWindowCtx ctx;
    EnumChildWindows(frameHwnd, FindCoreWindowProc, reinterpret_cast<LPARAM>(&ctx));
    return ctx.pid;
}

// 路徑正規化：小寫 + 反斜線統一；空字串維持空。
static std::wstring NormalizePath(const std::wstring& path) {
    std::wstring out = path;
    for (auto& c : out) {
        if (c == L'/') c = L'\\';
        else c = (wchar_t)towlower(c);
    }
    return out;
}

const GamepadProfile* FindGamepadProfileForForeground(const std::vector<GamepadProfile>& profiles,
                                           const std::wstring& procName,
                                           const std::wstring& fullPath,
                                           HWND fgHwnd) {
    if (procName.empty()) return nullptr;

    // ── 取前景 packaged AUMID（涵蓋兩種型態）──
    //   1. ApplicationFrameHost 宿主（Xbox / 設定 / 小算盤）：列舉子視窗找 CoreWindow，取宿主 pid
    //   2. 自跑 exe 的 packaged（Notepad / SnippingTool / WindowsTerminal / OmniConsole 等）：直接對前景 pid 取
    // 桌面 process：GetApplicationUserModelId 回 APPMODEL_ERROR_NO_PACKAGE → aumid 空字串，落到下方 process 名稱比對
    DWORD aumidPid = 0;
    GetWindowThreadProcessId(fgHwnd, &aumidPid);
    if (_wcsicmp(procName.c_str(), L"ApplicationFrameHost") == 0) {
        DWORD hostedPid = GetHostedUwpPid(fgHwnd);
        if (hostedPid != 0) aumidPid = hostedPid;
    }
    std::wstring aumid = GetAumidFromProcess(aumidPid);
    if (!aumid.empty()) {
        for (const auto& p : profiles) {
            if (p.appId.kind == AppId::Kind::Aumid &&
                _wcsicmp(p.appId.value.c_str(), aumid.c_str()) == 0) {
                return &p;
            }
        }
        // packaged 取到 AUMID 但 profile 未命中 → 不再回退到 process 名稱（避免誤命中同名 Win32 profile）
        return nullptr;
    }

    // Win32：強綁定 path，只認 procName + fullPath 雙件命中（不做 name 通配回退）
    // 舊 name-only profile 在 store 裡仍會被讀進來，但這裡比對時忽略 — 主程式 Editor 開啟時自動升級為 path-bound
    if (fullPath.empty()) return nullptr;
    std::wstring fgPathNorm = NormalizePath(fullPath);
    for (const auto& p : profiles) {
        if (p.appId.kind != AppId::Kind::Process) continue;
        if (p.appId.fullPath.empty()) continue;
        if (_wcsicmp(p.appId.value.c_str(), procName.c_str()) != 0) continue;
        if (NormalizePath(p.appId.fullPath) == fgPathNorm) return &p;
    }
    return nullptr;
}
