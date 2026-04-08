#pragma once
#include <string>
#include <map>

// ============================================================================
// Valve Data Format (VDF) 遞迴下降解析器
// ============================================================================

struct VdfNode {
    std::wstring value;                                 // 葉值（section 時為空）
    std::map<std::wstring, VdfNode> children;           // 子節點（葉值時為空）

    // 依路徑查詢子節點（以 '.' 分隔，如 "users.12345.MostRecent"）
    const VdfNode* Navigate(const std::wstring& dottedPath) const;
};

// 解析 VDF 檔案，回傳根節點（失敗時回傳空 VdfNode）
VdfNode VdfParse(const std::wstring& filePath);
