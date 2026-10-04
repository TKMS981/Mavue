#pragma once

#include "PreviewContent.h"

#include <shobjidl.h>

#include <memory>

namespace mavue
{
    // File Explorer preview pane handler. Runs in the preview host (prevhost.exe, Mavue's own instance through its
    // AppID) on an STA thread; initialized with a stream (works for any shell item, not only files on disk).
    class PreviewHandler final : public IPreviewHandler, public IInitializeWithStream, public IObjectWithSite,
        public IOleWindow, public IPreviewHandlerVisuals
    {
    public:
        PreviewHandler() noexcept;

        IFACEMETHODIMP QueryInterface(REFIID riid, void** object) override;
        IFACEMETHODIMP_(ULONG) AddRef() override;
        IFACEMETHODIMP_(ULONG) Release() override;

        // IInitializeWithStream
        IFACEMETHODIMP Initialize(IStream* stream, DWORD mode) override;

        // IPreviewHandler
        IFACEMETHODIMP SetWindow(HWND parent, const RECT* rect) override;
        IFACEMETHODIMP SetRect(const RECT* rect) override;
        IFACEMETHODIMP DoPreview() override;
        IFACEMETHODIMP Unload() override;
        IFACEMETHODIMP SetFocus() override;
        IFACEMETHODIMP QueryFocus(HWND* focus) override;
        IFACEMETHODIMP TranslateAccelerator(MSG* message) override;

        // IObjectWithSite
        IFACEMETHODIMP SetSite(IUnknown* site) override;
        IFACEMETHODIMP GetSite(REFIID riid, void** site) override;

        // IOleWindow
        IFACEMETHODIMP GetWindow(HWND* window) override;
        IFACEMETHODIMP ContextSensitiveHelp(BOOL enter) override;

        // IPreviewHandlerVisuals
        IFACEMETHODIMP SetBackgroundColor(COLORREF color) override;
        IFACEMETHODIMP SetFont(const LOGFONTW* font) override;
        IFACEMETHODIMP SetTextColor(COLORREF color) override;

    private:
        ~PreviewHandler();
        LONG m_refs = 1;
        HWND m_parent = nullptr;
        RECT m_rect{};
        ComPtr<IStream> m_stream;
        ComPtr<IUnknown> m_site;
        ComPtr<IPreviewHandlerFrame> m_frame;
        PreviewColors m_colors;
        std::unique_ptr<PreviewWindow> m_window;
    };
}
