#pragma once
#include <windows.h>

// ============================================================================
// 除錯日誌（Release 建置時完全移除）
// ============================================================================

#ifdef _DEBUG
void InitLog();
void Log(const wchar_t* fmt, ...);
#else
inline void InitLog() {}
inline void Log(const wchar_t*, ...) {}
#endif
