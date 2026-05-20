#include "VdfParser.h"
#include "Log.h"

// ============================================================================
// 自製 VDF 遞迴下降解析器
// ============================================================================

// 讀取 UTF-8 檔案並轉換為 wstring
static std::wstring ReadFileAsWstring(const std::wstring& filePath) {
    // 以二進位讀取 UTF-8 內容
    HANDLE hFile = CreateFileW(filePath.c_str(), GENERIC_READ, FILE_SHARE_READ, NULL,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return L"";

    DWORD fileSize = GetFileSize(hFile, NULL);
    if (fileSize == 0 || fileSize == INVALID_FILE_SIZE) {
        CloseHandle(hFile);
        return L"";
    }

    std::string utf8(fileSize, '\0');
    DWORD bytesRead = 0;
    if (!ReadFile(hFile, utf8.data(), fileSize, &bytesRead, NULL) || bytesRead == 0) {
        CloseHandle(hFile);
        return L"";
    }
    CloseHandle(hFile);

    // 跳過 UTF-8 BOM
    const char* start = utf8.c_str();
    size_t len = bytesRead;
    if (len >= 3 && (unsigned char)start[0] == 0xEF &&
        (unsigned char)start[1] == 0xBB && (unsigned char)start[2] == 0xBF) {
        start += 3;
        len -= 3;
    }

    // UTF-8 → wstring
    int wlen = MultiByteToWideChar(CP_UTF8, 0, start, (int)len, NULL, 0);
    if (wlen == 0) return L"";
    std::wstring result(wlen, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, start, (int)len, result.data(), wlen);
    return result;
}

// Tokenizer 狀態
struct VdfTokenizer {
    const wchar_t* pos;
    const wchar_t* end;

    VdfTokenizer(const std::wstring& text)
        : pos(text.c_str()), end(text.c_str() + text.size()) {}

    void SkipWhitespaceAndComments() {
        while (pos < end) {
            // 空白
            if (*pos == L' ' || *pos == L'\t' || *pos == L'\r' || *pos == L'\n') {
                pos++;
                continue;
            }
            // 行註解 "//"
            if (pos + 1 < end && pos[0] == L'/' && pos[1] == L'/') {
                pos += 2;
                while (pos < end && *pos != L'\n') pos++;
                continue;
            }
            break;
        }
    }

    // 讀取引號字串（處理 \" 轉義），回傳引號內的內容
    bool ReadQuotedString(std::wstring& out) {
        SkipWhitespaceAndComments();
        if (pos >= end || *pos != L'"') return false;
        pos++; // 跳過開頭 "

        out.clear();
        while (pos < end && *pos != L'"') {
            if (*pos == L'\\' && pos + 1 < end) {
                pos++;
                switch (*pos) {
                    case L'"':  out += L'"'; break;
                    case L'\\': out += L'\\'; break;
                    case L'n':  out += L'\n'; break;
                    case L't':  out += L'\t'; break;
                    default:    out += *pos; break;
                }
            } else {
                out += *pos;
            }
            pos++;
        }

        if (pos < end && *pos == L'"') pos++; // 跳過結尾 "
        return true;
    }

    // 窺視下一個非空白字元
    wchar_t Peek() {
        SkipWhitespaceAndComments();
        return (pos < end) ? *pos : L'\0';
    }

    // 消耗指定字元
    bool Consume(wchar_t c) {
        SkipWhitespaceAndComments();
        if (pos < end && *pos == c) {
            pos++;
            return true;
        }
        return false;
    }
};

// 遞迴解析 VDF 節點
static bool ParseNodes(VdfTokenizer& tok, std::map<std::wstring, VdfNode>& nodes) {
    while (true) {
        wchar_t next = tok.Peek();
        if (next == L'\0' || next == L'}') return true;

        std::wstring key;
        if (!tok.ReadQuotedString(key)) return false;

        next = tok.Peek();
        if (next == L'{') {
            // Section：遞迴解析子節點
            tok.Consume(L'{');
            VdfNode child;
            if (!ParseNodes(tok, child.children)) return false;
            if (!tok.Consume(L'}')) return false;
            nodes[key] = std::move(child);
        } else if (next == L'"') {
            // 葉值
            VdfNode leaf;
            if (!tok.ReadQuotedString(leaf.value)) return false;
            nodes[key] = std::move(leaf);
        } else {
            // 非預期的 token
            Log(L"[VdfParser] Unexpected token at position.");
            return false;
        }
    }
}

// ============================================================================
// 公開介面
// ============================================================================

VdfNode VdfParse(const std::wstring& filePath) {
    VdfNode root;
    std::wstring content = ReadFileAsWstring(filePath);
    if (content.empty()) {
        Log(L"[VdfParser] Failed to read: %s", filePath.c_str());
        return root;
    }

    VdfTokenizer tok(content);
    if (!ParseNodes(tok, root.children)) {
        Log(L"[VdfParser] Parse error in: %s", filePath.c_str());
        root.children.clear();
    }
    return root;
}

const VdfNode* VdfNode::Navigate(const std::wstring& dottedPath) const {
    const VdfNode* current = this;
    std::wstring segment;

    for (size_t i = 0; i <= dottedPath.size(); i++) {
        wchar_t c = (i < dottedPath.size()) ? dottedPath[i] : L'.';
        if (c == L'.') {
            if (!segment.empty()) {
                // Case-insensitive 查找
                bool found = false;
                for (const auto& pair : current->children) {
                    if (_wcsicmp(pair.first.c_str(), segment.c_str()) == 0) {
                        current = &pair.second;
                        found = true;
                        break;
                    }
                }
                if (!found) return nullptr;
                segment.clear();
            }
        } else {
            segment += c;
        }
    }
    return current;
}
