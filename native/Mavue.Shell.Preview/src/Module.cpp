#include "Module.h"

#include <shlwapi.h>
#include <strsafe.h>

#include <cwctype>

namespace mavue
{
    namespace
    {
        HMODULE g_module = nullptr;
        LONG g_refs = 0;
        SRWLOCK g_traceLock = SRWLOCK_INIT;

        LONGLONG Frequency() noexcept
        {
            static const LONGLONG frequency = []
            {
                LARGE_INTEGER value{};
                QueryPerformanceFrequency(&value);
                return value.QuadPart;
            }();
            return frequency;
        }

        std::wstring TraceFile()
        {
            wchar_t path[MAX_PATH * 2]{};
            DWORD size = sizeof(path);
            if (RegGetValueW(HKEY_CURRENT_USER, L"Software\\Mavue\\Shell", L"TraceFile", RRF_RT_REG_SZ, nullptr, path, &size) != ERROR_SUCCESS)
            {
                return {};
            }

            return path;
        }
    }

    void SetModule(HMODULE module) noexcept { g_module = module; }
    HMODULE Module() noexcept { return g_module; }
    void ModuleAddRef() noexcept { InterlockedIncrement(&g_refs); }
    void ModuleRelease() noexcept { InterlockedDecrement(&g_refs); }
    LONG ModuleRefs() noexcept { return InterlockedCompareExchange(&g_refs, 0, 0); }

    std::wstring ModuleDirectory()
    {
        wchar_t path[MAX_PATH * 4]{};
        DWORD length = GetModuleFileNameW(g_module, path, ARRAYSIZE(path));
        if (length == 0 || length >= ARRAYSIZE(path))
        {
            return {};
        }

        PathRemoveFileSpecW(path);
        return path;
    }

    std::wstring Text(UINT id)
    {
        const wchar_t* resource = nullptr;
        int length = LoadStringW(g_module, id, reinterpret_cast<LPWSTR>(&resource), 0); // read-only pointer into the resource
        return length > 0 && resource != nullptr ? std::wstring(resource, static_cast<size_t>(length)) : std::wstring();
    }

    std::wstring StreamExtension(IStream* stream)
    {
        if (stream == nullptr)
        {
            return {};
        }

        STATSTG stat{};
        if (FAILED(stream->Stat(&stat, STATFLAG_DEFAULT)) || stat.pwcsName == nullptr)
        {
            return {};
        }

        std::wstring extension = PathFindExtensionW(stat.pwcsName);
        CoTaskMemFree(stat.pwcsName);
        for (wchar_t& c : extension)
        {
            c = static_cast<wchar_t>(std::towlower(c));
        }

        return extension.size() <= 16 ? extension : std::wstring();
    }

    ULONGLONG StreamSize(IStream* stream)
    {
        STATSTG stat{};
        if (stream == nullptr || FAILED(stream->Stat(&stat, STATFLAG_NONAME)))
        {
            return 0;
        }

        return stat.cbSize.QuadPart;
    }

    bool TraceEnabled() { return !TraceFile().empty(); }

    void Trace(std::string_view event, std::string_view fields)
    {
        std::wstring file = TraceFile();
        if (file.empty())
        {
            return;
        }

        char head[160]{};
        StringCchPrintfA(head, ARRAYSIZE(head), "{\"event\":\"%.*s\",\"qpc\":%lld,\"freq\":%lld,\"pid\":%lu,\"tid\":%lu",
            static_cast<int>(event.size()), event.data(), Now(), Frequency(), GetCurrentProcessId(), GetCurrentThreadId());
        std::string line = head;
        if (!fields.empty())
        {
            line += ',';
            line += fields;
        }

        line += "}\n";
        AcquireSRWLockExclusive(&g_traceLock);
        HANDLE handle = CreateFileW(file.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (handle != INVALID_HANDLE_VALUE)
        {
            DWORD written = 0;
            WriteFile(handle, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
            CloseHandle(handle);
        }

        ReleaseSRWLockExclusive(&g_traceLock);
    }

    LONGLONG Now() noexcept
    {
        LARGE_INTEGER value{};
        QueryPerformanceCounter(&value);
        return value.QuadPart;
    }

    double Milliseconds(LONGLONG from, LONGLONG to) noexcept
    {
        return static_cast<double>(to - from) * 1000.0 / static_cast<double>(Frequency());
    }

    std::string Utf8(std::wstring_view text)
    {
        if (text.empty())
        {
            return {};
        }

        int size = WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        std::string result(static_cast<size_t>(size), '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), result.data(), size, nullptr, nullptr);
        return result;
    }

    std::string HResultText(HRESULT hr)
    {
        char text[16]{};
        StringCchPrintfA(text, ARRAYSIZE(text), "0x%08X", static_cast<unsigned>(hr));
        return text;
    }
}
