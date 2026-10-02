// "Mavue Quick View" for the Windows 11 File Explorer context menu (IExplorerCommand).
//
// Deliberately minimal (docs/WINDOWS-INTEGRATION.md §15): the command does not show anything itself and never
// starts the Quick View host directly. Invoke asks File Explorer to run
//     Mavue.QuickView.Host.exe --quickview "<path>" ...
// through the desktop's Shell automation object (IShellDispatch2::ShellExecute). The process is then created
// by explorer.exe, which (measured) gives it the right to take the foreground and keeps it out of this package's
// desktop app container; it forwards the request to the resident host over the existing named pipe, or becomes
// the resident host itself. Quick View reads the Explorer selection itself, so the first path is enough there.
//
// The class runs in a packaged COM surrogate (dllhost.exe), registered by the identity package's manifest.

#include "ExplorerCommand.h"

#include <exdisp.h>
#include <shldisp.h>
#include <shlguid.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <strsafe.h>

#include <string>

namespace mavue
{
    namespace
    {
        constexpr wchar_t Title[] = L"Mavue Quick View"; // product name, the same in every language
        constexpr wchar_t HostExecutable[] = L"Mavue.QuickView.Host.exe";

        // Longest argument list passed to the host. Explorer passes the whole selection; Quick View reads the
        // Explorer selection itself, so later paths only matter when no Explorer view has them selected.
        constexpr size_t MaxArgumentChars = 8000;

        HMODULE g_module = nullptr;

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

        void AppendQuoted(std::wstring& arguments, const wchar_t* path)
        {
            // File system paths cannot contain '"'; a trailing backslash would escape the closing quote.
            arguments += L" \"";
            arguments += path;
            if (!arguments.empty() && arguments.back() == L'\\')
            {
                arguments += L'\\';
            }

            arguments += L'"';
        }

        /// <summary>Asks the running File Explorer (not this surrogate) to start a program.</summary>
        HRESULT ShellExecuteThroughExplorer(const std::wstring& file, const std::wstring& arguments, const std::wstring& directory)
        {
            IShellWindows* windows = nullptr;
            HRESULT hr = CoCreateInstance(CLSID_ShellWindows, nullptr, CLSCTX_LOCAL_SERVER, IID_PPV_ARGS(&windows));
            if (FAILED(hr))
            {
                return hr;
            }

            VARIANT location{};
            location.vt = VT_I4;
            location.lVal = CSIDL_DESKTOP;
            VARIANT empty{};
            long hwnd = 0;
            IDispatch* desktopDispatch = nullptr;
            hr = windows->FindWindowSW(&location, &empty, SWC_DESKTOP, &hwnd, SWFO_NEEDDISPATCH, &desktopDispatch);
            windows->Release();
            if (hr != S_OK || desktopDispatch == nullptr)
            {
                return FAILED(hr) ? hr : E_FAIL;
            }

            IServiceProvider* services = nullptr;
            hr = desktopDispatch->QueryInterface(IID_PPV_ARGS(&services));
            desktopDispatch->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IShellBrowser* browser = nullptr;
            hr = services->QueryService(SID_STopLevelBrowser, IID_PPV_ARGS(&browser));
            services->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IShellView* view = nullptr;
            hr = browser->QueryActiveShellView(&view);
            browser->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IDispatch* backgroundDispatch = nullptr;
            hr = view->GetItemObject(SVGIO_BACKGROUND, IID_PPV_ARGS(&backgroundDispatch));
            view->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IShellFolderViewDual* folderView = nullptr;
            hr = backgroundDispatch->QueryInterface(IID_PPV_ARGS(&folderView));
            backgroundDispatch->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IDispatch* applicationDispatch = nullptr;
            hr = folderView->get_Application(&applicationDispatch);
            folderView->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            IShellDispatch2* shell = nullptr;
            hr = applicationDispatch->QueryInterface(IID_PPV_ARGS(&shell));
            applicationDispatch->Release();
            if (FAILED(hr))
            {
                return hr;
            }

            BSTR fileBstr = SysAllocString(file.c_str());
            VARIANT args{};
            args.vt = VT_BSTR;
            args.bstrVal = SysAllocString(arguments.c_str());
            VARIANT dir{};
            dir.vt = VT_BSTR;
            dir.bstrVal = SysAllocString(directory.c_str());
            VARIANT operation{};
            operation.vt = VT_BSTR;
            operation.bstrVal = SysAllocString(L"open");
            VARIANT show{};
            show.vt = VT_I4;
            show.lVal = SW_SHOWNORMAL;

            hr = (fileBstr && args.bstrVal && dir.bstrVal && operation.bstrVal)
                ? shell->ShellExecute(fileBstr, args, dir, operation, show)
                : E_OUTOFMEMORY;

            SysFreeString(fileBstr);
            VariantClear(&args);
            VariantClear(&dir);
            VariantClear(&operation);
            shell->Release();
            return hr;
        }
    }

