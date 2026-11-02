using System.Globalization;

namespace Mavue.Metadata;

/// <summary>Short, language-neutral texts of camera settings ("f/2.8", "1/250 s", "ISO 400", "35 mm"). Pure.</summary>
public static class ExifText
{
    /// <summary>"1/250 s", "0.5 s", "2 s"; null when absent or not positive.</summary>
    public static string? Exposure(RationalValue? value)
    {
        if (value is not { } v || v.Numerator <= 0 || v.Denominator <= 0)
        {
            return null;
        }

        double seconds = v.Value;
        if (seconds < 1)
        {
            double inverse = 1 / seconds;
            return Math.Abs(inverse - Math.Round(inverse)) < 0.05 || inverse >= 10
                ? string.Create(CultureInfo.InvariantCulture, $"1/{Math.Round(inverse):0} s")
                : string.Create(CultureInfo.InvariantCulture, $"{seconds:0.0#} s");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{seconds:0.#} s");
    }

    public static string? FNumber(double? value) =>
        value is > 0 and < 1000 ? string.Create(CultureInfo.InvariantCulture, $"f/{value:0.0#}") : null;

    public static string? Iso(long? value) => value is > 0 ? string.Create(CultureInfo.InvariantCulture, $"ISO {value}") : null;

    /// <summary>"35 mm", or "35 mm (52 mm)" with the 35 mm equivalent.</summary>
    public static string? FocalLength(double? value, long? equivalent)
    {
        if (value is not { } v || v <= 0 || v >= 100_000)
        {
            return null;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{v:0.#} mm");
        return equivalent is > 0 && Math.Abs(equivalent.Value - v) >= 1
            ? text + string.Create(CultureInfo.InvariantCulture, $" ({equivalent} mm)")
            : text;
    }

    /// <summary>"+0.3 EV", "−1 EV"; null when 0 or absent.</summary>
    public static string? ExposureBias(double? value)
    {
        if (value is not { } v || Math.Abs(v) < 0.05 || Math.Abs(v) > 100)
        {
            return null;
        }

        string number = Math.Abs(v).ToString("0.#", CultureInfo.InvariantCulture);
        return (v > 0 ? "+" : "−") + number + " EV";
    }

    /// <summary>The camera settings on one line ("f/2.8 · 1/250 s · ISO 400 · 35 mm · +0.3 EV").</summary>
    public static string? Settings(ExifInfo? exif)
    {
        if (exif is null)
        {
            return null;
        }

        string[] parts = new[] { FNumber(exif.FNumber), Exposure(exif.ExposureTime), Iso(exif.Iso), FocalLength(exif.FocalLength, exif.FocalLength35mm), ExposureBias(exif.ExposureBias) }
            .OfType<string>().ToArray();
        return parts.Length == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>The flash fired (bit 0 of the EXIF Flash value).</summary>
    public static bool? FlashFired(long? flash) => flash is { } f ? (f & 1) == 1 : null;

    /// <summary>"35.68123, 139.76712" and the altitude.</summary>
    public static string? Position(GpsPosition? gps)
    {
        if (gps is not { HasCoordinates: true })
        {
            return null;
        }

        string text = string.Create(CultureInfo.InvariantCulture, $"{gps.Latitude:0.00000}, {gps.Longitude:0.00000}");
        return gps.Altitude is { } altitude ? text + string.Create(CultureInfo.InvariantCulture, $" · {altitude:0} m") : text;
    }

    /// <summary>"2026-10-01 14:03:22 (+09:00)" in the local culture's date order.</summary>
    public static string? Taken(ExifInfo? exif, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        DateTime? value = exif?.DateTimeOriginal ?? exif?.DateTimeDigitized;
        if (value is null)
        {
            return null;
        }

        string text = value.Value.ToString("g", culture);
        return exif?.OffsetTimeOriginal is { } offset ? text + " (" + offset + ")" : text;
    }
}
