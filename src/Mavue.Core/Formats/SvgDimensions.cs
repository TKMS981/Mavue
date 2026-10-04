using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace Mavue.Core.Formats;

/// <summary>
/// Intrinsic size of an SVG drawing in CSS pixels (device-independent pixels), read from the root element's
/// width/height or viewBox. Only the root start tag is read; DTDs and external entities are never processed.
/// </summary>
public static partial class SvgDimensions
{
    /// <summary>Size used when the file states neither width/height nor a viewBox (the CSS default for replaced elements).</summary>
    public static readonly (double Width, double Height) Default = (300, 150);

    // Absolute CSS units in px.
    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        [""] = 1,
        ["px"] = 1,
        ["pt"] = 96.0 / 72,
        ["pc"] = 16,
        ["in"] = 96,
        ["cm"] = 96 / 2.54,
        ["mm"] = 96 / 25.4,
        ["q"] = 96 / 101.6,
    };

    /// <summary>Reads the size from <paramref name="path"/>; null when the file is not an SVG document.</summary>
    public static (double Width, double Height)? TryRead(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
        return TryRead(stream);
    }

    /// <summary>Reads the size from a stream positioned at the start of an SVG document; null when it is not one.</summary>
    public static (double Width, double Height)? TryRead(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 0,
            MaxCharactersFromEntities = 1024,
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                if (!string.Equals(reader.LocalName, "svg", StringComparison.Ordinal))
                {
                    return null;
                }

                return FromAttributes(reader.GetAttribute("width"), reader.GetAttribute("height"), reader.GetAttribute("viewBox"));
            }
        }
        catch (XmlException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// The size from the root attributes: absolute width/height win; a percentage or missing side is derived from the
    /// viewBox's aspect ratio; without both, the viewBox size or <see cref="Default"/>.
    /// </summary>
    public static (double Width, double Height) FromAttributes(string? width, string? height, string? viewBox)
    {
        double? w = Length(width);
        double? h = Length(height);
        (double Width, double Height)? box = ViewBox(viewBox);
        if (w is { } bothW && h is { } bothH)
        {
            return (bothW, bothH);
        }

        if (box is { } b)
        {
            if (w is { } onlyW)
            {
                return (onlyW, onlyW * b.Height / b.Width);
            }

            if (h is { } onlyH)
            {
                return (onlyH * b.Width / b.Height, onlyH);
            }

            return b;
        }

        return (w ?? Default.Width, h ?? Default.Height);
    }

    private static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = LengthPattern().Match(value.Trim());
        if (!match.Success ||
            !double.TryParse(match.Groups["number"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) ||
            !Units.TryGetValue(match.Groups["unit"].Value, out double factor))
        {
            return null; // percentages and relative units (em, ex, vw…) have no intrinsic size
        }

        double px = number * factor;
        return px > 0 && double.IsFinite(px) ? px : null;
    }

    private static (double Width, double Height)? ViewBox(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double height) ||
            width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return null;
        }

        return (width, height);
    }

    [GeneratedRegex(@"^(?<number>[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?)\s*(?<unit>[a-zA-Z%]*)$")]
    private static partial Regex LengthPattern();
}
