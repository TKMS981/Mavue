#include "PreviewContent.h"

#include "Resources.h"

#include <shlwapi.h>
#include <windowsx.h>

#include <algorithm>
#include <cmath>
#include <condition_variable>
#include <mutex>
#include <thread>

namespace mavue
{
    namespace
    {
        constexpr UINT WM_PREVIEW_LOADED = WM_APP + 1;
        constexpr UINT WM_PREVIEW_PAGE = WM_APP + 2;
        constexpr UINT WM_PREVIEW_MEDIA = WM_APP + 3;
        constexpr UINT_PTR TimerFrame = 1;
        constexpr UINT_PTR TimerLoading = 2;
        constexpr UINT_PTR TimerMedia = 3;

        constexpr ULONGLONG GifBudgetBytes = 256ull * 1024 * 1024;
        constexpr ULONGLONG MaxSvgBytes = 32ull * 1024 * 1024;
        constexpr UINT MaxDecodeSide = 4096;

        constexpr wchar_t ClassName[] = L"Mavue.Shell.Preview";
        constexpr wchar_t VideoClassName[] = L"Mavue.Shell.Preview.Video";
        bool g_registered = false;

        D2D1_COLOR_F Color(COLORREF color, float alpha = 1.0f)
        {
            return D2D1::ColorF(GetRValue(color) / 255.0f, GetGValue(color) / 255.0f, GetBValue(color) / 255.0f, alpha);
        }

        // Halfway between two colors (borders, tracks).
        COLORREF Mix(COLORREF a, COLORREF b, double amount)
        {
            auto channel = [&](BYTE x, BYTE y) { return static_cast<BYTE>(std::lround(x + (y - x) * amount)); };
            return RGB(channel(GetRValue(a), GetRValue(b)), channel(GetGValue(a), GetGValue(b)), channel(GetBValue(a), GetBValue(b)));
        }

        // Explorer's preview pane is per-monitor (v2) aware and passes physical pixels; the preview host thread may not
        // be (its windows then got Explorer's rectangle scaled by the system DPI on another monitor: 150 % on a 100 %
        // monitor, measured). The preview window is created and placed as per-monitor v2; the thread's own context
        // is restored afterwards (other handlers can share the host thread).
        class PerMonitorDpi final
        {
        public:
            PerMonitorDpi() noexcept : m_previous(SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {}
            ~PerMonitorDpi()
            {
                if (m_previous != nullptr)
                {
                    SetThreadDpiAwarenessContext(m_previous);
                }
            }

            PerMonitorDpi(const PerMonitorDpi&) = delete;
            PerMonitorDpi& operator=(const PerMonitorDpi&) = delete;
            DPI_AWARENESS Previous() const noexcept { return m_previous != nullptr ? GetAwarenessFromDpiAwarenessContext(m_previous) : DPI_AWARENESS_INVALID; }

        private:
            DPI_AWARENESS_CONTEXT m_previous;
        };

        bool AppsUseDarkTheme()
        {
            DWORD light = 1;
            DWORD size = sizeof(light);
            return RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", L"AppsUseLightTheme",
                RRF_RT_REG_DWORD, nullptr, &light, &size) == ERROR_SUCCESS && light == 0;
        }

        // File Explorer passes white/black through IPreviewHandlerVisuals even in dark mode (measured on Windows 11
        // 25H2), which left a white panel in a dark window. In dark mode a light background from the host is replaced
        // by Explorer's own dark colors; colors that are already dark are kept.
        PreviewColors ColorsForTheme(const PreviewColors& given)
        {
            double luminance = 0.299 * GetRValue(given.Background) + 0.587 * GetGValue(given.Background) + 0.114 * GetBValue(given.Background);
            if (AppsUseDarkTheme() && luminance > 128)
            {
                return PreviewColors{ RGB(25, 25, 25), RGB(235, 235, 235) };
            }

            return given;
        }

        const char* KindName(ContentKind kind, bool media)
        {
            if (media)
            {
                return "media";
            }

            switch (kind)
            {
            case ContentKind::Image: return "image";
            case ContentKind::Svg: return "svg";
            case ContentKind::Pdf: return "pdf";
            default: return "unknown";
            }
        }

        std::wstring FormatTime(double seconds)
        {
            if (!(seconds >= 0) || seconds > 1e7)
            {
                seconds = 0;
            }

            auto total = static_cast<long long>(seconds);
            wchar_t text[32]{};
            if (total >= 3600)
            {
                swprintf_s(text, L"%lld:%02lld:%02lld", total / 3600, (total / 60) % 60, total % 60);
            }
            else
            {
                swprintf_s(text, L"%lld:%02lld", total / 60, total % 60);
            }

            return text;
        }

        bool Contains(const RECT& rect, int x, int y) { return x >= rect.left && x < rect.right && y >= rect.top && y < rect.bottom; }
    }

    struct LoadResult
    {
        ContentKind Kind = ContentKind::Unknown;
        bool Media = false;
        UINT Message = 0;      // string id when nothing can be shown
        std::string Reason;    // for the trace
        Bitmap Image;
        UINT SourceWidth = 0;
        UINT SourceHeight = 0;
        std::vector<AnimationFrame> Frames;
        std::vector<BYTE> Svg;
        std::vector<pdf::PageSize> Pages;
        std::unique_ptr<MediaPlayer> Player;
        HRESULT MediaHr = S_OK;
        bool ByPath = false;
        std::wstring MediaPath; // a file on disk (else the stream is used)
        std::wstring MediaName;
        double Ms = 0;
    };

    struct PageResult
    {
        int Index = 0;
        UINT Width = 0;
        UINT Height = 0;
        std::vector<BYTE> Pixels;
        double Ms = 0;
    };

    struct PageRequest
    {
        int Index = 0;
        UINT Width = 0;
        UINT Height = 0;
    };

    // Shared by the window and its background thread. The window clears `Window` (under the mutex) before it is
    // destroyed, so nothing is posted to it afterwards; the thread then ends on its own (it is never joined).
    struct WorkerState
    {
        std::mutex Mutex;
        std::condition_variable Changed;
        HWND Window = nullptr;
        HWND Video = nullptr; // the (hidden) child the media engine draws video into
        bool Stop = false;
        std::atomic<bool> Cancel{ false };
        std::vector<PageRequest> Requests;
        UINT MaxWidth = MaxDecodeSide;
        UINT MaxHeight = MaxDecodeSide;

        template <typename T>
        void Post(UINT message, std::unique_ptr<T> payload)
        {
            std::lock_guard lock(Mutex);
            if (Window != nullptr && PostMessageW(Window, message, 0, reinterpret_cast<LPARAM>(payload.get())))
            {
                payload.release(); // the window owns it now
            }
        }
    };

    namespace
    {
        // Media Foundation shutdown takes ~200 ms and waits in COM: on its own thread, so Unload returns at once and
        // the preview window's thread (whose input Explorer shares) never pumps while Explorer has keys pending.
        // `windowToClose` (the detached video window the engine drew into) is closed when the engine is gone.
        void ShutdownInBackground(std::unique_ptr<MediaPlayer> player, HWND windowToClose)
        {
            if (player)
            {
                player->DetachNotify();
                try
                {
                    std::thread([held = std::move(player), windowToClose]() mutable
                    {
                        ModuleLock moduleLock;
                        held->Shutdown();
                        held.reset();
                        if (windowToClose != nullptr)
                        {
                            PostMessageW(windowToClose, WM_CLOSE, 0, 0);
                        }
                    }).detach();
                    return;
                }
                catch (const std::system_error&)
                {
                    // the player went with the failed thread object (shut down here, synchronously)
                }
            }

            if (windowToClose != nullptr)
            {
                PostMessageW(windowToClose, WM_CLOSE, 0, 0);
            }
        }

