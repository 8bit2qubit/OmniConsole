#include "InputSender.h"
#include <map>

// ============================================================================
// 鍵盤輸入
// ============================================================================

// 鍵名 → VK code 對照表（鍵名全小寫）
static const std::map<std::wstring, WORD>& GetKeyMap() {
    static const std::map<std::wstring, WORD> keyMap = {
        // 修飾鍵
        { L"ctrl",      VK_LCONTROL },
        { L"control",   VK_LCONTROL },
        { L"alt",       VK_LMENU },
        { L"shift",     VK_LSHIFT },

        // 常用鍵
        { L"tab",       VK_TAB },
        { L"escape",    VK_ESCAPE },
        { L"esc",       VK_ESCAPE },
        { L"space",     VK_SPACE },
        { L"enter",     VK_RETURN },
        { L"return",    VK_RETURN },
        { L"backspace", VK_BACK },

        // 導覽鍵
        { L"home",      VK_HOME },
        { L"end",       VK_END },
        { L"insert",    VK_INSERT },
        { L"delete",    VK_DELETE },
        { L"del",       VK_DELETE },
        { L"pageup",    VK_PRIOR },
        { L"pgup",      VK_PRIOR },
        { L"pagedown",  VK_NEXT },
        { L"pgdn",      VK_NEXT },

        // 方向鍵
        { L"up",        VK_UP },
        { L"down",      VK_DOWN },
        { L"left",      VK_LEFT },
        { L"right",     VK_RIGHT },

        // 功能鍵
        { L"f1",  VK_F1 },  { L"f2",  VK_F2 },  { L"f3",  VK_F3 },  { L"f4",  VK_F4 },
        { L"f5",  VK_F5 },  { L"f6",  VK_F6 },  { L"f7",  VK_F7 },  { L"f8",  VK_F8 },
        { L"f9",  VK_F9 },  { L"f10", VK_F10 }, { L"f11", VK_F11 }, { L"f12", VK_F12 },

        // 符號鍵
        { L"`",   VK_OEM_3 },      // `~
        { L"-",   VK_OEM_MINUS },  // -_
        { L"=",   VK_OEM_PLUS },   // =+
        { L"[",   VK_OEM_4 },      // [{
        { L"]",   VK_OEM_6 },      // ]}
        { L"\\",  VK_OEM_5 },      // \|
        { L";",   VK_OEM_1 },      // ;:
        { L"'",   VK_OEM_7 },      // '"
        { L",",   VK_OEM_COMMA },  // ,<
        { L".",   VK_OEM_PERIOD }, // .>
        { L"/",   VK_OEM_2 },      // /?
    };
    return keyMap;
}

std::vector<WORD> ParseCombo(const std::wstring& combo) {
    std::vector<WORD> keys;
    std::wstring token;
    const auto& keyMap = GetKeyMap();

    for (size_t i = 0; i <= combo.size(); i++) {
        wchar_t c = (i < combo.size()) ? combo[i] : L'+';
        if (c == L'+') {
            // 去除前後空白
            while (!token.empty() && token.front() == L' ') token.erase(token.begin());
            while (!token.empty() && token.back() == L' ') token.pop_back();
            if (token.empty()) continue;

            // 轉小寫
            for (auto& ch : token) ch = towlower(ch);

            // 查表
            auto it = keyMap.find(token);
            if (it != keyMap.end()) {
                keys.push_back(it->second);
            }
            // 單字元 A~Z
            else if (token.size() == 1 && token[0] >= L'a' && token[0] <= L'z') {
                keys.push_back((WORD)(0x41 + (token[0] - L'a')));
            }
            // 單字元 0~9
            else if (token.size() == 1 && token[0] >= L'0' && token[0] <= L'9') {
                keys.push_back((WORD)(0x30 + (token[0] - L'0')));
            }
            token.clear();
        } else {
            token += c;
        }
    }
    return keys;
}

// ============================================================================
// 公開介面
// ============================================================================

void SendKeyCombo(const std::wstring& combo) {
    auto keys = ParseCombo(combo);
    if (keys.empty()) return;

    std::vector<INPUT> inputs;

    // 依序按下
    for (auto vk : keys) {
        INPUT inp = {};
        inp.type = INPUT_KEYBOARD;
        inp.ki.wVk = vk;
        inp.ki.wScan = (WORD)MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        inp.ki.dwFlags = 0;
        inputs.push_back(inp);
    }
    SendInput((UINT)inputs.size(), inputs.data(), sizeof(INPUT));

    // 短暫按住，確保目標應用程式接收到組合鍵
    Sleep(50);

    // 反序放開
    inputs.clear();
    for (int i = (int)keys.size() - 1; i >= 0; i--) {
        INPUT inp = {};
        inp.type = INPUT_KEYBOARD;
        inp.ki.wVk = keys[i];
        inp.ki.wScan = (WORD)MapVirtualKeyW(keys[i], MAPVK_VK_TO_VSC);
        inp.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs.push_back(inp);
    }
    SendInput((UINT)inputs.size(), inputs.data(), sizeof(INPUT));
}
