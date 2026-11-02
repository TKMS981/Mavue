using System.Globalization;

namespace Mavue.Metadata;

/// <summary>Kinds of possibly private information in an image's metadata.</summary>
public enum PrivacyKind
{
    /// <summary>GPS position (EXIF), place names (XMP, IPTC).</summary>
    Location,

    /// <summary>When the photo was taken (EXIF, XMP, IPTC, GPS time).</summary>
    CaptureTime,

    /// <summary>Camera, lens and exposure settings.</summary>
    CameraDetails,

    /// <summary>Artist, author, creator, copyright.</summary>
    AuthorCopyright,

    /// <summary>Serial numbers of the body and lens, unique image IDs: identify the device.</summary>
    DeviceIdentity,

    /// <summary>The camera owner's name.</summary>
    OwnerName,

    /// <summary>Editing software and editing history.</summary>
    EditingSoftware,

    /// <summary>Titles, descriptions, comments, keywords, text chunks.</summary>
    Descriptions,

    /// <summary>Embedded thumbnails, secondary images (MPF), data after the image (e.g. a motion photo's video).
    /// A thumbnail may still show what was cropped or edited away.</summary>
    EmbeddedImages,

    /// <summary>Manufacturer-specific data (maker notes): undocumented, often includes serial numbers and settings.</summary>
    MakerNote,

    /// <summary>Other application data.</summary>
    OtherData,

    /// <summary>The color profile (not private; kept so colors stay right).</summary>
    ColorProfile,
}

/// <summary>How much a finding matters before sharing.</summary>
public enum PrivacyLevel
{
    /// <summary>Identifies a place, a person or a device.</summary>
    High,

    /// <summary>Tells more than the picture (time, equipment, authorship, hidden content).</summary>
    Medium,

    /// <summary>Technical information.</summary>
    Low,

    /// <summary>Not private; kept when a copy is made.</summary>
    Kept,
}

/// <summary>One finding: what, how much it matters and a short value to show the user (never logged).</summary>
public sealed record PrivacyFinding(PrivacyKind Kind, PrivacyLevel Level, string? Detail);

/// <summary>Which metadata a "copy without private metadata" removes (ARCHITECTURE §4.7.13).</summary>
/// <param name="RemoveLocation">GPS position and place names.</param>
/// <param name="RemoveCaptureTime">Dates and times.</param>
/// <param name="RemoveCameraDetails">Camera, lens and exposure settings.</param>
/// <param name="RemoveAuthorCopyright">Artist and copyright.</param>
/// <param name="KeepColorProfile">Keep the ICC profile (colors stay right).</param>
/// <param name="KeepOrientation">Keep the EXIF orientation (the picture stays upright).</param>
/// <remarks>Always removed: serial numbers and IDs, owner name, software and history, descriptions and comments,
/// thumbnails and secondary images, data after the image, maker notes, XMP, IPTC and other application data.</remarks>
public sealed record StripOptions(
    bool RemoveLocation = true,
    bool RemoveCaptureTime = true,
    bool RemoveCameraDetails = true,
    bool RemoveAuthorCopyright = true,
    bool KeepColorProfile = true,
    bool KeepOrientation = true)
{
    public static StripOptions Default { get; } = new();

    /// <summary>Whether a finding of <paramref name="kind"/> is removed by these options.</summary>
    public bool Removes(PrivacyKind kind) => kind switch
    {
        PrivacyKind.Location => RemoveLocation,
        PrivacyKind.CaptureTime => RemoveCaptureTime,
        PrivacyKind.CameraDetails => RemoveCameraDetails,
        PrivacyKind.AuthorCopyright => RemoveAuthorCopyright,
        PrivacyKind.ColorProfile => !KeepColorProfile,
        _ => true,
    };
}

