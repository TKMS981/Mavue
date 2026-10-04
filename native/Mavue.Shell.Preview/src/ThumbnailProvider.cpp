#include "ThumbnailProvider.h"

#include "Imaging.h"
#include "Pdfium.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace mavue
{
    namespace
    {
        constexpr ULONGLONG MaxSvgBytes = 32ull * 1024 * 1024;

        HRESULT PdfThumbnail(IStream* stream, UINT size, Bitmap& image)
        {
            std::unique_ptr<pdf::Document> document;
            switch (pdf::Document::Open(stream, document))
            {
            case pdf::OpenResult::Ok:
                break;
            case pdf::OpenResult::Unavailable:
                return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);
            default:
                return E_FAIL;
            }

            pdf::PageSize page = document->Size(0);
            double width = 0;
            double height = 0;
            FitWithin(page.Width, page.Height, size, size, width, height);
            image.Width = std::max(1u, static_cast<UINT>(std::lround(width)));
            image.Height = std::max(1u, static_cast<UINT>(std::lround(height)));
            return document->Render(0, static_cast<int>(image.Width), static_cast<int>(image.Height), image.Pixels, nullptr) ? S_OK : E_FAIL;
        }

        HRESULT ToHbitmap(const Bitmap& image, HBITMAP* bitmap)
        {
            BITMAPINFO info{};
            info.bmiHeader.biSize = sizeof(info.bmiHeader);
            info.bmiHeader.biWidth = static_cast<LONG>(image.Width);
            info.bmiHeader.biHeight = -static_cast<LONG>(image.Height); // top-down
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = BI_RGB;
            void* bits = nullptr;
            HBITMAP created = CreateDIBSection(nullptr, &info, DIB_RGB_COLORS, &bits, nullptr, 0);
            if (created == nullptr || bits == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            std::memcpy(bits, image.Pixels.data(), image.Pixels.size());
            *bitmap = created;
            return S_OK;
        }

        const char* KindName(ContentKind kind)
        {
            switch (kind)
            {
            case ContentKind::Image: return "image";
            case ContentKind::Svg: return "svg";
            case ContentKind::Pdf: return "pdf";
            default: return "unknown";
            }
        }
    }

    ThumbnailProvider::ThumbnailProvider() noexcept { ModuleAddRef(); }
    ThumbnailProvider::~ThumbnailProvider() { ModuleRelease(); }

    IFACEMETHODIMP ThumbnailProvider::QueryInterface(REFIID riid, void** object)
    {
        if (object == nullptr)
        {
            return E_POINTER;
        }

        if (riid == IID_IUnknown || riid == IID_IInitializeWithStream)
        {
            *object = static_cast<IInitializeWithStream*>(this);
        }
        else if (riid == IID_IThumbnailProvider)
        {
            *object = static_cast<IThumbnailProvider*>(this);
        }
        else
        {
            *object = nullptr;
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }

    IFACEMETHODIMP_(ULONG) ThumbnailProvider::AddRef() { return InterlockedIncrement(&m_refs); }

    IFACEMETHODIMP_(ULONG) ThumbnailProvider::Release()
    {
        ULONG refs = InterlockedDecrement(&m_refs);
        if (refs == 0)
        {
            delete this;
        }

        return refs;
    }

    IFACEMETHODIMP ThumbnailProvider::Initialize(IStream* stream, DWORD)
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

    IFACEMETHODIMP ThumbnailProvider::GetThumbnail(UINT size, HBITMAP* bitmap, WTS_ALPHATYPE* alpha)
    {
        if (bitmap == nullptr || alpha == nullptr)
        {
            return E_POINTER;
        }

        *bitmap = nullptr;
        *alpha = WTSAT_UNKNOWN;
        if (!m_stream || size == 0)
        {
            return E_UNEXPECTED;
        }

        size = std::min(size, 2560u);
        LONGLONG start = Now();
        ComPtr<IWICImagingFactory> wic = CreateWicFactory();
        ContentKind kind = Sniff(m_stream.Get(), wic.Get());
        Bitmap image;
        HRESULT hr = E_FAIL;
        try
        {
            switch (kind)
            {
            case ContentKind::Pdf:
                hr = PdfThumbnail(m_stream.Get(), size, image);
                break;
            case ContentKind::Svg:
            {
                std::vector<BYTE> bytes;
                hr = ReadAll(m_stream.Get(), MaxSvgBytes, bytes);
                if (SUCCEEDED(hr))
                {
                    hr = RenderSvg(bytes, size, image);
                }

                break;
            }
            case ContentKind::Image:
            {
                UINT sourceWidth = 0;
                UINT sourceHeight = 0;
                GUID container{};
                hr = wic ? DecodeImage(wic.Get(), m_stream.Get(), size, size, image, sourceWidth, sourceHeight, container) : E_FAIL;
                break;
            }
            default:
                hr = E_FAIL;
                break;
            }
        }
        catch (const std::bad_alloc&)
        {
            hr = E_OUTOFMEMORY;
        }

        if (SUCCEEDED(hr) && (image.Width == 0 || image.Height == 0 || image.Pixels.size() != static_cast<size_t>(image.Width) * image.Height * 4))
        {
            hr = E_FAIL;
        }

        if (SUCCEEDED(hr))
        {
            hr = ToHbitmap(image, bitmap);
        }

        if (SUCCEEDED(hr))
        {
            *alpha = WTSAT_ARGB;
        }

        if (TraceEnabled())
        {
            Trace("thumbnail", std::string("\"kind\":\"") + KindName(kind) + "\",\"size\":" + std::to_string(size) +
                ",\"width\":" + std::to_string(image.Width) + ",\"height\":" + std::to_string(image.Height) +
                ",\"hr\":\"" + HResultText(hr) + "\",\"ms\":" + std::to_string(Milliseconds(start, Now())));
        }

        return hr;
    }
}
