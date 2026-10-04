#pragma once

#include "Module.h"

#include <thumbcache.h>

namespace mavue
{
    // Thumbnails for File Explorer: first page of a PDF, SVG, and images through WIC. Registered only for file types
    // that have no thumbnail provider of their own (Mavue.Shell.ShellHandlerRegistration). Initialized with a stream,
    // so Explorer runs it in its isolated thumbnail process (no DisableProcessIsolation).
    class ThumbnailProvider final : public IInitializeWithStream, public IThumbnailProvider
    {
    public:
        ThumbnailProvider() noexcept;

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override;
        IFACEMETHODIMP_(ULONG) AddRef() override;
        IFACEMETHODIMP_(ULONG) Release() override;

        // IInitializeWithStream
        IFACEMETHODIMP Initialize(IStream* stream, DWORD mode) override;

        // IThumbnailProvider
        IFACEMETHODIMP GetThumbnail(UINT size, HBITMAP* bitmap, WTS_ALPHATYPE* alpha) override;

    private:
        ~ThumbnailProvider();
        LONG m_refs = 1;
        ComPtr<IStream> m_stream;
    };
}
