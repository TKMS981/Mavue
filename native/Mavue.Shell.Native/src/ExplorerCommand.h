#pragma once

#include <windows.h>
#include <shobjidl_core.h>

#include <string>

namespace mavue
{
    // CLSID of the "Mavue Quick View" command. Must match com:Class/@Id and desktop5:Verb/@Clsid in the identity
    // package manifest (Mavue.Shell.IdentityPackageManifest.CommandClsid).
    class __declspec(uuid("3C34DBCC-2B28-45D3-A949-A83B0EC298EB")) ExplorerCommand final : public IExplorerCommand
    {
    public:
        ExplorerCommand() noexcept;

        // IUnknown
        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override;
        IFACEMETHODIMP_(ULONG) AddRef() override;
        IFACEMETHODIMP_(ULONG) Release() override;

        // IExplorerCommand
        IFACEMETHODIMP GetTitle(IShellItemArray* items, LPWSTR* name) override;
        IFACEMETHODIMP GetIcon(IShellItemArray* items, LPWSTR* icon) override;
        IFACEMETHODIMP GetToolTip(IShellItemArray* items, LPWSTR* tooltip) override;
        IFACEMETHODIMP GetCanonicalName(GUID* name) override;
        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL okToBeSlow, EXPCMDSTATE* state) override;
        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx* bindContext) override;
        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override;
        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override;

    private:
        ~ExplorerCommand();
        LONG m_refs = 1;
    };

    void SetModule(HMODULE module) noexcept;
    void ModuleAddRef() noexcept;
    void ModuleRelease() noexcept;

    // "--quickview" followed by the quoted file system paths of the items (bounded length). Empty if none.
    std::wstring BuildArguments(IShellItemArray* items);
}
