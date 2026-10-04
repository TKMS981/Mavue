#pragma once

#include "Module.h"

#include <mfmediaengine.h>

#include <memory>
#include <string>

namespace mavue
{
    // Video and audio in the preview pane: the Media Foundation media engine (the Windows codecs Mavue's own
    // player uses), drawing video into `videoWindow`. Shut down off the preview window's thread: that
    // takes a few hundred ms and waits in COM, which must not happen on a thread whose input Explorer shares (a key
    // pressed in Explorer meanwhile was lost, measured). Engine events are posted to `notifyWindow` as
    // `notifyMessage` (wParam = MF_MEDIA_ENGINE_EVENT, lParam = HRESULT for errors). Nothing plays until Play().
    class MediaPlayer final
    {
    public:
        ~MediaPlayer();
        MediaPlayer(const MediaPlayer&) = delete;
        MediaPlayer& operator=(const MediaPlayer&) = delete;

        // `path` is used when the stream is a file on disk; otherwise the stream is read through a byte stream
        // (`nameHint` gives the container type, e.g. "x.mp4").
        static HRESULT Create(HWND videoWindow, HWND notifyWindow, UINT notifyMessage, const std::wstring& path,
            IStream* stream, const std::wstring& nameHint, std::unique_ptr<MediaPlayer>& player);

        void Play();
        void Pause();
        void TogglePlay();
        bool IsPaused() const;
        bool HasVideo() const;
        bool HasAudio() const;
        bool IsMuted() const;
        void SetMuted(bool muted);
        double Duration() const;  // seconds, 0 when unknown
        double Position() const;  // seconds
        void Seek(double seconds);
        bool NativeSize(DWORD& width, DWORD& height) const;
        void UpdateVideoRect(const RECT& rect);
        HRESULT Error() const;
        bool HasMetadata() const;   // the duration and streams are known
        void DetachNotify();        // no more events to the window (before a shutdown elsewhere)
        void Shutdown();            // may take a few hundred ms: never on the preview window's thread

    private:
        MediaPlayer() = default;
        class Notify;
        ComPtr<IMFMediaEngine> m_engine;
        ComPtr<IMFMediaEngineEx> m_engineEx;
        ComPtr<Notify> m_notify;
        bool m_started = false;
        CO_MTA_USAGE_COOKIE m_mta = nullptr; // keeps the MTA (and a stream proxy in it) alive while the engine reads
    };
}
