namespace Mavue.Core.Viewing;

/// <summary>
/// The size Quick View opens at (docs/ARCHITECTURE.md §4.7.9): a share of the monitor's work area, capped on very
/// large screens, shaped like a picture on ultrawide and portrait screens, and never smaller than a usable window.
/// All sizes are physical pixels of the target monitor; <c>scale</c> is that monitor's DPI / 96.
/// </summary>
public static class QuickViewWindowSize
{
    /// <summary>Share of the work area's width (was 0.70 until 2026-11-03).</summary>
    public const double WidthShare = 0.60;

    /// <summary>Share of the work area's height (was 0.75 until 2026-11-03).</summary>
    public const double HeightShare = 0.64;

    /// <summary>Largest size in device-independent pixels (a 4K monitor at 100 % would otherwise get a huge window).</summary>
    public const double MaxWidthDip = 1600;

    public const double MaxHeightDip = 1040;

    /// <summary>Smallest size in device-independent pixels (still limited to the work area on tiny screens).</summary>
    public const double MinWidthDip = 640;

    public const double MinHeightDip = 480;

    /// <summary>Widest shape (width : height): 16:10, so an ultrawide monitor does not get a strip.</summary>
    public const double WidestAspect = 1.6;

    /// <summary>Tallest shape (width : height): 4:5, for portrait monitors.</summary>
    public const double TallestAspect = 0.8;

    public static (int Width, int Height) For(int workWidth, int workHeight, double scale)
    {
        if (workWidth <= 0 || workHeight <= 0)
        {
            return (Math.Max(1, workWidth), Math.Max(1, workHeight));
        }

        if (!(scale > 0) || double.IsInfinity(scale))
        {
            scale = 1;
        }

        double width = Math.Min(workWidth * WidthShare, MaxWidthDip * scale);
        double height = Math.Min(workHeight * HeightShare, MaxHeightDip * scale);
        width = Math.Min(width, height * WidestAspect);
        height = Math.Min(height, width / TallestAspect);
        width = Math.Min(Math.Max(width, MinWidthDip * scale), workWidth);
        height = Math.Min(Math.Max(height, MinHeightDip * scale), workHeight);
        return ((int)Math.Round(width), (int)Math.Round(height));
    }
}
