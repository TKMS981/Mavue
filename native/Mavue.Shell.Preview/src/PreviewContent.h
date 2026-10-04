#pragma once

#include "Imaging.h"
#include "MediaPlayer.h"
#include "Pdfium.h"

#include <d2d1_3.h>
#include <dwrite.h>

#include <map>
#include <memory>
#include <string>
#include <vector>

namespace mavue
{
    struct PreviewColors
    {
        COLORREF Background = RGB(255, 255, 255);
        COLORREF Text = RGB(0, 0, 0);
    };

    struct WorkerState;
    struct LoadResult;
    struct PageResult;

    // The preview pane content: a child window of Explorer's preview area (in the preview host process). Decoding
    // and PDF page rendering run on a background thread; the window thread only draws (Direct2D) and handles input,
    // so a slow or broken file never blocks the host.
    class PreviewWindow final
    {
    public:
        static std::unique_ptr<PreviewWindow> Create(HWND parent, const RECT& rect, const PreviewColors& colors, IStream* stream);
        ~PreviewWindow();
        PreviewWindow(const PreviewWindow&) = delete;
        PreviewWindow& operator=(const PreviewWindow&) = delete;

        HWND Handle() const noexcept { return m_hwnd; }
        void SetParent(HWND parent, const RECT& rect);
        void SetRect(const RECT& rect);
        void SetColors(const PreviewColors& colors);
        void Focus();

        static void UnregisterClasses() noexcept;

    private:
        PreviewWindow() = default;
        static LRESULT CALLBACK WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam);
        LRESULT Handle(UINT message, WPARAM wParam, LPARAM lParam);
        bool Start(HWND parent, const RECT& rect, IStream* stream);

        // Drawing
        void Paint();
        bool EnsureTarget();
        void DiscardDeviceResources();
        float Scale(float dips) const;
        void DrawMessage(const std::wstring& text);
        void DrawImage();
        void DrawSvg();
        void DrawPdf();
        void DrawMedia();
        void DrawPill(const std::wstring& text, float right, float bottom);
        D2D1_RECT_F ContentBox() const;
        ComPtr<ID2D1Bitmap> CreateBitmap(const Bitmap& image, bool opaque);

        // Content
        void OnLoaded(std::unique_ptr<LoadResult> result);
        void OnPage(std::unique_ptr<PageResult> page);
        void OnMediaEvent(DWORD event, HRESULT error);
        void LayoutMedia();
        void ShowFailure(UINT messageId, const std::string& reason);
        void AdvanceFrame();

        // PDF
        void LayoutPdf();
        void RequestPages();
        void ScrollTo(double offset);
        int PageAt(double y) const;
        void UpdateScrollBar();

        // Input
        void OnKey(WPARAM key);
        void OnClick(int x, int y);
        void OnWheel(int delta);

        HWND m_hwnd = nullptr;
        HWND m_video = nullptr;
        PreviewColors m_given;  // from the host (IPreviewHandlerVisuals)
        PreviewColors m_colors; // what is drawn: m_given, or a dark palette in dark mode (see ColorsForTheme)
        ComPtr<IStream> m_stream;
        std::shared_ptr<WorkerState> m_worker;
        LONGLONG m_started = 0;
        bool m_shownTraced = false;

        ComPtr<ID2D1Factory1> m_factory;
        ComPtr<IDWriteFactory> m_writeFactory;
        ComPtr<ID2D1HwndRenderTarget> m_target;
        ComPtr<ID2D1DeviceContext> m_context;
        ComPtr<ID2D1DeviceContext5> m_context5;
        ComPtr<IDWriteTextFormat> m_textFormat;
        ComPtr<IDWriteTextFormat> m_iconFormat;
        UINT m_formatDpi = 0;

        // State
        bool m_loading = true;
        bool m_showLoading = false;
        std::wstring m_message;
        std::unique_ptr<LoadResult> m_content;

        // Image / animation
        ComPtr<ID2D1Bitmap> m_imageBitmap;
        size_t m_frame = 0;

        // SVG
        ComPtr<ID2D1SvgDocument> m_svg;
        D2D1_SIZE_F m_svgIntrinsic{};
        D2D1_SIZE_F m_svgFitted{};

        // PDF
        std::vector<double> m_pageTops;
        std::vector<D2D1_SIZE_F> m_pageSizes; // pixels at the current layout
        double m_contentHeight = 0;
        double m_scroll = 0;
        UINT m_layoutGeneration = 0;
        struct CachedPage
        {
            ComPtr<ID2D1Bitmap> Bitmap;
            UINT Width = 0;
            UINT Height = 0;
        };
        std::map<int, CachedPage> m_pages;
        std::map<int, std::pair<UINT, UINT>> m_pending;

        // Media
        std::unique_ptr<MediaPlayer> m_media;
        std::wstring m_mediaName;
        bool m_mediaReady = false;
        RECT m_playRect{};
        RECT m_seekRect{};
        RECT m_muteRect{};
    };
}
