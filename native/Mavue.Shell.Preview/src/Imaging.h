#pragma once

#include "Module.h"

#include <d2d1_3.h>
#include <wincodec.h>

#include <atomic>
#include <vector>

namespace mavue
{
    enum class ContentKind
    {
        Unknown, // not an image, SVG or PDF (the preview then tries media playback)
        Image,   // a WIC decoder accepts it (still image, or GIF animation)
        Svg,
        Pdf,
    };

    // Premultiplied BGRA, top-down, stride Width * 4.
    struct Bitmap
    {
        UINT Width = 0;
        UINT Height = 0;
        std::vector<BYTE> Pixels;
    };

    struct AnimationFrame
    {
        Bitmap Image;    // the whole canvas after this frame
        UINT DelayMs = 0;
    };

    // Images larger than this are not decoded (a fit-size decode of anything bigger is too slow for Explorer).
    inline constexpr ULONGLONG MaxSourcePixels = 400ull * 1000 * 1000;

    // Looks at the content (not the extension): "%PDF-", an <svg> root, or a WIC decoder. Rewinds the stream.
    ContentKind Sniff(IStream* stream, IWICImagingFactory* factory);

    ComPtr<IWICImagingFactory> CreateWicFactory();

    // Decodes the first frame to fit within maxWidth x maxHeight (never enlarged), with the EXIF/XMP orientation
    // applied (System.Photo.Orientation). sourceWidth/Height are the oriented full size.
    HRESULT DecodeImage(IWICImagingFactory* factory, IStream* stream, UINT maxWidth, UINT maxHeight, Bitmap& image,
        UINT& sourceWidth, UINT& sourceHeight, GUID& container);

    // All frames of an animated GIF, composed on the logical screen (disposal methods applied). S_FALSE when the
    // GIF has one frame or the frames would need more than budgetBytes (then show the first frame still).
    HRESULT DecodeGifFrames(IWICImagingFactory* factory, IStream* stream, ULONGLONG budgetBytes,
        std::vector<AnimationFrame>& frames, const std::atomic<bool>* cancel);

    // Whole stream into memory (SVG). E_OUTOFMEMORY over maxBytes.
    HRESULT ReadAll(IStream* stream, ULONGLONG maxBytes, std::vector<BYTE>& bytes);

    // SVG drawn by Direct2D (ID2D1SvgDocument: no scripts, no external resources). `intrinsic` is the size from
    // width/height/viewBox (300 x 150 when none, as in browsers).
    HRESULT CreateSvg(ID2D1DeviceContext5* context, const std::vector<BYTE>& bytes, ComPtr<ID2D1SvgDocument>& document, D2D1_SIZE_F& intrinsic);

    // Scales the document to fill `size` (aspect ratio kept by the viewBox, centered).
    void FitSvg(ID2D1SvgDocument* document, D2D1_SIZE_F intrinsic, D2D1_SIZE_F size);

    // SVG rendered to fit within maxSide x maxSide (enlarged if small: it is a vector image).
    HRESULT RenderSvg(const std::vector<BYTE>& bytes, UINT maxSide, Bitmap& image);

    // Largest size of `width x height` inside `boxWidth x boxHeight` keeping the aspect ratio.
    void FitWithin(double width, double height, double boxWidth, double boxHeight, double& fittedWidth, double& fittedHeight);
}
