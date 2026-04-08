#pragma once
#include <string>

// ============================================================================
// 規則表
// ============================================================================

struct InputRule {
    const wchar_t* processName;   // 比對前景程式名（不含 .exe，大小寫不敏感）
    const wchar_t* shortCombo;    // View 短按送出的快速鍵
    const wchar_t* longCombo;     // View 長按送出的快速鍵（空字串=不觸發）
};

// 取得前景視窗的行程名（不含 .exe）
std::wstring GetForegroundProcessName();

// 從規則表中查詢與前景程式匹配的規則
const InputRule* FindRuleForForeground();
