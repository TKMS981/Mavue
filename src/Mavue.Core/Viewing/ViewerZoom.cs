namespace Mavue.Core.Viewing;

/// <summary>How the zoom factor of the viewer is chosen.</summary>
public enum ZoomMode
{
    /// <summary>Default. Reduced to fit the area; small content keeps its own size (never enlarged).</summary>
    Fit = 0,

    /// <summary>The width fills the area (pages of a document); may enlarge.</summary>
    FitWidth,

    /// <summary>A factor the user chose (100 % = actual size).</summary>
    Custom,
}

/// <summary>The viewer's zoom: a mode, and the factor used when the mode is <see cref="ZoomMode.Custom"/>.</summary>
/// <param name="Mode">How the factor is chosen.</param>
/// <param name="Factor">Display pixels per source pixel for <see cref="ZoomMode.Custom"/> (1 = actual size).</param>
public readonly record struct ZoomSetting(ZoomMode Mode, double Factor = 1)
{
    public static readonly ZoomSetting Fit = new(ZoomMode.Fit);

    public static readonly ZoomSetting FitWidth = new(ZoomMode.FitWidth);

    public static readonly ZoomSetting ActualSize = new(ZoomMode.Custom, 1);

    /// <summary>The setting for the user's default scale (fit without enlarging, or actual size).</summary>
    public static ZoomSetting For(ImageScaleMode mode) => mode == ImageScaleMode.ActualSize ? ActualSize : Fit;
}

/// <summary>
/// Pure zoom arithmetic shared by the main window and Quick View. Sizes are in physical pixels. A "source" is the
/// content at 100 %: an image's pixels, or a PDF page / SVG drawing at 96 dpi times the monitor scale.
/// <para>
/// Zooming is shown at once by stretching the bitmap already on screen; the viewer then decodes again at
/// <see cref="DecodeSize"/> so the result is sharp (never more pixels than the source has, for raster images).
/// </para>
/// </summary>
public static class ViewerZoom
{
    public const double MinFactor = 0.02;

    public const double MaxFactor = 32;

    /// <summary>Longest side of zoomed content on screen; larger factors are refused (the element would be unwieldy).</summary>
    public const double MaxDisplaySide = 200_000;

    /// <summary>Zoom factor change per mouse-wheel notch with Ctrl.</summary>
    public const double WheelStep = 1.2;

    /// <summary>Steps for zoom in/out (keyboard and buttons), like common image viewers.</summary>
    public static readonly IReadOnlyList<double> Steps =
    [
        0.05, 0.1, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8, 12, 16, 24, 32,
    ];

    /// <summary>
    /// The factor that <paramref name="setting"/> gives for a source of <paramref name="sourceWidth"/> ×
    /// <paramref name="sourceHeight"/> (already turned as shown) in an area of <paramref name="areaWidth"/> ×
    /// <paramref name="areaHeight"/>.
    /// </summary>
    /// <param name="allowEnlarge">Fit may enlarge small content (vector pages); images are never enlarged by Fit.</param>
    public static double FactorFor(ZoomSetting setting, double sourceWidth, double sourceHeight, double areaWidth, double areaHeight, bool allowEnlarge = false)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return 1;
        }

        double factor = setting.Mode switch
        {
            ZoomMode.Fit => Math.Min(Math.Max(1, areaWidth) / sourceWidth, Math.Max(1, areaHeight) / sourceHeight),
            ZoomMode.FitWidth => Math.Max(1, areaWidth) / sourceWidth,
            _ => setting.Factor,
        };
        if (setting.Mode == ZoomMode.Fit && !allowEnlarge)
        {
            factor = Math.Min(1, factor);
        }

        return Clamp(factor, sourceWidth, sourceHeight);
    }

    /// <summary>Keeps a factor inside the limits and below <see cref="MaxDisplaySide"/>.</summary>
    public static double Clamp(double factor, double sourceWidth, double sourceHeight)
    {
        double longest = Math.Max(1, Math.Max(sourceWidth, sourceHeight));
        double max = Math.Min(MaxFactor, Math.Max(MinFactor, MaxDisplaySide / longest));
        return Math.Clamp(double.IsFinite(factor) ? factor : 1, MinFactor, max);
    }

    /// <summary>The next step above <paramref name="factor"/> (a factor between steps goes to the nearest step above).</summary>
    public static double StepIn(double factor)
    {
        foreach (double step in Steps)
        {
            if (step > factor * 1.001)
            {
                return step;
            }
        }

        return MaxFactor;
    }

    /// <summary>The next step below <paramref name="factor"/>.</summary>
    public static double StepOut(double factor)
    {
        for (int i = Steps.Count - 1; i >= 0; i--)
        {
            if (Steps[i] < factor / 1.001)
            {
                return Steps[i];
            }
        }

        return MinFactor;
    }

    /// <summary>The factor after <paramref name="wheelDelta"/> (multiples of 120 per notch) with Ctrl held.</summary>
    public static double Wheel(double factor, int wheelDelta) => factor * Math.Pow(WheelStep, wheelDelta / 120.0);

    /// <summary>Displayed size of the source at <paramref name="factor"/> (at least one pixel each way).</summary>
    public static (uint Width, uint Height) DisplaySize(double sourceWidth, double sourceHeight, double factor) =>
        ((uint)Math.Max(1, Math.Round(sourceWidth * factor)), (uint)Math.Max(1, Math.Round(sourceHeight * factor)));

    /// <summary>
    /// Pixels to decode for a raster source shown at <paramref name="displayWidth"/> × <paramref name="displayHeight"/>:
    /// the displayed size when reduced, the source size when enlarged (the screen stretches it), and never more than
    /// <paramref name="maxPixels"/> (fitted inside that budget, then stretched).
    /// </summary>
    public static (uint Width, uint Height) DecodeSize(uint sourceWidth, uint sourceHeight, uint displayWidth, uint displayHeight, long maxPixels)
    {
        uint width = Math.Min(Math.Max(1, displayWidth), Math.Max(1, sourceWidth));
        uint height = Math.Min(Math.Max(1, displayHeight), Math.Max(1, sourceHeight));
        if ((long)width * height <= maxPixels || maxPixels <= 0)
        {
            return (width, height);
        }

        double scale = Math.Sqrt(maxPixels / ((double)width * height));
        return ((uint)Math.Max(1, Math.Floor(width * scale)), (uint)Math.Max(1, Math.Floor(height * scale)));
    }

    /// <summary>
    /// Scroll offset that keeps the point under the pointer still while zooming: the content point at
    /// <paramref name="offset"/> + <paramref name="anchor"/> moves to (that × <paramref name="ratio"/>).
    /// </summary>
    /// <param name="offset">Current scroll offset.</param>
    /// <param name="anchor">Position of the fixed point inside the viewport (the mouse pointer).</param>
    /// <param name="ratio">New size ÷ old size.</param>
    /// <param name="extent">New content length.</param>
    /// <param name="viewport">Viewport length.</param>
    public static double AnchoredOffset(double offset, double anchor, double ratio, double extent, double viewport)
    {
        double target = ((offset + anchor) * ratio) - anchor;
        return Math.Clamp(target, 0, Math.Max(0, extent - viewport));
    }

    /// <summary>"100%" style text (whole percent from 10 %, one decimal below).</summary>
    public static string Percent(double factor) => factor >= 0.1
        ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{Math.Round(factor * 100):0}%")
        : string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{factor * 100:0.#}%");
}
