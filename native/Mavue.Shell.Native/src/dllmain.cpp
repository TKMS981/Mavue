// COM entry points. The class is registered by the identity package manifest (com:SurrogateServer), so there is
// no DllRegisterServer: nothing is written to the registry by this DLL.

#include "ExplorerCommand.h"

#include <new>

namespace
{
    LONG g_moduleRefs = 0;

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

            auto* command = new (std::nothrow) mavue::ExplorerCommand();
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            HRESULT hr = command->QueryInterface(riid, object);
            command->Release();
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
}

namespace mavue
{
    void ModuleAddRef() noexcept { InterlockedIncrement(&g_moduleRefs); }
    void ModuleRelease() noexcept { InterlockedDecrement(&g_moduleRefs); }
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
    if (clsid != __uuidof(mavue::ExplorerCommand))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    HRESULT hr = factory->QueryInterface(riid, object);
    factory->Release();
    return hr;
}

__control_entrypoint(DllExport) STDAPI DllCanUnloadNow()
{
    return g_moduleRefs == 0 ? S_OK : S_FALSE;
}