    void SetModule(HMODULE module) noexcept
    {
        g_module = module;
    }

    std::wstring BuildArguments(IShellItemArray* items)
    {
        std::wstring arguments = L"--quickview";
        DWORD count = 0;
        if (items == nullptr || FAILED(items->GetCount(&count)))
        {
            return {};
        }

        size_t added = 0;
        for (DWORD i = 0; i < count; i++)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(i, &item)))
            {
                continue;
            }

            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path != nullptr)
            {
                size_t length = wcslen(path);
                if (added == 0 || arguments.size() + length + 4 <= MaxArgumentChars)
                {
                    AppendQuoted(arguments, path);
                    added++;
                }

                CoTaskMemFree(path);
            }

            item->Release();
        }

        return added > 0 ? arguments : std::wstring{};
    }

    ExplorerCommand::ExplorerCommand() noexcept
    {
        ModuleAddRef();
    }

    ExplorerCommand::~ExplorerCommand()
    {
        ModuleRelease();
    }

    IFACEMETHODIMP ExplorerCommand::QueryInterface(REFIID riid, void** object)
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == IID_IExplorerCommand)
        {
            *object = static_cast<IExplorerCommand*>(this);
            AddRef();
            return S_OK;
        }

        *object = nullptr;
        return E_NOINTERFACE;
    }

    IFACEMETHODIMP_(ULONG) ExplorerCommand::AddRef()
    {
        return InterlockedIncrement(&m_refs);
    }

    IFACEMETHODIMP_(ULONG) ExplorerCommand::Release()
    {
        ULONG refs = InterlockedDecrement(&m_refs);
        if (refs == 0)
        {
            delete this;
        }

        return refs;
    }

    IFACEMETHODIMP ExplorerCommand::GetTitle(IShellItemArray*, LPWSTR* name)
    {
        return SHStrDupW(Title, name);
    }

    IFACEMETHODIMP ExplorerCommand::GetIcon(IShellItemArray*, LPWSTR* icon)
    {
        *icon = nullptr;
        std::wstring directory = ModuleDirectory();
        if (directory.empty())
        {
            return E_FAIL;
        }

        std::wstring resource = directory + L"\\" + HostExecutable + L",0";
        return SHStrDupW(resource.c_str(), icon);
    }

    IFACEMETHODIMP ExplorerCommand::GetToolTip(IShellItemArray*, LPWSTR* tooltip)
    {
        *tooltip = nullptr;
        return E_NOTIMPL;
    }

    IFACEMETHODIMP ExplorerCommand::GetCanonicalName(GUID* name)
    {
        *name = __uuidof(ExplorerCommand);
        return S_OK;
    }

    IFACEMETHODIMP ExplorerCommand::GetState(IShellItemArray*, BOOL, EXPCMDSTATE* state)
    {
        // The manifest limits the command to the file types Quick View shows; nothing to read here
        // (this runs while Explorer builds the menu and must stay fast).
        *state = ECS_ENABLED;
        return S_OK;
    }

    IFACEMETHODIMP ExplorerCommand::Invoke(IShellItemArray* items, IBindCtx*)
    {
        std::wstring arguments = BuildArguments(items);
        std::wstring directory = ModuleDirectory();
        if (arguments.empty() || directory.empty())
        {
            return E_INVALIDARG;
        }

        std::wstring host = directory + L"\\" + HostExecutable;
        HRESULT hr = ShellExecuteThroughExplorer(host, arguments, directory);
        if (FAILED(hr))
        {
            wchar_t message[128];
            StringCchPrintfW(message, ARRAYSIZE(message), L"Mavue Quick View: could not ask Explorer to start the host (0x%08X)\n", static_cast<unsigned>(hr));
            OutputDebugStringW(message);
        }

        return hr;
    }

    IFACEMETHODIMP ExplorerCommand::GetFlags(EXPCMDFLAGS* flags)
    {
        *flags = ECF_DEFAULT;
        return S_OK;
    }

    IFACEMETHODIMP ExplorerCommand::EnumSubCommands(IEnumExplorerCommand** commands)
    {
        *commands = nullptr;
        return E_NOTIMPL;
    }
}
