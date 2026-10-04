// Images for previews and thumbnails: Windows Imaging Component (the same decoders Mavue uses, including Store codec
// extensions such as HEIF/AVIF/WebP/RAW) and Direct2D for SVG. Same rules as Mavue.Image: EXIF/XMP orientation
// applied, never enlarged (bitmaps), GIF delays under 20 ms shown as 100 ms (GifComposer).

#include "Imaging.h"

#include <propvarutil.h>
#include <shlwapi.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <string_view>

namespace mavue
{
    namespace
    {
        constexpr UINT MinimumDelayMs = 20;
        constexpr UINT DefaultDelayMs = 100;

        HRESULT Rewind(IStream* stream)
        {
            LARGE_INTEGER zero{};
            return stream->Seek(zero, STREAM_SEEK_SET, nullptr);
        }

        USHORT ReadUShort(IWICMetadataQueryReader* reader, const wchar_t* name, USHORT fallback)
        {
            PROPVARIANT value;
            PropVariantInit(&value);
            USHORT result = fallback;
            if (SUCCEEDED(reader->GetMetadataByName(name, &value)))
            {
                if (value.vt == VT_UI2)
                {
                    result = value.uiVal;
                }
                else if (value.vt == VT_UI1)
                {
                    result = value.bVal;
                }
            }

            PropVariantClear(&value);
            return result;
        }

        // EXIF orientation 1-8 (System.Photo.Orientation), 1 when absent.
        USHORT ReadOrientation(IWICBitmapFrameDecode* frame)
        {
            ComPtr<IWICMetadataQueryReader> reader;
            if (FAILED(frame->GetMetadataQueryReader(&reader)))
            {
                return 1;
            }

            USHORT orientation = ReadUShort(reader.Get(), L"System.Photo.Orientation", 1);
            return orientation >= 1 && orientation <= 8 ? orientation : 1;
        }

