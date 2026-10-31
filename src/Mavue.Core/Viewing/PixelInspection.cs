using System.Globalization;

namespace Mavue.Core.Viewing;

/// <summary>One pixel's color as stored in the file (8 bits per channel, straight alpha).</summary>
public readonly record struct PixelColor(byte R, byte G, byte B, byte A)
{
    /// <summary>"#RRGGBB".</summary>
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>"#RRGGBBAA" (CSS order).</summary>
    public string HexWithAlpha => string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>Alpha as a whole percentage (0–100).</summary>
    public int AlphaPercent => (int)Math.Round(A * 100 / 255.0, MidpointRounding.AwayFromZero);

    /// <summary>Reads a pixel from 32-bit BGRA memory.</summary>
    public static PixelColor FromBgra(ReadOnlySpan<byte> bgra) => new(bgra[2], bgra[1], bgra[0], bgra[3]);
}

/// <summary>A rectangle of pixels (non-negative size).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// The pixel inspector's arithmetic (docs/ARCHITECTURE.md §4.7.8): from a point on the displayed image to the
/// pixel of the upright image (undoing zoom and the view's rotation and mirror), from there to the pixel as stored
/// in the file (undoing the EXIF orientation), and the tile of the file that is decoded at full resolution to read it.
/// Pure; coordinates are zero-based pixels.
/// </summary>
public static class PixelInspection
{
    /// <summary>Default side of the full-resolution tile decoded around the pointer.</summary>
    public const int DefaultTileSize = 256;

    /// <summary>
    /// The pixel of the upright image (EXIF orientation applied, before the view's rotation) under a point of the
    /// image element, or null when the point is outside the element.
    /// </summary>
    /// <param name="x">Point in the element (any unit, origin at the element's top left).</param>
    /// <param name="y">Point in the element.</param>
    /// <param name="elementWidth">Element width in the same unit.</param>
    /// <param name="elementHeight">Element height in the same unit.</param>
    /// <param name="uprightWidth">Image width before the view's rotation.</param>
    /// <param name="uprightHeight">Image height before the view's rotation.</param>
    /// <param name="view">The view's rotation and mirror.</param>
    public static (int X, int Y)? ImagePixelAt(double x, double y, double elementWidth, double elementHeight, int uprightWidth, int uprightHeight, ViewOrientation view)
    {
        if (elementWidth <= 0 || elementHeight <= 0 || uprightWidth <= 0 || uprightHeight <= 0 ||
            !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= elementWidth || y >= elementHeight)
        {
            return null;
        }

        (int shownWidth, int shownHeight) = view.Oriented(uprightWidth, uprightHeight);
        int sx = Math.Clamp((int)Math.Floor(x / elementWidth * shownWidth), 0, shownWidth - 1);
        int sy = Math.Clamp((int)Math.Floor(y / elementHeight * shownHeight), 0, shownHeight - 1);
        return Unmap(view, sx, sy, uprightWidth, uprightHeight);
    }

    /// <summary>
    /// Inverse of <see cref="ViewOrientation.MapPixel"/>: the pixel of a <paramref name="width"/> ×
    /// <paramref name="height"/> image (before turning) that is shown at (<paramref name="x"/>, <paramref name="y"/>).
    /// </summary>
    public static (int X, int Y) Unmap(ViewOrientation view, int x, int y, int width, int height)
    {
        (int ux, int uy) = view.QuarterTurns switch
        {
            1 => (y, height - 1 - x),
            2 => (width - 1 - x, height - 1 - y),
            3 => (width - 1 - y, x),
            _ => (x, y),
        };
        return view.Mirrored ? (width - 1 - ux, uy) : (ux, uy);
    }

    /// <summary>Size of the upright image for a file stored as <paramref name="storedWidth"/> × <paramref name="storedHeight"/>.</summary>
    public static (int Width, int Height) UprightSize(int storedWidth, int storedHeight, int exifOrientation) =>
        exifOrientation is >= 5 and <= 8 ? (storedHeight, storedWidth) : (storedWidth, storedHeight);

    /// <summary>
    /// Where the upright pixel (<paramref name="x"/>, <paramref name="y"/>) is stored in the file, for an image
    /// stored as <paramref name="storedWidth"/> × <paramref name="storedHeight"/> with an EXIF orientation (1–8;
    /// other values are treated as 1).
    /// </summary>
    public static (int X, int Y) StoredPixel(int x, int y, int storedWidth, int storedHeight, int exifOrientation)
    {
        (int uprightWidth, int uprightHeight) = UprightSize(storedWidth, storedHeight, exifOrientation);
        return exifOrientation switch
        {
            2 => (uprightWidth - 1 - x, y),                          // mirrored left to right
            3 => (uprightWidth - 1 - x, uprightHeight - 1 - y),      // half turn
            4 => (x, uprightHeight - 1 - y),                         // mirrored top to bottom
            5 => (y, x),                                             // transposed
            6 => (y, uprightWidth - 1 - x),                          // shown turned 90° clockwise
            7 => (uprightHeight - 1 - y, uprightWidth - 1 - x),      // transversed
            8 => (uprightHeight - 1 - y, x),                         // shown turned 90° counter-clockwise
            _ => (x, y),
        };
    }

    /// <summary>
    /// The aligned tile (multiples of <paramref name="tileSize"/>) that contains the pixel, clipped to the image.
    /// Aligned tiles let one decoded tile serve every pointer position inside it.
    /// </summary>
    public static PixelRect TileAround(int x, int y, int width, int height, int tileSize = DefaultTileSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tileSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int left = Math.Clamp(x, 0, width - 1) / tileSize * tileSize;
        int top = Math.Clamp(y, 0, height - 1) / tileSize * tileSize;
        return new PixelRect(left, top, Math.Min(tileSize, width - left), Math.Min(tileSize, height - top));
    }

    /// <summary>"R 255  G 128  B 0".</summary>
    public static string RgbText(PixelColor color) => string.Create(CultureInfo.InvariantCulture, $"R {color.R}  G {color.G}  B {color.B}");

    /// <summary>"A 255 (100%)".</summary>
    public static string AlphaText(PixelColor color) => string.Create(CultureInfo.InvariantCulture, $"A {color.A} ({color.AlphaPercent}%)");

    /// <summary>"X 120  Y 45".</summary>
    public static string PositionText(int x, int y) => string.Create(CultureInfo.InvariantCulture, $"X {x}  Y {y}");
}
