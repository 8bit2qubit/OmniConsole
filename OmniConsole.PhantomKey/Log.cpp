#include "Log.h"

#ifdef _DEBUG
#include <shlobj.h>
#include <string>

static std::wstring g_logPath;

void InitLog() {
    WCHAR localAppData[MAX_PATH];
    if (SUCCEEDED(SHGetFolderPathW(NULL, CSIDL_LOCAL_APPDATA, NULL, 0, localAppData))) {
        std::wstring dir = std::wstring(localAppData) + L"\\OmniConsole";
        CreateDirectoryW(dir.c_str(), NULL);
        g_logPath = dir + L"\\PhantomKeyTrace.log";
    }
}

void Log(const wchar_t* fmt, ...) {
    if (g_logPath.empty()) return;
    HANDLE hFile = CreateFileW(g_logPath.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (hFile == INVALID_HANDLE_VALUE) return;

    SYSTEMTIME st;
    GetLocalTime(&st);
    WCHAR buf[1024];
    int prefix = wsprintfW(buf, L"[%02d:%02d:%02d.%03d] ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);

    va_list args;
    va_start(args, fmt);
    wvsprintfW(buf + prefix, fmt, args);
    va_end(args);

    lstrcatW(buf, L"\n");
    DWORD written;
    WriteFile(hFile, buf, (DWORD)(lstrlenW(buf) * sizeof(WCHAR)), &written, NULL);
    CloseHandle(hFile);
}
#endif