        void Discard(LoadResult* result)
        {
            std::unique_ptr<LoadResult> owned(result);
            if (owned)
            {
                ShutdownInBackground(std::move(owned->Player), nullptr);
            }
        }

        void WorkerMain(std::shared_ptr<WorkerState> state, IStream* marshaled)
        {
            ModuleLock moduleLock;
            ComApartment apartment(COINIT_MULTITHREADED);
            ComPtr<IStream> stream;
            HRESULT hr = E_FAIL;
            if (apartment.Ok())
            {
                hr = CoGetInterfaceAndReleaseStream(marshaled, IID_PPV_ARGS(&stream)); // releases `marshaled`
            }
            else if (marshaled != nullptr)
            {
                marshaled->Release();
            }

            auto result = std::make_unique<LoadResult>();
            LONGLONG start = Now();
            std::unique_ptr<pdf::Document> document;
            try
            {
                if (FAILED(hr))
                {
                    result->Message = IDS_CANNOT_PREVIEW;
                    result->Reason = "stream " + HResultText(hr);
                }
                else if (StreamSize(stream.Get()) == 0)
                {
                    result->Message = IDS_EMPTY;
                    result->Reason = "empty";
                }
                else
                {
                    ComPtr<IWICImagingFactory> wic = CreateWicFactory();
                    result->Kind = Sniff(stream.Get(), wic.Get());
                    switch (result->Kind)
                    {
                    case ContentKind::Pdf:
                        switch (pdf::Document::Open(stream.Get(), document))
                        {
                        case pdf::OpenResult::Ok:
                            for (int i = 0; i < document->PageCount(); i++)
                            {
                                result->Pages.push_back(document->Size(i));
                            }

                            break;
                        case pdf::OpenResult::Password:
                            result->Message = IDS_PASSWORD;
                            result->Reason = "password";
                            break;
                        case pdf::OpenResult::Unavailable:
                            result->Message = IDS_PDF_UNAVAILABLE;
                            result->Reason = "pdfium";
                            break;
                        default:
                            result->Message = IDS_CANNOT_PREVIEW;
                            result->Reason = "pdf";
                            break;
                        }

                        break;
                    case ContentKind::Svg:
                        hr = ReadAll(stream.Get(), MaxSvgBytes, result->Svg);
                        if (FAILED(hr))
                        {
                            result->Message = hr == E_OUTOFMEMORY ? IDS_TOO_LARGE : IDS_CANNOT_PREVIEW;
                            result->Reason = "svg " + HResultText(hr);
                        }

                        break;
                    case ContentKind::Image:
                    {
                        GUID container{};
                        hr = DecodeImage(wic.Get(), stream.Get(), state->MaxWidth, state->MaxHeight, result->Image, result->SourceWidth, result->SourceHeight, container);
                        if (FAILED(hr))
                        {
                            result->Message = hr == WINCODEC_ERR_IMAGESIZEOUTOFRANGE ? IDS_TOO_LARGE : IDS_CANNOT_PREVIEW;
                            result->Reason = "image " + HResultText(hr);
                        }
                        else if (container == GUID_ContainerFormatGif && !state->Cancel.load())
                        {
                            if (DecodeGifFrames(wic.Get(), stream.Get(), GifBudgetBytes, result->Frames, &state->Cancel) != S_OK)
                            {
                                result->Frames.clear(); // one frame, or too big to animate: the still first frame
                            }
                            else
                            {
                                result->SourceWidth = result->Frames.front().Image.Width;
                                result->SourceHeight = result->Frames.front().Image.Height;
                            }
                        }

                        break;
                    }
                    default:
                    {
                        // Video or audio (or nothing playable): the window creates the media engine.
                        result->Media = true;
                        std::wstring path;
                        STATSTG stat{};
                        if (SUCCEEDED(stream->Stat(&stat, STATFLAG_DEFAULT)) && stat.pwcsName != nullptr)
                        {
                            result->MediaName = PathFindFileNameW(stat.pwcsName);
                            if (!PathIsRelativeW(stat.pwcsName) && GetFileAttributesW(stat.pwcsName) != INVALID_FILE_ATTRIBUTES)
                            {
                                path = stat.pwcsName; // a file on disk: opened by path (seeks without the stream)
                            }

                            CoTaskMemFree(stat.pwcsName);
                        }

                        result->ByPath = !path.empty();
                        result->MediaPath = path;
                        break;
                    }
                    }
                }
            }
            catch (const std::bad_alloc&)
            {
                ShutdownInBackground(std::move(result->Player), nullptr);
                *result = LoadResult{};
                result->Message = IDS_CANNOT_PREVIEW;
                result->Reason = "memory";
                document.reset();
            }

            result->Ms = Milliseconds(start, Now());
            bool pdfOpen = document != nullptr && result->Message == 0;
            state->Post(WM_PREVIEW_LOADED, std::move(result));
            if (!pdfOpen)
            {
                return;
            }

            // PDF pages, drawn on request (only the pages near the visible area, at the size they are shown).
            for (;;)
            {
                PageRequest request;
                {
                    std::unique_lock lock(state->Mutex);
                    state->Changed.wait(lock, [&] { return state->Stop || !state->Requests.empty(); });
                    if (state->Stop)
                    {
                        break;
                    }

                    request = state->Requests.front();
                    state->Requests.erase(state->Requests.begin());
                }

                auto page = std::make_unique<PageResult>();
                page->Index = request.Index;
                page->Width = request.Width;
                page->Height = request.Height;
                LONGLONG pageStart = Now();
                bool drawn = false;
                try
                {
                    drawn = document->Render(request.Index, static_cast<int>(request.Width), static_cast<int>(request.Height), page->Pixels, &state->Cancel);
                }
                catch (const std::bad_alloc&)
                {
                    drawn = false;
                }

                if (!drawn)
                {
                    page->Pixels.clear(); // reported with no pixels: the page stays white
                }

                page->Ms = Milliseconds(pageStart, Now());
                state->Post(WM_PREVIEW_PAGE, std::move(page));
            }
        }
    }

    float PreviewWindow::Scale(float dips) const
    {
        UINT dpi = m_hwnd != nullptr ? GetDpiForWindow(m_hwnd) : 96;
        return dips * static_cast<float>(dpi == 0 ? 96 : dpi) / 96.0f;
    }

    void PreviewWindow::UnregisterClasses() noexcept
    {
        if (g_registered)
        {
            // A detached video window may still be closing (its engine shuts down in the background): try again later.
            bool video = UnregisterClassW(VideoClassName, Module()) != FALSE || GetLastError() == ERROR_CLASS_DOES_NOT_EXIST;
            bool window = UnregisterClassW(ClassName, Module()) != FALSE || GetLastError() == ERROR_CLASS_DOES_NOT_EXIST;
            g_registered = !(video && window);
        }
    }

    std::unique_ptr<PreviewWindow> PreviewWindow::Create(HWND parent, const RECT& rect, const PreviewColors& colors, IStream* stream)
    {
        std::unique_ptr<PreviewWindow> window(new (std::nothrow) PreviewWindow());
        if (!window)
        {
            return nullptr;
        }

        window->m_given = colors;
        window->m_colors = ColorsForTheme(colors);
        if (!window->Start(parent, rect, stream))
        {
            return nullptr;
        }

        return window;
    }

