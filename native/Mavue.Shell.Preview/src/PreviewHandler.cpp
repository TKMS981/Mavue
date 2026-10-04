#include "PreviewHandler.h"

namespace mavue
{
    PreviewHandler::PreviewHandler() noexcept
    {
        ModuleAddRef();
        // Until the host sends its colors (IPreviewHandlerVisuals): the system window colors.
        m_colors.Background = GetSysColor(COLOR_WINDOW);
        m_colors.Text = GetSysColor(COLOR_WINDOWTEXT);
    }

    PreviewHandler::~PreviewHandler()
    {
        m_window.reset();
        ModuleRelease();
    }

    IFACEMETHODIMP PreviewHandler::QueryInterface(REFIID riid, void** object)
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == IID_IPreviewHandler)
        {
            *object = static_cast<IPreviewHandler*>(this);
        }
        else if (riid == IID_IInitializeWithStream)
        {
            *object = static_cast<IInitializeWithStream*>(this);
        }
        else if (riid == IID_IObjectWithSite)
        {
            *object = static_cast<IObjectWithSite*>(this);
        }
        else if (riid == IID_IOleWindow)
        {
            *object = static_cast<IOleWindow*>(this);
        }
        else if (riid == IID_IPreviewHandlerVisuals)
        {
            *object = static_cast<IPreviewHandlerVisuals*>(this);
        }
        else
        {
            *object = nullptr;
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) PreviewHandler::AddRef() { return InterlockedIncrement(&m_refs); }

    IFACEMETHODIMP_(ULONG) PreviewHandler::Release()
    {
        ULONG refs = InterlockedDecrement(&m_refs);
        if (refs == 0)
        {
            delete this;
        }

        return refs;
    }

    IFACEMETHODIMP PreviewHandler::Initialize(IStream* stream, DWORD)
    {
        if (stream == nullptr)
        {
            return E_INVALIDARG;
        }

        if (m_stream)
        {
            return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
        }

        m_stream = stream;
        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::SetWindow(HWND parent, const RECT* rect)
    {
        if (parent == nullptr || rect == nullptr)
        {
            return E_INVALIDARG;
        }

        m_parent = parent;
        m_rect = *rect;
        if (m_window)
        {
            m_window->SetParent(parent, *rect);
        }

        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::SetRect(const RECT* rect)
    {
        if (rect == nullptr)
        {
            return E_INVALIDARG;
        }

        m_rect = *rect;
        if (m_window)
        {
            m_window->SetRect(*rect);
        }

        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::DoPreview()
    {
        if (m_window)
        {
            return E_UNEXPECTED; // called once per item
        }

        if (!m_stream || m_parent == nullptr)
        {
            return E_FAIL;
        }

        m_window = PreviewWindow::Create(m_parent, m_rect, m_colors, m_stream.Get());
        return m_window ? S_OK : E_FAIL;
    }

    IFACEMETHODIMP PreviewHandler::Unload()
    {
        m_window.reset(); // stops the background work without waiting for it
        m_stream.Reset();
        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::SetFocus()
    {
        if (m_window)
        {
            m_window->Focus();
        }

        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::QueryFocus(HWND* focus)
    {
        if (focus == nullptr)
        {
            return E_INVALIDARG;
        }

        *focus = ::GetFocus();
        return *focus != nullptr ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    IFACEMETHODIMP PreviewHandler::TranslateAccelerator(MSG* message)
    {
        // Keys the frame (Explorer) uses itself, such as Tab to leave the pane.
        return m_frame ? m_frame->TranslateAccelerator(message) : S_FALSE;
    }

    IFACEMETHODIMP PreviewHandler::SetSite(IUnknown* site)
    {
        m_site = site;
        m_frame.Reset();
        if (site != nullptr)
        {
            site->QueryInterface(IID_PPV_ARGS(&m_frame));
        }

        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::GetSite(REFIID riid, void** site)
    {
        if (site == nullptr)
        {
            return E_POINTER;
        }

        *site = nullptr;
        return m_site ? m_site->QueryInterface(riid, site) : E_FAIL;
    }

    IFACEMETHODIMP PreviewHandler::GetWindow(HWND* window)
    {
        if (window == nullptr)
        {
            return E_INVALIDARG;
        }

        *window = m_window ? m_window->Handle() : m_parent;
        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::ContextSensitiveHelp(BOOL) { return E_NOTIMPL; }

    IFACEMETHODIMP PreviewHandler::SetBackgroundColor(COLORREF color)
    {
        Trace("preview-visuals", "\"background\":" + std::to_string(color));
        m_colors.Background = color;
        if (m_window)
        {
            m_window->SetColors(m_colors);
        }

        return S_OK;
    }

    IFACEMETHODIMP PreviewHandler::SetFont(const LOGFONTW*) { return S_OK; }

    IFACEMETHODIMP PreviewHandler::SetTextColor(COLORREF color)
    {
        Trace("preview-visuals", "\"text\":" + std::to_string(color));
        m_colors.Text = color;
        if (m_window)
        {
            m_window->SetColors(m_colors);
        }

        return S_OK;
    }
}
