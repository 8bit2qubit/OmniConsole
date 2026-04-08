#pragma once
#include <windows.h>
#include <string>
#include <vector>

// 解析簡易快速鍵字串（如 "Ctrl+1"、"Shift+Tab"）為 VK code 序列
std::vector<WORD> ParseCombo(const std::wstring& combo);

// 送出鍵盤快速鍵組合
void SendKeyCombo(const std::wstring& combo);
