namespace Mavue.QuickView.Preview;

/// <summary>How Quick View sizes images (user setting).</summary>
public enum ImageScaleMode
{
    /// <summary>Default. Large images are reduced to fit the window; small images keep their own size (never enlarged).</summary>
    FitNoUpscale = 0,

    /// <summary>One image pixel per screen pixel; larger images can be scrolled.</summary>
    ActualSize,
}

/// <summary>Decode target for one preview.</summary>
/// <param name="Width">Pixels to decode horizontally.</param>
/// <param name="Height">Pixels to decode vertically.</param>
/// <param name="ActualSizeRefused">ActualSize was requested but the image exceeds the budget; fitted instead.</param>
public readonly record struct DecodeBox(uint Width, uint Height, bool ActualSizeRefused = false);

/// <summary>
/// Pure sizing rules for Quick View images (docs/QUICKVIEW-POC.md §10). Decoding to exactly the pixels
/// that will be shown avoids a second, blurring resample in the UI layer.
/// </summary>
public static class DisplaySizing
{
    /// <summary>Largest image (in source pixels) decoded at actual size; beyond this the image is fitted.</summary>
    public const long MaxActualSizePixels = 50_000_000;

    /// <summary>
    /// Box to decode into: the image area in physical pixels when fitting, the source size at actual size.
    /// </summary>
    public static DecodeBox DecodeBoxFor(ImageScaleMode mode, uint areaWidth, uint areaHeight, uint sourceWidth, uint sourceHeight)
    {
        if (mode == ImageScaleMode.ActualSize && sourceWidth > 0 && sourceHeight > 0)
        {
            return (long)sourceWidth * sourceHeight <= MaxActualSizePixels
                ? new DecodeBox(sourceWidth, sourceHeight)
                : new DecodeBox(Math.Max(1, areaWidth), Math.Max(1, areaHeight), ActualSizeRefused: true);
        }

        return new DecodeBox(Math.Max(1, areaWidth), Math.Max(1, areaHeight));
    }

    /// <summary>
    /// Size of the image element in device-independent pixels so that the decoded bitmap maps 1:1 to
    /// physical pixels (it was already decoded to fit; the UI must not enlarge or shrink it again).
    /// </summary>
    public static (double Width, double Height) ElementSize(uint pixelWidth, uint pixelHeight, double rasterizationScale)
    {
        double scale = rasterizationScale > 0 ? rasterizationScale : 1.0;
        return (pixelWidth / scale, pixelHeight / scale);
    }

    /// <summary>
    /// Expected displayed size (physical pixels) of an image before it is decoded, used to show the
    /// placeholder thumbnail at the size the final image will have (no jump when it is replaced).
    /// </summary>
    public static (uint Width, uint Height) ExpectedDisplayPixels(ImageScaleMode mode, uint areaWidth, uint areaHeight, uint sourceWidth, uint sourceHeight)
    {
        DecodeBox box = DecodeBoxFor(mode, areaWidth, areaHeight, sourceWidth, sourceHeight);
        return PreviewSizing.FitWithin(sourceWidth, sourceHeight, box.Width, box.Height);
    }
}
