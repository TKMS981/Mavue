#include "MediaPlayer.h"

#include <mfapi.h>
#include <mfidl.h>

#include <atomic>
#include <new>

namespace mavue
{
    // IMFMediaEngineNotify: forwards events to the window (called on Media Foundation threads).
    class MediaPlayer::Notify final : public IMFMediaEngineNotify
    {
    public:
        Notify(HWND window, UINT message) noexcept : m_window(window), m_message(message) {}

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            if (riid == IID_IUnknown || riid == __uuidof(IMFMediaEngineNotify))
            {
                *object = static_cast<IMFMediaEngineNotify*>(this);
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

        IFACEMETHODIMP EventNotify(DWORD event, DWORD_PTR, DWORD param2) override
        {
            HWND window = m_window.load();
            if (window != nullptr)
            {
                PostMessageW(window, m_message, event, event == MF_MEDIA_ENGINE_EVENT_ERROR ? static_cast<LPARAM>(param2) : 0);
            }

            return S_OK;
        }

        void Detach() noexcept { m_window.store(nullptr); }

    private:
        std::atomic<HWND> m_window;
        UINT m_message;
        LONG m_refs = 1;
    };

    HRESULT MediaPlayer::Create(HWND videoWindow, HWND notifyWindow, UINT notifyMessage, const std::wstring& path,
        IStream* stream, const std::wstring& nameHint, std::unique_ptr<MediaPlayer>& player)
    {
        player.reset();
        HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(hr))
        {
            return hr;
        }

        std::unique_ptr<MediaPlayer> created(new (std::nothrow) MediaPlayer());
        if (!created)
        {
            MFShutdown();
            return E_OUTOFMEMORY;
        }

        created->m_started = true; // from here the destructor balances MFStartup
        CoIncrementMTAUsage(&created->m_mta);
        created->m_notify.Attach(new (std::nothrow) Notify(notifyWindow, notifyMessage));
        if (!created->m_notify)
        {
            return E_OUTOFMEMORY;
        }

        ComPtr<IMFMediaEngineClassFactory> factory;
        hr = CoCreateInstance(CLSID_MFMediaEngineClassFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory));
        ComPtr<IMFAttributes> attributes;
        if (SUCCEEDED(hr))
        {
            hr = MFCreateAttributes(&attributes, 2);
        }

        if (SUCCEEDED(hr))
        {
            hr = attributes->SetUnknown(MF_MEDIA_ENGINE_CALLBACK, created->m_notify.Get());
        }

        if (SUCCEEDED(hr))
        {
            hr = attributes->SetUINT64(MF_MEDIA_ENGINE_PLAYBACK_HWND, reinterpret_cast<UINT64>(videoWindow));
        }

        if (SUCCEEDED(hr))
        {
            hr = factory->CreateInstance(0, attributes.Get(), &created->m_engine);
        }

        if (SUCCEEDED(hr))
        {
            hr = created->m_engine.As(&created->m_engineEx);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        created->m_engine->SetAutoPlay(FALSE);
        created->m_engine->SetPreload(MF_MEDIA_ENGINE_PRELOAD_AUTOMATIC);
        if (!path.empty())
        {
            BSTR url = SysAllocString(path.c_str());
            hr = url != nullptr ? created->m_engine->SetSource(url) : E_OUTOFMEMORY;
            SysFreeString(url);
        }
        else
        {
            ComPtr<IMFByteStream> byteStream;
            hr = MFCreateMFByteStreamOnStream(stream, &byteStream);
            if (SUCCEEDED(hr))
            {
                BSTR name = SysAllocString(nameHint.empty() ? L"media" : nameHint.c_str());
                hr = name != nullptr ? created->m_engineEx->SetSourceFromByteStream(byteStream.Get(), name) : E_OUTOFMEMORY;
                SysFreeString(name);
            }
        }

        if (SUCCEEDED(hr))
        {
            player = std::move(created);
        }

        return hr;
    }

    MediaPlayer::~MediaPlayer()
    {
        Shutdown();
    }

    void MediaPlayer::Shutdown()
    {
        if (m_notify)
        {
            m_notify->Detach();
        }

        if (m_engine)
        {
            m_engine->Shutdown();
            m_engine.Reset();
            m_engineEx.Reset();
        }

        m_notify.Reset();
        if (m_started)
        {
            m_started = false;
            MFShutdown();
        }

        if (m_mta != nullptr)
        {
            CoDecrementMTAUsage(m_mta);
            m_mta = nullptr;
        }
    }

    bool MediaPlayer::HasMetadata() const
    {
        return m_engine && m_engine->GetReadyState() >= MF_MEDIA_ENGINE_READY_HAVE_METADATA;
    }

    void MediaPlayer::DetachNotify()
    {
        if (m_notify)
        {
            m_notify->Detach();
        }
    }

    void MediaPlayer::Play()
    {
        if (m_engine)
        {
            if (m_engine->IsEnded())
            {
                m_engine->SetCurrentTime(0);
            }

            m_engine->Play();
        }
    }

    void MediaPlayer::Pause()
    {
        if (m_engine)
        {
            m_engine->Pause();
        }
    }

    void MediaPlayer::TogglePlay()
    {
        IsPaused() ? Play() : Pause();
    }

    bool MediaPlayer::IsPaused() const { return !m_engine || m_engine->IsPaused() || m_engine->IsEnded(); }
    bool MediaPlayer::IsMuted() const { return m_engine && m_engine->GetMuted(); }

    void MediaPlayer::SetMuted(bool muted)
    {
        if (m_engine)
        {
            m_engine->SetMuted(muted ? TRUE : FALSE);
        }
    }

    bool MediaPlayer::HasVideo() const { return m_engine && m_engine->HasVideo(); }
    bool MediaPlayer::HasAudio() const { return m_engine && m_engine->HasAudio(); }

    double MediaPlayer::Duration() const
    {
        if (!m_engine)
        {
            return 0;
        }

        double duration = m_engine->GetDuration();
        return duration > 0 && duration < 1e9 ? duration : 0; // NaN / infinity for unknown or live
    }

    double MediaPlayer::Position() const { return m_engine ? m_engine->GetCurrentTime() : 0; }

    void MediaPlayer::Seek(double seconds)
    {
        if (m_engine)
        {
            m_engine->SetCurrentTime(seconds);
        }
    }

    bool MediaPlayer::NativeSize(DWORD& width, DWORD& height) const
    {
        width = height = 0;
        return m_engine && SUCCEEDED(m_engine->GetNativeVideoSize(&width, &height)) && width > 0 && height > 0;
    }

    void MediaPlayer::UpdateVideoRect(const RECT& rect)
    {
        if (m_engineEx)
        {
            MFARGB border{ 0, 0, 0, 255 };
            m_engineEx->UpdateVideoStream(nullptr, &rect, &border);
        }
    }

    HRESULT MediaPlayer::Error() const
    {
        if (!m_engine)
        {
            return E_FAIL;
        }

        ComPtr<IMFMediaError> error;
        if (FAILED(m_engine->GetError(&error)) || !error)
        {
            return S_OK;
        }

        return error->GetExtendedErrorCode();
    }
}
