// COM entry points. Registration is written by Mavue (Mavue.exe --register, Mavue.Shell.ShellHandlerRegistration,
// per user), so there is no DllRegisterServer: this DLL never writes to the registry.

#include "PreviewHandler.h"
#include "ThumbnailProvider.h"

#include <new>

namespace
{
    template <typename T>
    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory() noexcept { mavue::ModuleAddRef(); }

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            if (riid == IID_IUnknown || riid == IID_IClassFactory)
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            *object = nullptr;
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }

        IFACEMETHODIMP_(ULONG) Release() override
        {
            ULONG refs = InterlockedDecrement(&m_refs);
            if (refs == 0)
            {
                delete this;
            }

            return refs;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto* instance = new (std::nothrow) T();
            if (instance == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            HRESULT hr = instance->QueryInterface(riid, object);
            instance->Release();
            return hr;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            lock ? mavue::ModuleAddRef() : mavue::ModuleRelease();
            return S_OK;
        }

    private:
        ~ClassFactory() { mavue::ModuleRelease(); }
        LONG m_refs = 1;
    };

    template <typename T>
    HRESULT CreateFactory(REFIID riid, void** object)
    {
        auto* factory = new (std::nothrow) ClassFactory<T>();
        if (factory == nullptr)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = factory->QueryInterface(riid, object);
        factory->Release();
        return hr;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        mavue::SetModule(module);
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

_Check_return_ STDAPI DllGetClassObject(_In_ REFCLSID clsid, _In_ REFIID riid, _Outptr_ LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (clsid == mavue::PreviewHandlerClsid)
    {
        return CreateFactory<mavue::PreviewHandler>(riid, object);
    }

    if (clsid == mavue::ThumbnailProviderClsid)
    {
        return CreateFactory<mavue::ThumbnailProvider>(riid, object);
    }

    return CLASS_E_CLASSNOTAVAILABLE;
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    if (mavue::ModuleRefs() != 0)
    {
        return S_FALSE;
    }

    // No preview window is left: the window classes (whose procedure is in this DLL) go with it.
    mavue::PreviewWindow::UnregisterClasses();
    return S_OK;
}
