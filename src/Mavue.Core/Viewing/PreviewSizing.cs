namespace Mavue.Core.Viewing;

/// <summary>
/// Chooses decode dimensions for "fit to window" previews so large images are decoded directly to
/// screen size (WIC scaled decode) instead of being expanded to full resolution in memory.
/// </summary>
public static class PreviewSizing
{
    /// <summary>
    /// Returns the size to decode an image of <paramref name="imageWidth"/> × <paramref name="imageHeight"/>
    /// so that it fits inside the viewport (in physical pixels) without upscaling.
    /// Aspect ratio is preserved; each side is at least 1 pixel.
    /// </summary>
    public static (uint Width, uint Height) FitWithin(uint imageWidth, uint imageHeight, uint viewportWidth, uint viewportHeight)
    {
        if (imageWidth == 0 || imageHeight == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "Image dimensions must be positive.");
        }

        if (viewportWidth == 0 || viewportHeight == 0)
        {
            return (imageWidth, imageHeight);
        }

        double scale = Math.Min(1.0, Math.Min((double)viewportWidth / imageWidth, (double)viewportHeight / imageHeight));
        uint width = (uint)Math.Max(1, Math.Round(imageWidth * scale));
        uint height = (uint)Math.Max(1, Math.Round(imageHeight * scale));
        return (width, height);
    }
}