        WICBitmapTransformOptions TransformFor(USHORT orientation)
        {
            switch (orientation)
            {
            case 2: return WICBitmapTransformFlipHorizontal;
            case 3: return WICBitmapTransformRotate180;
            case 4: return WICBitmapTransformFlipVertical;
            case 5: return static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate90 | WICBitmapTransformFlipHorizontal);
            case 6: return WICBitmapTransformRotate90;
            case 7: return static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate270 | WICBitmapTransformFlipHorizontal);
            case 8: return WICBitmapTransformRotate270;
            default: return WICBitmapTransformRotate0;
            }
        }

        HRESULT ToPbgra(IWICImagingFactory* factory, IWICBitmapSource* source, ComPtr<IWICBitmapSource>& converted)
        {
            ComPtr<IWICFormatConverter> converter;
            HRESULT hr = factory->CreateFormatConverter(&converter);
            if (SUCCEEDED(hr))
            {
                hr = converter->Initialize(source, GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeMedianCut);
            }

            if (SUCCEEDED(hr))
            {
                converted = converter;
            }

            return hr;
        }

        HRESULT CopyAll(IWICBitmapSource* source, Bitmap& image)
        {
            UINT width = 0;
            UINT height = 0;
            HRESULT hr = source->GetSize(&width, &height);
            if (FAILED(hr))
            {
                return hr;
            }

            if (width == 0 || height == 0 || static_cast<ULONGLONG>(width) * height > 128ull * 1024 * 1024)
            {
                return WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
            }

            try
            {
                image.Pixels.assign(static_cast<size_t>(width) * height * 4, 0);
            }
            catch (const std::bad_alloc&)
            {
                return E_OUTOFMEMORY;
            }

            image.Width = width;
            image.Height = height;
            return source->CopyPixels(nullptr, width * 4, static_cast<UINT>(image.Pixels.size()), image.Pixels.data());
        }

        bool StartsWith(const BYTE* data, size_t size, std::string_view prefix)
        {
            return size >= prefix.size() && std::memcmp(data, prefix.data(), prefix.size()) == 0;
        }

        bool LooksLikeSvg(const BYTE* data, size_t size)
        {
            size_t start = StartsWith(data, size, "\xEF\xBB\xBF") ? 3 : 0;
            while (start < size && (data[start] == ' ' || data[start] == '\t' || data[start] == '\r' || data[start] == '\n'))
            {
                start++;
            }

            if (start >= size || data[start] != '<')
            {
                return false; // text before the first tag: not XML
            }

            std::string_view text(reinterpret_cast<const char*>(data + start), size - start);
            if (text.find('\0') != std::string_view::npos)
            {
                return false;
            }

            return text.find("<svg") != std::string_view::npos;
        }

        HRESULT ParseSvg(ID2D1DeviceContext5* context, const std::vector<BYTE>& bytes, D2D1_SIZE_F viewport, ComPtr<ID2D1SvgDocument>& document)
        {
            if (bytes.empty() || bytes.size() > UINT_MAX)
            {
                return E_INVALIDARG;
            }

            ComPtr<IStream> memory;
            memory.Attach(SHCreateMemStream(bytes.data(), static_cast<UINT>(bytes.size())));
            if (!memory)
            {
                return E_OUTOFMEMORY;
            }

            return context->CreateSvgDocument(memory.Get(), viewport, &document);
        }
    }

    ComPtr<IWICImagingFactory> CreateWicFactory()
    {
        ComPtr<IWICImagingFactory> factory;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))))
        {
            return nullptr;
        }

        return factory;
    }

    ContentKind Sniff(IStream* stream, IWICImagingFactory* factory)
    {
        if (stream == nullptr || FAILED(Rewind(stream)))
        {
            return ContentKind::Unknown;
        }

        BYTE head[4096]{};
        ULONG total = 0;
        while (total < sizeof(head))
        {
            ULONG read = 0;
            if (FAILED(stream->Read(head + total, sizeof(head) - total, &read)) || read == 0)
            {
                break;
            }

            total += read;
        }

        Rewind(stream);
        std::string_view start(reinterpret_cast<const char*>(head), std::min<size_t>(total, 1024));
        if (start.find("%PDF-") != std::string_view::npos)
        {
            return ContentKind::Pdf;
        }

        if (LooksLikeSvg(head, total))
        {
            return ContentKind::Svg;
        }

        if (factory != nullptr && total > 0)
        {
            ComPtr<IWICBitmapDecoder> decoder;
            HRESULT hr = factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
            Rewind(stream);
            if (SUCCEEDED(hr))
            {
                return ContentKind::Image;
            }
        }

        return ContentKind::Unknown;
    }

    void FitWithin(double width, double height, double boxWidth, double boxHeight, double& fittedWidth, double& fittedHeight)
    {
        if (width <= 0 || height <= 0 || boxWidth <= 0 || boxHeight <= 0)
        {
            fittedWidth = 0;
            fittedHeight = 0;
            return;
        }

        double scale = std::min(boxWidth / width, boxHeight / height);
        fittedWidth = width * scale;
        fittedHeight = height * scale;
    }

    HRESULT DecodeImage(IWICImagingFactory* factory, IStream* stream, UINT maxWidth, UINT maxHeight, Bitmap& image,
        UINT& sourceWidth, UINT& sourceHeight, GUID& container)
    {
        image = {};
        sourceWidth = sourceHeight = 0;
        container = GUID_NULL;
        HRESULT hr = Rewind(stream);
        ComPtr<IWICBitmapDecoder> decoder;
        if (SUCCEEDED(hr))
        {
            hr = factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
        }

        ComPtr<IWICBitmapFrameDecode> frame;
        if (SUCCEEDED(hr))
        {
            decoder->GetContainerFormat(&container);
            hr = decoder->GetFrame(0, &frame);
        }

        UINT width = 0;
        UINT height = 0;
        if (SUCCEEDED(hr))
        {
            hr = frame->GetSize(&width, &height);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        if (width == 0 || height == 0 || static_cast<ULONGLONG>(width) * height > MaxSourcePixels)
        {
            return WINCODEC_ERR_IMAGESIZEOUTOFRANGE;
        }

        USHORT orientation = ReadOrientation(frame.Get());
        bool swap = orientation >= 5;
        sourceWidth = swap ? height : width;
        sourceHeight = swap ? width : height;

        double scale = std::min({ 1.0, static_cast<double>(maxWidth) / sourceWidth, static_cast<double>(maxHeight) / sourceHeight });
        UINT targetWidth = std::max(1u, static_cast<UINT>(std::lround(sourceWidth * scale)));
        UINT targetHeight = std::max(1u, static_cast<UINT>(std::lround(sourceHeight * scale)));
        UINT scaledWidth = swap ? targetHeight : targetWidth;
        UINT scaledHeight = swap ? targetWidth : targetHeight;

        ComPtr<IWICBitmapSource> source = frame;
        if (scaledWidth != width || scaledHeight != height)
        {
            // The scaler uses the decoder's own downscaling (IWICBitmapSourceTransform, e.g. JPEG DCT scaling) when it has one.
            ComPtr<IWICBitmapScaler> scaler;
            hr = factory->CreateBitmapScaler(&scaler);
            if (SUCCEEDED(hr))
            {
                hr = scaler->Initialize(frame.Get(), scaledWidth, scaledHeight, WICBitmapInterpolationModeHighQualityCubic);
            }

            if (FAILED(hr))
            {
                return hr;
            }

            source = scaler;
        }

        ComPtr<IWICBitmapSource> converted;
        hr = ToPbgra(factory, source.Get(), converted);
        if (FAILED(hr))
        {
            return hr;
        }

        if (orientation != 1)
        {
            ComPtr<IWICBitmapFlipRotator> rotator;
            hr = factory->CreateBitmapFlipRotator(&rotator);
            if (SUCCEEDED(hr))
            {
                hr = rotator->Initialize(converted.Get(), TransformFor(orientation));
            }

            if (FAILED(hr))
            {
                return hr;
            }

            converted = rotator;
        }

        return CopyAll(converted.Get(), image);
    }

    HRESULT DecodeGifFrames(IWICImagingFactory* factory, IStream* stream, ULONGLONG budgetBytes,
        std::vector<AnimationFrame>& frames, const std::atomic<bool>* cancel)
    {
        frames.clear();
        HRESULT hr = Rewind(stream);
        ComPtr<IWICBitmapDecoder> decoder;
        if (SUCCEEDED(hr))
        {
            hr = factory->CreateDecoderFromStream(stream, nullptr, WICDecodeMetadataCacheOnDemand, &decoder);
        }

        GUID container{};
        UINT count = 0;
        if (SUCCEEDED(hr))
        {
            decoder->GetContainerFormat(&container);
            hr = decoder->GetFrameCount(&count);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        if (container != GUID_ContainerFormatGif || count < 2)
        {
            return S_FALSE;
        }

        UINT canvasWidth = 0;
        UINT canvasHeight = 0;
        ComPtr<IWICMetadataQueryReader> globalReader;
        if (SUCCEEDED(decoder->GetMetadataQueryReader(&globalReader)))
        {
            canvasWidth = ReadUShort(globalReader.Get(), L"/logscrdesc/Width", 0);
            canvasHeight = ReadUShort(globalReader.Get(), L"/logscrdesc/Height", 0);
        }

        if (canvasWidth == 0 || canvasHeight == 0)
        {
            ComPtr<IWICBitmapFrameDecode> first;
            if (FAILED(decoder->GetFrame(0, &first)) || FAILED(first->GetSize(&canvasWidth, &canvasHeight)))
            {
                return S_FALSE;
            }
        }

        ULONGLONG frameBytes = static_cast<ULONGLONG>(canvasWidth) * canvasHeight * 4;
        if (frameBytes == 0 || frameBytes * count > budgetBytes)
        {
            return S_FALSE;
        }

        std::vector<BYTE> canvas(static_cast<size_t>(frameBytes), 0);
        std::vector<BYTE> saved;
        for (UINT index = 0; index < count; index++)
        {
            if (cancel != nullptr && cancel->load())
            {
                return E_ABORT;
            }

            ComPtr<IWICBitmapFrameDecode> frame;
            hr = decoder->GetFrame(index, &frame);
            if (FAILED(hr))
            {
                break; // a truncated GIF: show the frames read so far
            }

            UINT left = 0;
            UINT top = 0;
            UINT delay = 0;
            UINT disposal = 0;
            ComPtr<IWICMetadataQueryReader> reader;
            if (SUCCEEDED(frame->GetMetadataQueryReader(&reader)))
            {
                left = ReadUShort(reader.Get(), L"/imgdesc/Left", 0);
                top = ReadUShort(reader.Get(), L"/imgdesc/Top", 0);
                delay = ReadUShort(reader.Get(), L"/grctlext/Delay", 0);
                disposal = ReadUShort(reader.Get(), L"/grctlext/Disposal", 0);
            }

            ComPtr<IWICBitmapSource> converted;
            Bitmap pixels;
            if (FAILED(ToPbgra(factory, frame.Get(), converted)) || FAILED(CopyAll(converted.Get(), pixels)))
            {
                break;
            }

            if (disposal == 3)
            {
                saved = canvas;
            }

            UINT right = std::min(canvasWidth, left + pixels.Width);
            UINT bottom = std::min(canvasHeight, top + pixels.Height);
            for (UINT y = top; y < bottom; y++)
            {
                const BYTE* source = pixels.Pixels.data() + static_cast<size_t>(y - top) * pixels.Width * 4;
                BYTE* target = canvas.data() + (static_cast<size_t>(y) * canvasWidth + left) * 4;
                for (UINT x = left; x < right; x++, source += 4, target += 4)
                {
                    if (source[3] != 0)
                    {
                        std::memcpy(target, source, 4); // GIF transparency is all or nothing
                    }
                }
            }

            AnimationFrame composed;
            composed.Image.Width = canvasWidth;
            composed.Image.Height = canvasHeight;
            composed.Image.Pixels = canvas;
            composed.DelayMs = delay * 10 < MinimumDelayMs ? DefaultDelayMs : delay * 10;
            frames.push_back(std::move(composed));

            if (disposal == 2)
            {
                for (UINT y = top; y < bottom; y++)
                {
                    std::memset(canvas.data() + (static_cast<size_t>(y) * canvasWidth + left) * 4, 0, static_cast<size_t>(right - left) * 4);
                }
            }
            else if (disposal == 3 && !saved.empty())
            {
                canvas = saved;
            }
        }

        return frames.size() >= 2 ? S_OK : S_FALSE;
    }

    HRESULT ReadAll(IStream* stream, ULONGLONG maxBytes, std::vector<BYTE>& bytes)
    {
        bytes.clear();
        ULONGLONG size = StreamSize(stream);
        if (size == 0)
        {
            return E_FAIL;
        }

        if (size > maxBytes)
        {
            return E_OUTOFMEMORY;
        }

        HRESULT hr = Rewind(stream);
        if (FAILED(hr))
        {
            return hr;
        }

        bytes.resize(static_cast<size_t>(size));
        size_t total = 0;
        while (total < bytes.size())
        {
            ULONG read = 0;
            hr = stream->Read(bytes.data() + total, static_cast<ULONG>(std::min<size_t>(bytes.size() - total, 1u << 20)), &read);
            if (FAILED(hr) || read == 0)
            {
                break;
            }

            total += read;
        }

        bytes.resize(total);
        return total > 0 ? S_OK : E_FAIL;
    }

    HRESULT CreateSvg(ID2D1DeviceContext5* context, const std::vector<BYTE>& bytes, ComPtr<ID2D1SvgDocument>& document, D2D1_SIZE_F& intrinsic)
    {
        intrinsic = D2D1::SizeF(300, 150);
        HRESULT hr = ParseSvg(context, bytes, D2D1::SizeF(300, 150), document);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<ID2D1SvgElement> root;
        document->GetRoot(&root);
        if (!root)
        {
            return E_FAIL;
        }

        D2D1_SVG_VIEWBOX viewBox{};
        bool hasViewBox = root->IsAttributeSpecified(L"viewBox") &&
            SUCCEEDED(root->GetAttributeValue(L"viewBox", D2D1_SVG_ATTRIBUTE_POD_TYPE_VIEWBOX, &viewBox, sizeof(viewBox))) &&
            viewBox.width > 0 && viewBox.height > 0;
        auto length = [&](const wchar_t* name, float& value)
        {
            D2D1_SVG_LENGTH svgLength{};
            if (root->IsAttributeSpecified(name) &&
                SUCCEEDED(root->GetAttributeValue(name, D2D1_SVG_ATTRIBUTE_POD_TYPE_LENGTH, &svgLength, sizeof(svgLength))) &&
                svgLength.units == D2D1_SVG_LENGTH_UNITS_NUMBER && svgLength.value > 0)
            {
                value = svgLength.value;
                return true;
            }

            return false;
        };

        float width = 0;
        float height = 0;
        bool hasWidth = length(L"width", width);
        bool hasHeight = length(L"height", height);
        if (hasWidth && hasHeight)
        {
            intrinsic = D2D1::SizeF(width, height);
        }
        else if (hasViewBox)
        {
            float aspect = viewBox.height / viewBox.width;
            intrinsic = hasWidth ? D2D1::SizeF(width, width * aspect)
                : hasHeight ? D2D1::SizeF(height / aspect, height)
                : D2D1::SizeF(viewBox.width, viewBox.height);
        }

        intrinsic.width = std::clamp(intrinsic.width, 1.0f, 100000.0f);
        intrinsic.height = std::clamp(intrinsic.height, 1.0f, 100000.0f);
        return S_OK;
    }

    void FitSvg(ID2D1SvgDocument* document, D2D1_SIZE_F intrinsic, D2D1_SIZE_F size)
    {
        ComPtr<ID2D1SvgElement> root;
        document->GetRoot(&root);
        if (root)
        {
            if (!root->IsAttributeSpecified(L"viewBox"))
            {
                D2D1_SVG_VIEWBOX viewBox{ 0, 0, intrinsic.width, intrinsic.height };
                root->SetAttributeValue(L"viewBox", D2D1_SVG_ATTRIBUTE_POD_TYPE_VIEWBOX, &viewBox, sizeof(viewBox));
            }

            D2D1_SVG_LENGTH full{ 100, D2D1_SVG_LENGTH_UNITS_PERCENTAGE };
            root->SetAttributeValue(L"width", D2D1_SVG_ATTRIBUTE_POD_TYPE_LENGTH, &full, sizeof(full));
            root->SetAttributeValue(L"height", D2D1_SVG_ATTRIBUTE_POD_TYPE_LENGTH, &full, sizeof(full));
        }

        document->SetViewportSize(size);
    }

    HRESULT RenderSvg(const std::vector<BYTE>& bytes, UINT maxSide, Bitmap& image)
    {
        image = {};
        ComPtr<IWICImagingFactory> wic = CreateWicFactory();
        ComPtr<ID2D1Factory1> d2d;
        if (!wic || FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, __uuidof(ID2D1Factory1), nullptr, reinterpret_cast<void**>(d2d.GetAddressOf()))))
        {
            return E_FAIL;
        }

        auto target = [&](UINT width, UINT height, ComPtr<IWICBitmap>& bitmap, ComPtr<ID2D1RenderTarget>& renderTarget, ComPtr<ID2D1DeviceContext5>& context)
        {
            HRESULT hr = wic->CreateBitmap(width, height, GUID_WICPixelFormat32bppPBGRA, WICBitmapCacheOnLoad, &bitmap);
            if (SUCCEEDED(hr))
            {
                hr = d2d->CreateWicBitmapRenderTarget(bitmap.Get(),
                    D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_SOFTWARE, D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED), 96, 96),
                    &renderTarget);
            }

            if (SUCCEEDED(hr))
            {
                hr = renderTarget.As(&context); // SVG needs ID2D1DeviceContext5 (Windows 10 1703+)
            }

            return hr;
        };

        // First the intrinsic size (any target will do), then the document again on a target of the final size.
        ComPtr<IWICBitmap> probeBitmap;
        ComPtr<ID2D1RenderTarget> probeTarget;
        ComPtr<ID2D1DeviceContext5> probeContext;
        ComPtr<ID2D1SvgDocument> document;
        D2D1_SIZE_F intrinsic{};
        HRESULT hr = target(1, 1, probeBitmap, probeTarget, probeContext);
        if (SUCCEEDED(hr))
        {
            hr = CreateSvg(probeContext.Get(), bytes, document, intrinsic);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        double fittedWidth = 0;
        double fittedHeight = 0;
        FitWithin(intrinsic.width, intrinsic.height, maxSide, maxSide, fittedWidth, fittedHeight);
        UINT width = std::max(1u, static_cast<UINT>(std::lround(fittedWidth)));
        UINT height = std::max(1u, static_cast<UINT>(std::lround(fittedHeight)));

        ComPtr<IWICBitmap> bitmap;
        ComPtr<ID2D1RenderTarget> renderTarget;
        ComPtr<ID2D1DeviceContext5> context;
        hr = target(width, height, bitmap, renderTarget, context);
        if (SUCCEEDED(hr))
        {
            document.Reset();
            hr = ParseSvg(context.Get(), bytes, D2D1::SizeF(static_cast<float>(width), static_cast<float>(height)), document);
        }

        if (FAILED(hr))
        {
            return hr;
        }

        FitSvg(document.Get(), intrinsic, D2D1::SizeF(static_cast<float>(width), static_cast<float>(height)));
        context->BeginDraw();
        context->Clear(D2D1::ColorF(0, 0, 0, 0));
        context->DrawSvgDocument(document.Get());
        hr = context->EndDraw();
        if (FAILED(hr))
        {
            return hr;
        }

        return CopyAll(bitmap.Get(), image);
    }
}
