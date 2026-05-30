#include <Windows.h>

int APIENTRY wWinMain(_In_ HINSTANCE, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int)
{
    wchar_t dllPath[MAX_PATH];
    if (::GetModuleFileNameW(nullptr, dllPath, MAX_PATH) == 0) return 1;

    wchar_t* lastSlash = wcsrchr(dllPath, L'\\');
    if (lastSlash == nullptr) return 1;
    *(lastSlash + 1) = L'\0';
    if (wcscat_s(dllPath, MAX_PATH, L"OmniConsole.PhantomBridge.dll") != 0) return 1;

    HMODULE dll = ::LoadLibraryW(dllPath);
    if (dll == nullptr) return 2;

    using RunFn = int(WINAPI*)();
    auto run = reinterpret_cast<RunFn>(::GetProcAddress(dll, "RunPhantomBridgeServer"));
    if (run == nullptr) { ::FreeLibrary(dll); return 3; }

    int result = run();
    ::FreeLibrary(dll);
    return result;
}
