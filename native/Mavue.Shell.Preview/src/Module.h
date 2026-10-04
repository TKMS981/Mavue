#pragma once

#include <windows.h>
#include <objidl.h>
#include <wrl/client.h>

#include <string>
#include <string_view>

namespace mavue
{
    using Microsoft::WRL::ComPtr;

    // CLSIDs. Must match Mavue.Shell.ShellHandlerRegistration (PreviewHandlerClsid / ThumbnailProviderClsid).
    // {AB883DEA-90EE-4AF4-944A-45CEDD231E53}
    inline constexpr CLSID PreviewHandlerClsid = { 0xab883dea, 0x90ee, 0x4af4, { 0x94, 0x4a, 0x45, 0xce, 0xdd, 0x23, 0x1e, 0x53 } };
    // {B4E9FA4B-4DA4-4A1A-9DC7-DE422F056135}
    inline constexpr CLSID ThumbnailProviderClsid = { 0xb4e9fa4b, 0x4da4, 0x4a1a, { 0x9d, 0xc7, 0xde, 0x42, 0x2f, 0x05, 0x61, 0x35 } };

    void SetModule(HMODULE module) noexcept;
    HMODULE Module() noexcept;
    void ModuleAddRef() noexcept;
    void ModuleRelease() noexcept;
    LONG ModuleRefs() noexcept;

    // Folder of this DLL (pdfium.dll is loaded from here).
    std::wstring ModuleDirectory();

    // Localized string from the string table (the thread's UI language, English otherwise).
    std::wstring Text(UINT id);

    // Lower-case extension (".pdf") of the stream's name, or empty. Only used as a hint; content decides.
    std::wstring StreamExtension(IStream* stream);

    // Size of the stream in bytes (0 when unknown).
    ULONGLONG StreamSize(IStream* stream);

    // Diagnostics for tests: one JSON line per event in the file named by HKCU\Software\Mavue\Shell "TraceFile"
    // (absent by default: nothing is written). Never contains file names or document content. `fields` is the
    // rest of a JSON object without braces (may be empty), e.g. "\"kind\":\"pdf\",\"ms\":12".
    void Trace(std::string_view event, std::string_view fields = {});
    bool TraceEnabled();

    // Milliseconds between two QueryPerformanceCounter values.
    double Milliseconds(LONGLONG from, LONGLONG to) noexcept;
    LONGLONG Now() noexcept;

    // Keeps the DLL loaded while a background thread runs.
    class ModuleLock final
    {
    public:
        ModuleLock() noexcept { ModuleAddRef(); }
        ~ModuleLock() { ModuleRelease(); }
        ModuleLock(const ModuleLock&) = delete;
        ModuleLock& operator=(const ModuleLock&) = delete;
    };

    // CoInitializeEx for the lifetime of the object (worker threads).
    class ComApartment final
    {
    public:
        explicit ComApartment(DWORD model) noexcept : m_hr(CoInitializeEx(nullptr, model)) {}
        ~ComApartment()
        {
            if (SUCCEEDED(m_hr))
            {
                CoUninitialize();
            }
        }

        ComApartment(const ComApartment&) = delete;
        ComApartment& operator=(const ComApartment&) = delete;
        bool Ok() const noexcept { return SUCCEEDED(m_hr); }

    private:
        HRESULT m_hr;
    };

    std::string Utf8(std::wstring_view text);
    std::string HResultText(HRESULT hr);
}