    bool PreviewWindow::Start(HWND parent, const RECT& rect, IStream* stream)
    {
        if (!g_registered)
        {
            WNDCLASSEXW window{ sizeof(window) };
            window.style = CS_HREDRAW | CS_VREDRAW | CS_DBLCLKS;
            window.lpfnWndProc = &PreviewWindow::WindowProc;
            window.hInstance = Module();
            window.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            window.lpszClassName = ClassName;
            WNDCLASSEXW video{ sizeof(video) };
            video.lpfnWndProc = DefWindowProcW;
            video.hInstance = Module();
            video.hCursor = LoadCursorW(nullptr, IDC_HAND);
            video.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
            video.lpszClassName = VideoClassName;
            if (RegisterClassExW(&window) == 0 || RegisterClassExW(&video) == 0)
            {
                UnregisterClassW(ClassName, Module());
                return false;
            }

            g_registered = true;
        }

        m_started = Now();
        m_stream = stream;
        if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), nullptr, reinterpret_cast<void**>(m_factory.GetAddressOf()))) ||
            FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), reinterpret_cast<IUnknown**>(m_writeFactory.GetAddressOf()))))
        {
            return false;
        }

        PerMonitorDpi dpi;
        m_hwnd = CreateWindowExW(0, ClassName, L"", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, rect.left, rect.top,
            std::max(0L, rect.right - rect.left), std::max(0L, rect.bottom - rect.top), parent, nullptr, Module(), this);
        if (m_hwnd == nullptr)
        {
            return false;
        }

        m_video = CreateWindowExW(0, VideoClassName, L"", WS_CHILD | WS_CLIPSIBLINGS, 0, 0, 1, 1, m_hwnd, nullptr, Module(), nullptr);
        m_worker = std::make_shared<WorkerState>();
        m_worker->Window = m_hwnd;
        m_worker->Video = m_video;
        MONITORINFO monitor{ sizeof(monitor) };
        if (GetMonitorInfoW(MonitorFromWindow(parent, MONITOR_DEFAULTTONEAREST), &monitor))
        {
            // Decoded once for the largest the pane can be (its monitor), so resizing never decodes again.
            m_worker->MaxWidth = std::clamp<UINT>(static_cast<UINT>(monitor.rcMonitor.right - monitor.rcMonitor.left), 256, MaxDecodeSide);
            m_worker->MaxHeight = std::clamp<UINT>(static_cast<UINT>(monitor.rcMonitor.bottom - monitor.rcMonitor.top), 256, MaxDecodeSide);
        }

        IStream* marshaled = nullptr;
        HRESULT hr = CoMarshalInterThreadInterfaceInStream(IID_IStream, stream, &marshaled);
        if (FAILED(hr))
        {
            ShowFailure(IDS_CANNOT_PREVIEW, "marshal " + HResultText(hr));
            return true;
        }

        try
        {
            std::thread(WorkerMain, m_worker, marshaled).detach();
        }
        catch (const std::system_error&)
        {
            marshaled->Release();
            ShowFailure(IDS_CANNOT_PREVIEW, "thread");
            return true;
        }

        SetTimer(m_hwnd, TimerLoading, 400, nullptr);
        Trace("preview-start", "\"width\":" + std::to_string(rect.right - rect.left) + ",\"height\":" + std::to_string(rect.bottom - rect.top) +
            ",\"dpi\":" + std::to_string(GetDpiForWindow(m_hwnd)) + ",\"hostAwareness\":" + std::to_string(static_cast<int>(dpi.Previous())));
        return true;
    }

    PreviewWindow::~PreviewWindow()
    {
        if (m_worker)
        {
            {
                std::lock_guard lock(m_worker->Mutex);
                m_worker->Window = nullptr;
                m_worker->Stop = true;
                m_worker->Cancel = true;
                m_worker->Requests.clear();
            }

            m_worker->Changed.notify_all();
        }

        if (m_hwnd != nullptr)
        {
            MSG message{};
            while (PeekMessageW(&message, m_hwnd, WM_PREVIEW_LOADED, WM_PREVIEW_PAGE, PM_REMOVE))
            {
                if (message.message == WM_PREVIEW_LOADED)
                {
                    Discard(reinterpret_cast<LoadResult*>(message.lParam));
                }
                else
                {
                    delete reinterpret_cast<PageResult*>(message.lParam);
                }
            }
        }

        LONGLONG unloadStart = Now();
        bool media = m_media != nullptr;
        if (m_media)
        {
            // The engine draws into m_video until it is shut down: keep that window until then, as a message-only
            // window (out of the pane, never shown or activated: a top-level window here took the foreground).
            HWND video = m_video;
            m_video = nullptr;
            if (video != nullptr)
            {
                ShowWindow(video, SW_HIDE);
                ::SetParent(video, HWND_MESSAGE);
            }

            ShutdownInBackground(std::move(m_media), video);
        }

        if (TraceEnabled())
        {
            Trace("preview-unload", std::string("\"media\":") + (media ? "true" : "false") + ",\"ms\":" + std::to_string(Milliseconds(unloadStart, Now())));
        }

        DiscardDeviceResources();
        if (m_hwnd != nullptr)
        {
            SetWindowLongPtrW(m_hwnd, GWLP_USERDATA, 0);
            DestroyWindow(m_hwnd);
            m_hwnd = nullptr;
        }
    }

    void PreviewWindow::SetParent(HWND parent, const RECT& rect)
    {
        if (m_hwnd != nullptr)
        {
            PerMonitorDpi dpi;
            ::SetParent(m_hwnd, parent);
            SetRect(rect);
        }
    }

    void PreviewWindow::SetRect(const RECT& rect)
    {
        if (m_hwnd != nullptr)
        {
            PerMonitorDpi dpi;
            SetWindowPos(m_hwnd, nullptr, rect.left, rect.top, std::max(0L, rect.right - rect.left), std::max(0L, rect.bottom - rect.top),
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    void PreviewWindow::SetColors(const PreviewColors& colors)
    {
        m_given = colors;
        m_colors = ColorsForTheme(colors);
        if (m_hwnd != nullptr)
        {
            InvalidateRect(m_hwnd, nullptr, FALSE);
        }
    }

    void PreviewWindow::Focus()
    {
        if (m_hwnd != nullptr)
        {
            ::SetFocus(m_hwnd);
        }
    }

    LRESULT CALLBACK PreviewWindow::WindowProc(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam)
    {
        if (message == WM_NCCREATE)
        {
            auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            SetWindowLongPtrW(hwnd, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(create->lpCreateParams));
        }

        auto* self = reinterpret_cast<PreviewWindow*>(GetWindowLongPtrW(hwnd, GWLP_USERDATA));
        if (self == nullptr || self->m_hwnd == nullptr)
        {
            if (message == WM_PREVIEW_LOADED)
            {
                Discard(reinterpret_cast<LoadResult*>(lParam));
                return 0;
            }

            if (message == WM_PREVIEW_PAGE)
            {
                delete reinterpret_cast<PageResult*>(lParam);
                return 0;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        return self->Handle(message, wParam, lParam);
    }

    LRESULT PreviewWindow::Handle(UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
        case WM_PAINT:
            Paint();
            return 0;
        case WM_ERASEBKGND:
            return 1;
        case WM_SETTINGCHANGE:
            if (lParam != 0 && lstrcmpiW(reinterpret_cast<const wchar_t*>(lParam), L"ImmersiveColorSet") == 0)
            {
                m_colors = ColorsForTheme(m_given); // the Windows theme changed while the preview is open
                InvalidateRect(m_hwnd, nullptr, FALSE);
            }

            break;
        case WM_SIZE:
            if (m_target)
            {
                m_target->Resize(D2D1::SizeU(LOWORD(lParam), HIWORD(lParam)));
            }

            if (m_content && m_content->Kind == ContentKind::Pdf && m_content->Message == 0)
            {
                LayoutPdf();
            }

            if (m_media)
            {
                LayoutMedia();
            }

            InvalidateRect(m_hwnd, nullptr, FALSE);
            return 0;
        case WM_DPICHANGED_AFTERPARENT:
            Trace("preview-dpi", "\"dpi\":" + std::to_string(GetDpiForWindow(m_hwnd)));
            m_formatDpi = 0;
            if (m_content && m_content->Kind == ContentKind::Pdf)
            {
                LayoutPdf();
            }

            InvalidateRect(m_hwnd, nullptr, FALSE);
            return 0;
        case WM_TIMER:
            if (wParam == TimerFrame)
            {
                AdvanceFrame();
            }
            else if (wParam == TimerLoading)
            {
                KillTimer(m_hwnd, TimerLoading);
                m_showLoading = true;
                InvalidateRect(m_hwnd, nullptr, FALSE);
            }
            else if (wParam == TimerMedia)
            {
                InvalidateRect(m_hwnd, nullptr, FALSE);
            }

            return 0;
        case WM_PREVIEW_LOADED:
            OnLoaded(std::unique_ptr<LoadResult>(reinterpret_cast<LoadResult*>(lParam)));
            return 0;
        case WM_PREVIEW_PAGE:
            OnPage(std::unique_ptr<PageResult>(reinterpret_cast<PageResult*>(lParam)));
            return 0;
        case WM_PREVIEW_MEDIA:
            OnMediaEvent(static_cast<DWORD>(wParam), static_cast<HRESULT>(lParam));
            return 0;
        case WM_VSCROLL:
        {
            if (m_pageTops.empty())
            {
                return 0;
            }

            RECT client{};
            GetClientRect(m_hwnd, &client);
            double line = Scale(40);
            double page = std::max<double>(line, client.bottom - line);
            switch (LOWORD(wParam))
            {
            case SB_LINEUP: ScrollTo(m_scroll - line); break;
            case SB_LINEDOWN: ScrollTo(m_scroll + line); break;
            case SB_PAGEUP: ScrollTo(m_scroll - page); break;
            case SB_PAGEDOWN: ScrollTo(m_scroll + page); break;
            case SB_TOP: ScrollTo(0); break;
            case SB_BOTTOM: ScrollTo(m_contentHeight); break;
            case SB_THUMBTRACK:
            case SB_THUMBPOSITION:
            {
                SCROLLINFO info{ sizeof(info), SIF_TRACKPOS };
                GetScrollInfo(m_hwnd, SB_VERT, &info);
                ScrollTo(info.nTrackPos);
                break;
            }
            default: break;
            }

            return 0;
        }
        case WM_MOUSEWHEEL:
            OnWheel(GET_WHEEL_DELTA_WPARAM(wParam));
            return 0;
        case WM_LBUTTONDOWN:
            ::SetFocus(m_hwnd);
            OnClick(GET_X_LPARAM(lParam), GET_Y_LPARAM(lParam));
            return 0;
        case WM_KEYDOWN:
            OnKey(wParam);
            return 0;
        case WM_GETDLGCODE:
            return DLGC_WANTARROWS | DLGC_WANTCHARS;
        case WM_PARENTNOTIFY:
            if (LOWORD(wParam) == WM_LBUTTONDOWN && m_media)
            {
                ::SetFocus(m_hwnd);
                m_media->TogglePlay(); // a click on the video
                InvalidateRect(m_hwnd, nullptr, FALSE);
            }

            break;
        default:
            break;
        }

        return DefWindowProcW(m_hwnd, message, wParam, lParam);
    }

    // ---------------------------------------------------------------- drawing

    bool PreviewWindow::EnsureTarget()
    {
        if (m_target)
        {
            return true;
        }

        LONGLONG start = Now();
        RECT client{};
        GetClientRect(m_hwnd, &client);
        D2D1_RENDER_TARGET_PROPERTIES properties = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96); // pixels, not DIPs
        D2D1_HWND_RENDER_TARGET_PROPERTIES window = D2D1::HwndRenderTargetProperties(m_hwnd,
            D2D1::SizeU(static_cast<UINT>(std::max(1L, client.right)), static_cast<UINT>(std::max(1L, client.bottom))));
        if (FAILED(m_factory->CreateHwndRenderTarget(properties, window, &m_target)))
        {
            return false;
        }

        m_target.As(&m_context);
        m_target.As(&m_context5); // SVG (Windows 10 1703+)
        Trace("preview-target", "\"ms\":" + std::to_string(Milliseconds(start, Now())) + ",\"svg\":" + (m_context5 ? "true" : "false"));
        return true;
    }

    void PreviewWindow::DiscardDeviceResources()
    {
        m_imageBitmap.Reset();
        m_svg.Reset();
        m_svgFitted = {};
        m_pages.clear();
        m_pending.clear();
        m_context5.Reset();
        m_context.Reset();
        m_target.Reset();
    }

    ComPtr<ID2D1Bitmap> PreviewWindow::CreateBitmap(const Bitmap& image, bool opaque)
    {
        ComPtr<ID2D1Bitmap> bitmap;
        if (!m_target || image.Width == 0 || image.Height == 0 || image.Pixels.empty())
        {
            return bitmap;
        }

        UINT32 maxSize = m_target->GetMaximumBitmapSize();
        if (image.Width > maxSize || image.Height > maxSize)
        {
            return bitmap;
        }

        D2D1_BITMAP_PROPERTIES properties = D2D1::BitmapProperties(
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, opaque ? D2D1_ALPHA_MODE_IGNORE : D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96);
        m_target->CreateBitmap(D2D1::SizeU(image.Width, image.Height), image.Pixels.data(), image.Width * 4, properties, &bitmap);
        return bitmap;
    }

    D2D1_RECT_F PreviewWindow::ContentBox() const
    {
        RECT client{};
        GetClientRect(m_hwnd, &client);
        float pad = Scale(8);
        return D2D1::RectF(pad, pad, std::max(pad, client.right - pad), std::max(pad, client.bottom - pad));
    }

    void PreviewWindow::Paint()
    {
        PAINTSTRUCT paint{};
        BeginPaint(m_hwnd, &paint);
        if (EnsureTarget())
        {
            UINT dpi = GetDpiForWindow(m_hwnd);
            if (m_formatDpi != dpi)
            {
                m_textFormat.Reset();
                m_iconFormat.Reset();
                float size = Scale(13.0f);
                if (SUCCEEDED(m_writeFactory->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH_NORMAL, size, L"", &m_textFormat)))
                {
                    m_textFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                    m_textFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                    m_textFormat->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP);
                }

                // Media control glyphs: Segoe Fluent Icons (Windows 11), else Segoe MDL2 Assets (Windows 10).
                ComPtr<IDWriteFontCollection> fonts;
                UINT32 index = 0;
                BOOL exists = FALSE;
                const wchar_t* iconFamily = L"Segoe MDL2 Assets";
                if (SUCCEEDED(m_writeFactory->GetSystemFontCollection(&fonts)) && SUCCEEDED(fonts->FindFamilyName(L"Segoe Fluent Icons", &index, &exists)) && exists)
                {
                    iconFamily = L"Segoe Fluent Icons";
                }

                if (SUCCEEDED(m_writeFactory->CreateTextFormat(iconFamily, nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                    DWRITE_FONT_STRETCH_NORMAL, Scale(16.0f), L"", &m_iconFormat)))
                {
                    m_iconFormat->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                    m_iconFormat->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                }

                m_formatDpi = dpi;
            }

            m_target->BeginDraw();
            m_target->SetTransform(D2D1::Matrix3x2F::Identity());
            m_target->Clear(Color(m_colors.Background));
            if (!m_message.empty())
            {
                DrawMessage(m_message);
            }
            else if (m_loading)
            {
                if (m_media)
                {
                    DrawMedia();
                }
                else if (m_showLoading)
                {
                    DrawMessage(Text(IDS_LOADING));
                }
            }
            else if (m_media)
            {
                DrawMedia();
            }
            else if (m_content)
            {
                switch (m_content->Kind)
                {
                case ContentKind::Image: DrawImage(); break;
                case ContentKind::Svg: DrawSvg(); break;
                case ContentKind::Pdf: DrawPdf(); break;
                default: break;
                }
            }

            HRESULT hr = m_target->EndDraw();
            if (hr == D2DERR_RECREATE_TARGET)
            {
                DiscardDeviceResources();
                InvalidateRect(m_hwnd, nullptr, FALSE);
            }
            else if (SUCCEEDED(hr) && !m_shownTraced && !m_loading && m_message.empty() && m_content)
            {
                m_shownTraced = true;
                RECT client{};
                GetClientRect(m_hwnd, &client);
                Trace("preview-shown", std::string("\"kind\":\"") + KindName(m_content->Kind, m_media != nullptr) + "\",\"ms\":" +
                    std::to_string(Milliseconds(m_started, Now())) + ",\"clientWidth\":" + std::to_string(client.right) +
                    ",\"clientHeight\":" + std::to_string(client.bottom));
            }
        }

        EndPaint(m_hwnd, &paint);
    }

    void PreviewWindow::DrawMessage(const std::wstring& text)
    {
        if (!m_textFormat || text.empty())
        {
            return;
        }

        ComPtr<ID2D1SolidColorBrush> brush;
        m_target->CreateSolidColorBrush(Color(m_colors.Text, 0.8f), &brush);
        D2D1_RECT_F box = ContentBox();
        m_target->DrawTextW(text.c_str(), static_cast<UINT32>(text.size()), m_textFormat.Get(), box, brush.Get());
    }

    void PreviewWindow::DrawImage()
    {
        const Bitmap& image = m_content->Frames.empty() ? m_content->Image : m_content->Frames[m_frame].Image;
        if (!m_imageBitmap)
        {
            m_imageBitmap = CreateBitmap(image, false);
            if (!m_imageBitmap)
            {
                DrawMessage(Text(IDS_TOO_LARGE));
                return;
            }
        }

        // Fitted to the pane, never larger than the image's own pixels (Mavue's rule).
        D2D1_RECT_F box = ContentBox();
        double width = 0;
        double height = 0;
        FitWithin(m_content->SourceWidth, m_content->SourceHeight, box.right - box.left, box.bottom - box.top, width, height);
        width = std::min<double>(width, m_content->SourceWidth);
        height = std::min<double>(height, m_content->SourceHeight);
        float left = std::floor((box.left + box.right - static_cast<float>(width)) / 2);
        float top = std::floor((box.top + box.bottom - static_cast<float>(height)) / 2);
        D2D1_RECT_F target = D2D1::RectF(left, top, left + static_cast<float>(width), top + static_cast<float>(height));
        if (m_context)
        {
            m_context->DrawBitmap(m_imageBitmap.Get(), target, 1.0f, D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, nullptr, nullptr);
        }
        else
        {
            m_target->DrawBitmap(m_imageBitmap.Get(), target, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        }
    }

    void PreviewWindow::DrawSvg()
    {
        if (!m_context5)
        {
            DrawMessage(Text(IDS_CANNOT_PREVIEW));
            return;
        }

        if (!m_svg)
        {
            if (FAILED(CreateSvg(m_context5.Get(), m_content->Svg, m_svg, m_svgIntrinsic)))
            {
                m_svg.Reset();
                DrawMessage(Text(IDS_CANNOT_PREVIEW));
                return;
            }

            m_svgFitted = {};
        }

        // A vector image: fitted to the pane (enlarged when small).
        D2D1_RECT_F box = ContentBox();
        double width = 0;
        double height = 0;
        FitWithin(m_svgIntrinsic.width, m_svgIntrinsic.height, box.right - box.left, box.bottom - box.top, width, height);
        if (width < 1 || height < 1)
        {
            return;
        }

        D2D1_SIZE_F size = D2D1::SizeF(static_cast<float>(width), static_cast<float>(height));
        if (size.width != m_svgFitted.width || size.height != m_svgFitted.height)
        {
            FitSvg(m_svg.Get(), m_svgIntrinsic, size);
            m_svgFitted = size;
        }

        float left = std::floor((box.left + box.right - size.width) / 2);
        float top = std::floor((box.top + box.bottom - size.height) / 2);
        m_context5->SetTransform(D2D1::Matrix3x2F::Translation(left, top));
        m_context5->DrawSvgDocument(m_svg.Get());
        m_context5->SetTransform(D2D1::Matrix3x2F::Identity());
    }

    void PreviewWindow::DrawPill(const std::wstring& text, float right, float bottom)
    {
        if (!m_textFormat)
        {
            return;
        }

        float width = Scale(16) + static_cast<float>(text.size()) * Scale(8);
        float height = Scale(26);
        D2D1_ROUNDED_RECT pill{ D2D1::RectF(right - width, bottom - height, right, bottom), height / 2, height / 2 };
        ComPtr<ID2D1SolidColorBrush> fill;
        ComPtr<ID2D1SolidColorBrush> ink;
        m_target->CreateSolidColorBrush(D2D1::ColorF(0, 0, 0, 0.6f), &fill);
        m_target->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &ink);
        m_target->FillRoundedRectangle(pill, fill.Get());
        m_target->DrawTextW(text.c_str(), static_cast<UINT32>(text.size()), m_textFormat.Get(), pill.rect, ink.Get());
    }

    // ---------------------------------------------------------------- PDF

    void PreviewWindow::LayoutPdf()
    {
        RECT client{};
        GetClientRect(m_hwnd, &client);
        const auto& pages = m_content->Pages;
        if (pages.empty() || client.right <= 0)
        {
            return;
        }

        double oldHeight = m_contentHeight;
        double pad = Scale(12);
        double gap = Scale(10);
        float widest = 1;
        for (const pdf::PageSize& page : pages)
        {
            widest = std::max(widest, page.Width);
        }

        // Fit width (the pane is usually tall and narrow), the same scale for every page.
        double scale = std::max(0.02, (client.right - 2 * pad) / widest);
        m_pageTops.resize(pages.size());
        m_pageSizes.resize(pages.size());
        double y = pad;
        for (size_t i = 0; i < pages.size(); i++)
        {
            m_pageTops[i] = y;
            m_pageSizes[i] = D2D1::SizeF(static_cast<float>(std::max(1.0, std::round(pages[i].Width * scale))), static_cast<float>(std::max(1.0, std::round(pages[i].Height * scale))));
            y += m_pageSizes[i].height + gap;
        }

        m_contentHeight = y - gap + pad;
        if (oldHeight > 0)
        {
            m_scroll = m_scroll / oldHeight * m_contentHeight;
        }

        m_layoutGeneration++;
        ScrollTo(m_scroll);
    }

    int PreviewWindow::PageAt(double y) const
    {
        if (m_pageTops.empty())
        {
            return 0;
        }

        auto it = std::upper_bound(m_pageTops.begin(), m_pageTops.end(), y);
        int index = static_cast<int>(it - m_pageTops.begin()) - 1;
        return std::clamp(index, 0, static_cast<int>(m_pageTops.size()) - 1);
    }

    void PreviewWindow::UpdateScrollBar()
    {
        RECT client{};
        GetClientRect(m_hwnd, &client);
        SCROLLINFO info{ sizeof(info) };
        info.fMask = SIF_RANGE | SIF_PAGE | SIF_POS | SIF_DISABLENOSCROLL; // always shown: the width (and layout) never jumps
        info.nMin = 0;
        info.nMax = static_cast<int>(std::max(0.0, m_contentHeight - 1));
        info.nPage = static_cast<UINT>(std::max(0L, client.bottom));
        info.nPos = static_cast<int>(m_scroll);
        SetScrollInfo(m_hwnd, SB_VERT, &info, TRUE);
    }

    void PreviewWindow::ScrollTo(double offset)
    {
        RECT client{};
        GetClientRect(m_hwnd, &client);
        double maximum = std::max(0.0, m_contentHeight - client.bottom);
        m_scroll = std::clamp(offset, 0.0, maximum);
        UpdateScrollBar();
        RequestPages();
        InvalidateRect(m_hwnd, nullptr, FALSE);
    }

    void PreviewWindow::RequestPages()
    {
        if (m_pageTops.empty() || !m_worker)
        {
            return;
        }

        RECT client{};
        GetClientRect(m_hwnd, &client);
        int count = static_cast<int>(m_pageTops.size());
        int first = PageAt(m_scroll);
        int last = PageAt(m_scroll + client.bottom);
        std::vector<PageRequest> requests;
        auto want = [&](int index)
        {
            if (index < 0 || index >= count)
            {
                return;
            }

            UINT width = static_cast<UINT>(m_pageSizes[static_cast<size_t>(index)].width);
            UINT height = static_cast<UINT>(m_pageSizes[static_cast<size_t>(index)].height);
            auto cached = m_pages.find(index);
            if (cached != m_pages.end() && cached->second.Width == width && cached->second.Height == height)
            {
                return;
            }

            requests.push_back({ index, width, height });
        };

        for (int i = first; i <= last; i++)
        {
            want(i); // visible pages first
        }

        want(last + 1);
        want(first - 1);

        // Keep only the bitmaps near the screen (a long document never holds more than a few pages).
        for (auto it = m_pages.begin(); it != m_pages.end();)
        {
            it = it->first < first - 3 || it->first > last + 3 ? m_pages.erase(it) : std::next(it);
        }

        m_pending.clear();
        for (const PageRequest& request : requests)
        {
            m_pending[request.Index] = { request.Width, request.Height };
        }

        {
            std::lock_guard lock(m_worker->Mutex);
            m_worker->Requests = std::move(requests); // the newest view wins
        }

        m_worker->Changed.notify_all();
    }

    void PreviewWindow::OnPage(std::unique_ptr<PageResult> page)
    {
        if (TraceEnabled())
        {
            Trace("preview-page", "\"page\":" + std::to_string(page->Index) + ",\"width\":" + std::to_string(page->Width) +
                ",\"height\":" + std::to_string(page->Height) + ",\"drawn\":" + (page->Pixels.empty() ? "false" : "true") +
                ",\"ms\":" + std::to_string(page->Ms) + ",\"cached\":" + std::to_string(m_pages.size()));
        }

        auto pending = m_pending.find(page->Index);
        if (pending != m_pending.end() && pending->second == std::make_pair(page->Width, page->Height))
        {
            m_pending.erase(pending);
        }

        if (page->Pixels.empty() || !EnsureTarget())
        {
            return;
        }

        RECT client{};
        GetClientRect(m_hwnd, &client);
        int first = PageAt(m_scroll);
        int last = PageAt(m_scroll + client.bottom);
        if (page->Index < first - 3 || page->Index > last + 3)
        {
            return; // scrolled away meanwhile
        }

        Bitmap image;
        image.Width = page->Width;
        image.Height = page->Height;
        image.Pixels = std::move(page->Pixels);
        CachedPage cached;
        cached.Bitmap = CreateBitmap(image, true);
        cached.Width = page->Width;
        cached.Height = page->Height;
        if (cached.Bitmap)
        {
            m_pages[page->Index] = std::move(cached);
            InvalidateRect(m_hwnd, nullptr, FALSE);
        }
    }

    void PreviewWindow::DrawPdf()
    {
        if (m_pageTops.empty())
        {
            LayoutPdf();
            if (m_pageTops.empty())
            {
                return;
            }
        }

        RECT client{};
        GetClientRect(m_hwnd, &client);
        int first = PageAt(m_scroll);
        int last = PageAt(m_scroll + client.bottom);
        ComPtr<ID2D1SolidColorBrush> paper;
        ComPtr<ID2D1SolidColorBrush> edge;
        m_target->CreateSolidColorBrush(D2D1::ColorF(1, 1, 1, 1), &paper);
        m_target->CreateSolidColorBrush(Color(Mix(m_colors.Background, m_colors.Text, 0.25)), &edge);
        for (int i = first; i <= last; i++)
        {
            D2D1_SIZE_F size = m_pageSizes[static_cast<size_t>(i)];
            float left = std::floor((client.right - size.width) / 2);
            float top = static_cast<float>(std::floor(m_pageTops[static_cast<size_t>(i)] - m_scroll));
            D2D1_RECT_F rect = D2D1::RectF(left, top, left + size.width, top + size.height);
            m_target->FillRectangle(rect, paper.Get());
            auto cached = m_pages.find(i);
            if (cached != m_pages.end())
            {
                // An older size is shown stretched until the page is drawn again at the new size.
                m_target->DrawBitmap(cached->second.Bitmap.Get(), rect, 1.0f, D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
            }

            m_target->DrawRectangle(D2D1::RectF(rect.left - 0.5f, rect.top - 0.5f, rect.right + 0.5f, rect.bottom + 0.5f), edge.Get(), 1.0f);
        }

        int current = PageAt(m_scroll + client.bottom / 3.0) + 1;
        std::wstring position = std::to_wstring(current) + L" / " + std::to_wstring(m_pageTops.size());
        DrawPill(position, static_cast<float>(client.right) - Scale(12), static_cast<float>(client.bottom) - Scale(12));
    }

    // ---------------------------------------------------------------- media

    void PreviewWindow::OnMediaEvent(DWORD event, HRESULT error)
    {
        if (!m_media)
        {
            return;
        }

        switch (event)
        {
        case MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA:
        {
            if (m_mediaReady)
            {
                break; // already handled when the player was adopted
            }

            m_mediaReady = true;
            m_loading = false;
            KillTimer(m_hwnd, TimerLoading);
            LayoutMedia();
            DWORD width = 0;
            DWORD height = 0;
            m_media->NativeSize(width, height);
            Trace("preview-loaded", std::string("\"kind\":\"media\",\"video\":") + (m_media->HasVideo() ? "true" : "false") +
                ",\"audio\":" + (m_media->HasAudio() ? "true" : "false") + ",\"duration\":" + std::to_string(m_media->Duration()) +
                ",\"width\":" + std::to_string(width) + ",\"height\":" + std::to_string(height) + ",\"ms\":" + std::to_string(Milliseconds(m_started, Now())));
            break;
        }
        case MF_MEDIA_ENGINE_EVENT_ERROR:
        {
            HRESULT extended = m_media->Error();
            ShutdownInBackground(std::move(m_media), nullptr);
            if (m_video != nullptr)
            {
                ShowWindow(m_video, SW_HIDE);
            }

            ShowFailure(m_mediaReady ? IDS_MEDIA_FAILED : IDS_CANNOT_PREVIEW, "media-error " + HResultText(FAILED(extended) ? extended : error));
            return;
        }
        case MF_MEDIA_ENGINE_EVENT_PLAYING:
            SetTimer(m_hwnd, TimerMedia, 250, nullptr);
            Trace("preview-media", "\"state\":\"playing\"");
            break;
        case MF_MEDIA_ENGINE_EVENT_PAUSE:
        case MF_MEDIA_ENGINE_EVENT_ENDED:
            KillTimer(m_hwnd, TimerMedia);
            Trace("preview-media", std::string("\"state\":\"") + (event == MF_MEDIA_ENGINE_EVENT_ENDED ? "ended" : "paused") + "\"");
            break;
        default:
            break;
        }

        InvalidateRect(m_hwnd, nullptr, FALSE);
    }

    void PreviewWindow::LayoutMedia()
    {
        RECT client{};
        GetClientRect(m_hwnd, &client);
        int pad = static_cast<int>(Scale(8));
        int bar = static_cast<int>(Scale(44));
        int button = static_cast<int>(Scale(36));
        int barTop = std::max(0L, client.bottom - bar);
        int buttonTop = barTop + (bar - button) / 2;
        m_playRect = { pad, buttonTop, pad + button, buttonTop + button };
        m_muteRect = { std::max(pad, static_cast<int>(client.right) - pad - button), buttonTop, std::max(pad, static_cast<int>(client.right) - pad), buttonTop + button };
        int seekLeft = m_playRect.right + static_cast<int>(Scale(96));
        m_seekRect = { seekLeft, buttonTop, std::max(seekLeft, static_cast<int>(m_muteRect.left - pad)), buttonTop + button };

        if (m_video == nullptr || !m_media)
        {
            return;
        }

        DWORD width = 0;
        DWORD height = 0;
        if (m_mediaReady && m_media->HasVideo() && m_media->NativeSize(width, height))
        {
            // Never enlarged (the same rule as images and Mavue's player), centered above the controls.
            double fittedWidth = 0;
            double fittedHeight = 0;
            FitWithin(width, height, std::max(0L, client.right - 2 * pad), std::max(0, barTop - 2 * pad), fittedWidth, fittedHeight);
            int w = static_cast<int>(std::min<double>(fittedWidth, width));
            int h = static_cast<int>(std::min<double>(fittedHeight, height));
            int x = (client.right - w) / 2;
            int y = pad + (std::max(0, barTop - 2 * pad) - h) / 2;
            SetWindowPos(m_video, nullptr, x, y, std::max(1, w), std::max(1, h), SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            m_media->UpdateVideoRect(RECT{ 0, 0, std::max(1, w), std::max(1, h) });
        }
        else
        {
            ShowWindow(m_video, SW_HIDE);
        }
    }

    void PreviewWindow::DrawMedia()
    {
        if (!m_media || !m_textFormat || !m_iconFormat)
        {
            return;
        }

        RECT client{};
        GetClientRect(m_hwnd, &client);
        ComPtr<ID2D1SolidColorBrush> ink;
        ComPtr<ID2D1SolidColorBrush> track;
        ComPtr<ID2D1SolidColorBrush> accent;
        m_target->CreateSolidColorBrush(Color(m_colors.Text), &ink);
        m_target->CreateSolidColorBrush(Color(Mix(m_colors.Background, m_colors.Text, 0.25)), &track);
        m_target->CreateSolidColorBrush(Color(GetSysColor(COLOR_HIGHLIGHT)), &accent);

        if (m_mediaReady && !m_media->HasVideo())
        {
            // Audio: a note and the file name.
            float barTop = static_cast<float>(m_playRect.top) - Scale(8);
            D2D1_RECT_F area = D2D1::RectF(0, 0, static_cast<float>(client.right), std::max(0.0f, barTop));
            ComPtr<IDWriteTextFormat> large;
            if (SUCCEEDED(m_writeFactory->CreateTextFormat(L"Segoe Fluent Icons", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL,
                DWRITE_FONT_STRETCH_NORMAL, Scale(56), L"", &large)))
            {
                large->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_CENTER);
                large->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
                D2D1_RECT_F note = D2D1::RectF(area.left, area.top, area.right, (area.top + area.bottom) / 2 + Scale(20));
                m_target->DrawTextW(L"\xE8D6", 1, large.Get(), note, ink.Get());
            }

            D2D1_RECT_F title = D2D1::RectF(Scale(12), (area.top + area.bottom) / 2 + Scale(28), area.right - Scale(12), (area.top + area.bottom) / 2 + Scale(64));
            m_target->DrawTextW(m_mediaName.c_str(), static_cast<UINT32>(m_mediaName.size()), m_textFormat.Get(), title, ink.Get());
        }

        // Controls: play/pause, time, seek bar, mute.
        auto rectF = [](const RECT& r) { return D2D1::RectF(static_cast<float>(r.left), static_cast<float>(r.top), static_cast<float>(r.right), static_cast<float>(r.bottom)); };
        const wchar_t* playGlyph = m_media->IsPaused() ? L"\xE768" : L"\xE769";
        m_target->DrawTextW(playGlyph, 1, m_iconFormat.Get(), rectF(m_playRect), ink.Get());
        const wchar_t* muteGlyph = m_media->IsMuted() ? L"\xE74F" : L"\xE767";
        m_target->DrawTextW(muteGlyph, 1, m_iconFormat.Get(), rectF(m_muteRect), ink.Get());

        double duration = m_media->Duration();
        double position = m_media->Position();
        std::wstring time = FormatTime(position) + L" / " + FormatTime(duration);
        D2D1_RECT_F timeRect = D2D1::RectF(static_cast<float>(m_playRect.right), static_cast<float>(m_playRect.top), static_cast<float>(m_seekRect.left), static_cast<float>(m_playRect.bottom));
        m_target->DrawTextW(time.c_str(), static_cast<UINT32>(time.size()), m_textFormat.Get(), timeRect, ink.Get());

        if (m_seekRect.right - m_seekRect.left > Scale(20))
        {
            float middle = (m_seekRect.top + m_seekRect.bottom) / 2.0f;
            float thickness = Scale(4);
            D2D1_ROUNDED_RECT whole{ D2D1::RectF(static_cast<float>(m_seekRect.left), middle - thickness / 2, static_cast<float>(m_seekRect.right), middle + thickness / 2), thickness / 2, thickness / 2 };
            m_target->FillRoundedRectangle(whole, track.Get());
            if (duration > 0)
            {
                float done = static_cast<float>(std::clamp(position / duration, 0.0, 1.0)) * (m_seekRect.right - m_seekRect.left);
                D2D1_ROUNDED_RECT played{ D2D1::RectF(static_cast<float>(m_seekRect.left), middle - thickness / 2, m_seekRect.left + done, middle + thickness / 2), thickness / 2, thickness / 2 };
                m_target->FillRoundedRectangle(played, accent.Get());
                m_target->FillEllipse(D2D1::Ellipse(D2D1::Point2F(m_seekRect.left + done, middle), Scale(6), Scale(6)), accent.Get());
            }
        }
    }

    // ---------------------------------------------------------------- content

    void PreviewWindow::OnLoaded(std::unique_ptr<LoadResult> result)
    {
        KillTimer(m_hwnd, TimerLoading);
        if (TraceEnabled() && !result->Media)
        {
            Trace("preview-loaded", std::string("\"kind\":\"") + KindName(result->Kind, false) + "\",\"ms\":" + std::to_string(result->Ms) +
                ",\"sourceWidth\":" + std::to_string(result->SourceWidth) + ",\"sourceHeight\":" + std::to_string(result->SourceHeight) +
                ",\"decodedWidth\":" + std::to_string(result->Image.Width) + ",\"decodedHeight\":" + std::to_string(result->Image.Height) +
                ",\"frames\":" + std::to_string(result->Frames.size()) + ",\"pages\":" + std::to_string(result->Pages.size()) +
                ",\"message\":" + std::to_string(result->Message));
        }

        bool media = result->Media;
        UINT message = result->Message;
        std::string reason = result->Reason;
        m_content = std::move(result);
        if (message != 0)
        {
            ShowFailure(message, reason);
            return;
        }

        if (media)
        {
            // Created here (the engine wants its window's thread; creating it does not wait in COM). Only its
            // shutdown, which does, runs elsewhere (ShutdownInBackground).
            m_mediaName = m_content->MediaName;
            HRESULT hr = m_video != nullptr
                ? MediaPlayer::Create(m_video, m_hwnd, WM_PREVIEW_MEDIA, m_content->MediaPath, m_stream.Get(), m_content->MediaName, m_media)
                : E_FAIL;
            Trace("preview-media-start", std::string("\"byPath\":") + (m_content->ByPath ? "true" : "false") + ",\"hr\":\"" + HResultText(hr) + "\"");
            if (FAILED(hr) || !m_media)
            {
                ShutdownInBackground(std::move(m_media), nullptr);
                ShowFailure(IDS_CANNOT_PREVIEW, "media " + HResultText(hr));
                return;
            }

            m_loading = true; // until the media engine has read the file
            LayoutMedia();
            // Events posted before the player was adopted were dropped: catch up from the engine's state.
            if (m_media->HasMetadata())
            {
                OnMediaEvent(MF_MEDIA_ENGINE_EVENT_LOADEDMETADATA, S_OK);
            }
            else if (FAILED(m_media->Error()))
            {
                OnMediaEvent(MF_MEDIA_ENGINE_EVENT_ERROR, S_OK);
                return;
            }

            InvalidateRect(m_hwnd, nullptr, FALSE);
            return;
        }

        m_loading = false;
        if (m_content->Kind == ContentKind::Image && !m_content->Frames.empty())
        {
            m_frame = 0;
            SetTimer(m_hwnd, TimerFrame, m_content->Frames.front().DelayMs, nullptr);
        }
        else if (m_content->Kind == ContentKind::Pdf)
        {
            LayoutPdf();
        }

        InvalidateRect(m_hwnd, nullptr, FALSE);
    }

    void PreviewWindow::AdvanceFrame()
    {
        if (!m_content || m_content->Frames.size() < 2)
        {
            KillTimer(m_hwnd, TimerFrame);
            return;
        }

        m_frame = (m_frame + 1) % m_content->Frames.size();
        const AnimationFrame& frame = m_content->Frames[m_frame];
        if (m_imageBitmap)
        {
            D2D1_RECT_U all = D2D1::RectU(0, 0, frame.Image.Width, frame.Image.Height);
            m_imageBitmap->CopyFromMemory(&all, frame.Image.Pixels.data(), frame.Image.Width * 4);
        }

        SetTimer(m_hwnd, TimerFrame, frame.DelayMs, nullptr);
        if (TraceEnabled() && m_frame == 1)
        {
            Trace("preview-frame", "\"frame\":1,\"frames\":" + std::to_string(m_content->Frames.size()));
        }

        InvalidateRect(m_hwnd, nullptr, FALSE);
    }

    void PreviewWindow::ShowFailure(UINT messageId, const std::string& reason)
    {
        m_loading = false;
        m_message = Text(messageId);
        if (m_hwnd != nullptr)
        {
            KillTimer(m_hwnd, TimerLoading);
            KillTimer(m_hwnd, TimerFrame);
            SetWindowTextW(m_hwnd, m_message.c_str()); // the accessible name
            InvalidateRect(m_hwnd, nullptr, FALSE);
        }

        Trace("preview-failed", "\"message\":" + std::to_string(messageId) + ",\"reason\":\"" + reason + "\",\"ms\":" + std::to_string(Milliseconds(m_started, Now())));
    }

    // ---------------------------------------------------------------- input

    void PreviewWindow::OnWheel(int delta)
    {
        if (m_pageTops.empty())
        {
            return;
        }

        UINT lines = 3;
        SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, &lines, 0);
        double step = (lines == WHEEL_PAGESCROLL ? 10.0 : lines) * Scale(20);
        ScrollTo(m_scroll - delta / static_cast<double>(WHEEL_DELTA) * step);
    }

    void PreviewWindow::OnClick(int x, int y)
    {
        if (!m_media)
        {
            return;
        }

        if (Contains(m_playRect, x, y))
        {
            m_media->TogglePlay();
        }
        else if (Contains(m_muteRect, x, y))
        {
            m_media->SetMuted(!m_media->IsMuted());
        }
        else if (Contains(m_seekRect, x, y) && m_media->Duration() > 0 && m_seekRect.right > m_seekRect.left)
        {
            double fraction = static_cast<double>(x - m_seekRect.left) / (m_seekRect.right - m_seekRect.left);
            m_media->Seek(std::clamp(fraction, 0.0, 1.0) * m_media->Duration());
        }
        else if (m_mediaReady && !m_media->HasVideo())
        {
            m_media->TogglePlay(); // anywhere on an audio file
        }

        InvalidateRect(m_hwnd, nullptr, FALSE);
    }

    void PreviewWindow::OnKey(WPARAM key)
    {
        if (m_media)
        {
            switch (key)
            {
            case VK_SPACE: m_media->TogglePlay(); break;
            case VK_LEFT: m_media->Seek(std::max(0.0, m_media->Position() - 5)); break;
            case VK_RIGHT: m_media->Seek(m_media->Position() + 5); break;
            default: return;
            }

            InvalidateRect(m_hwnd, nullptr, FALSE);
            return;
        }

        if (m_pageTops.empty())
        {
            return;
        }

        RECT client{};
        GetClientRect(m_hwnd, &client);
        double line = Scale(40);
        switch (key)
        {
        case VK_UP: ScrollTo(m_scroll - line); break;
        case VK_DOWN: ScrollTo(m_scroll + line); break;
        case VK_PRIOR: ScrollTo(m_scroll - std::max<double>(line, client.bottom - line)); break;
        case VK_NEXT:
        case VK_SPACE: ScrollTo(m_scroll + std::max<double>(line, client.bottom - line)); break;
        case VK_HOME: ScrollTo(0); break;
        case VK_END: ScrollTo(m_contentHeight); break;
        default: break;
        }
    }
}