/// <summary>Lists what an image's metadata reveals (pure; the values are shown to the user and never logged).</summary>
public static class PrivacyCheck
{
    public static IReadOnlyList<PrivacyFinding> Review(ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var findings = new List<PrivacyFinding>();
        ExifInfo? exif = metadata.Exif;
        XmpSummary? xmp = metadata.Xmp;
        IptcSummary? iptc = metadata.Iptc;

        // Location
        if (exif?.Gps is { HasCoordinates: true } gps)
        {
            string detail = string.Create(CultureInfo.InvariantCulture, $"{gps.Latitude:0.00000}, {gps.Longitude:0.00000}");
            if (gps.Altitude is { } altitude)
            {
                detail += string.Create(CultureInfo.InvariantCulture, $" ({altitude:0} m)");
            }

            findings.Add(new(PrivacyKind.Location, PrivacyLevel.High, detail));
        }
        else if (exif is { HasGpsDirectory: true } || xmp is { HasLocation: true } || iptc is { HasLocation: true })
        {
            findings.Add(new(PrivacyKind.Location, PrivacyLevel.High, null));
        }

        // Time
        DateTime? taken = exif?.DateTimeOriginal ?? exif?.DateTimeDigitized ?? exif?.Gps?.TimestampUtc;
        if (taken is { } time)
        {
            string detail = time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (exif?.OffsetTimeOriginal is { } offset ? " (" + offset + ")" : string.Empty);
            findings.Add(new(PrivacyKind.CaptureTime, PrivacyLevel.Medium, detail));
        }
        else if (exif?.DateTimeModified is not null || xmp is { HasCaptureTime: true } || metadata.Has(MetadataBlockKind.Time))
        {
            findings.Add(new(PrivacyKind.CaptureTime, PrivacyLevel.Medium, exif?.DateTimeModified?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }

        // Equipment
        string? camera = Join(exif?.Camera, exif?.LensModel);
        if (camera is not null || exif?.ExposureTime is not null || exif?.FNumber is not null || exif?.Iso is not null)
        {
            findings.Add(new(PrivacyKind.CameraDetails, PrivacyLevel.Low, camera));
        }

        string? serial = Join(exif?.BodySerialNumber, exif?.LensSerialNumber, exif?.ImageUniqueId);
        if (serial is not null)
        {
            findings.Add(new(PrivacyKind.DeviceIdentity, PrivacyLevel.High, serial));
        }

        if (exif?.OwnerName is { } owner)
        {
            findings.Add(new(PrivacyKind.OwnerName, PrivacyLevel.High, owner));
        }

        // People
        string? author = Join(exif?.Artist, exif?.XpAuthor, exif?.Copyright);
        if (author is not null || xmp is { HasCreator: true } || iptc is { HasCreator: true } || iptc is { HasCopyright: true })
        {
            findings.Add(new(PrivacyKind.AuthorCopyright, PrivacyLevel.Medium, author));
        }

        // Software and history
        string? software = Join(exif?.Software, xmp?.CreatorTool);
        if (software is not null || xmp is { HasHistory: true })
        {
            findings.Add(new(PrivacyKind.EditingSoftware, xmp is { HasHistory: true } ? PrivacyLevel.Medium : PrivacyLevel.Low, software));
        }

        // Words
        string? words = Join(exif?.Description, exif?.UserComment, exif?.XpTitle, exif?.XpComment, exif?.XpSubject, exif?.XpKeywords,
            metadata.Comments.Count > 0 ? metadata.Comments[0] : null, metadata.TextKeys.Count > 0 ? string.Join(", ", metadata.TextKeys) : null);
        if (words is not null || xmp is { HasDescription: true } || iptc is { HasCaption: true } || iptc is { HasKeywords: true })
        {
            findings.Add(new(PrivacyKind.Descriptions, PrivacyLevel.Medium, words is { Length: > 120 } ? words[..120] + "…" : words));
        }

        // Hidden pictures and data
        long embedded = (exif?.ThumbnailBytes ?? 0) + metadata.Blocks.Where(b => b.Kind is MetadataBlockKind.EmbeddedImage or MetadataBlockKind.Trailer).Sum(b => b.Length);
        if (embedded > 0)
        {
            findings.Add(new(PrivacyKind.EmbeddedImages, PrivacyLevel.Medium, Bytes(embedded)));
        }

        if (exif is { MakerNoteBytes: > 0 })
        {
            findings.Add(new(PrivacyKind.MakerNote, PrivacyLevel.Medium, Bytes(exif.MakerNoteBytes)));
        }

        string[] other = metadata.Blocks.Where(b => b.Kind == MetadataBlockKind.Other).Select(b => b.Label ?? "?").Distinct().Take(8).ToArray();
        bool otherXmp = xmp is not null && !xmp.HasLocation && !xmp.HasCreator && !xmp.HasDescription && !xmp.HasHistory && xmp.CreatorTool is null;
        if (other.Length > 0 || otherXmp || (iptc is not null && !iptc.HasCreator && !iptc.HasLocation && !iptc.HasCaption && !iptc.HasKeywords))
        {
            findings.Add(new(PrivacyKind.OtherData, PrivacyLevel.Low, other.Length > 0 ? string.Join(", ", other) : null));
        }

        if (metadata.Has(MetadataBlockKind.IccProfile))
        {
            findings.Add(new(PrivacyKind.ColorProfile, PrivacyLevel.Kept, null));
        }

        return findings;
    }

    /// <summary>
    /// Findings of <paramref name="after"/> (a copy made with <paramref name="options"/>) that should have been removed.
    /// Empty when the copy is clean.
    /// </summary>
    public static IReadOnlyList<PrivacyKind> Leftovers(ImageMetadata after, StripOptions options)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(options);
        return Review(after).Where(f => f.Level != PrivacyLevel.Kept && options.Removes(f.Kind)).Select(f => f.Kind).Distinct().ToArray();
    }

    private static string? Join(params string?[] parts)
    {
        string[] present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        return present.Length == 0 ? null : string.Join(" · ", present);
    }

    private static string Bytes(long bytes) => bytes < 1024
        ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
        : bytes < 1024 * 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB")
            : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");
}
