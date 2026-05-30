#include <shlobj.h>
#include <stdio.h>

int APIENTRY wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int)
{
    wchar_t localAppData[MAX_PATH];
    if (FAILED(::SHGetFolderPathW(nullptr, CSIDL_LOCAL_APPDATA, nullptr, 0, localAppData))) {
        return 1;
    }

    wchar_t dllPath[MAX_PATH];
    int written = swprintf_s(dllPath, MAX_PATH,
        L"%s\\Packages\\b5fbce6b-2d7d-4da0-b419-4beb30e2b808_n7gpkx2kypjte"
        L"\\LocalCache\\Local\\OmniConsole\\OmniConsole.PhantomKey.dll",
        localAppData);
    if (written < 0) return 1;

    HMODULE dll = ::LoadLibraryW(dllPath);
    if (dll == nullptr) return 2;

    using RunFn = int(WINAPI*)();
    auto run = reinterpret_cast<RunFn>(::GetProcAddress(dll, "PhantomKeyMain"));
    if (run == nullptr) { ::FreeLibrary(dll); return 3; }

    int result = run();
    ::FreeLibrary(dll);
    return result;
}
