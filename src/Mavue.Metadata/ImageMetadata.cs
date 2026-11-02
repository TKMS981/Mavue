using Mavue.Metadata.Tiff;

namespace Mavue.Metadata;

/// <summary>Containers whose metadata Mavue reads itself (and, except TIFF, can remove without re-encoding).</summary>
public enum MetadataContainer
{
    /// <summary>Not read by Mavue (HEIF, AVIF, JPEG XL, CR3…): the Windows property system is used for display.</summary>
    Unsupported = 0,
    Jpeg,
    Png,
    WebP,
    Gif,

    /// <summary>TIFF and TIFF-based camera raw files (read; a clean copy is a converted export).</summary>
    Tiff,

    /// <summary>HEIF (HEIC) and AVIF: the Exif and XMP items (read; a clean copy is a converted export).</summary>
    Heif,
}

/// <summary>Kinds of metadata blocks in a file.</summary>
public enum MetadataBlockKind
{
    Exif,
    Xmp,
    Iptc,
    IccProfile,
    Comment,

    /// <summary>PNG tEXt/zTXt/iTXt text chunks.</summary>
    Text,

    /// <summary>PNG tIME (last modification time).</summary>
    Time,

    /// <summary>JFIF/JFXX thumbnails, multi-picture (MPF) secondary images.</summary>
    EmbeddedImage,

    /// <summary>Data after the end of the image (e.g. a motion photo's video, vendor trailers).</summary>
    Trailer,

    /// <summary>Other application data (unknown APPn segments, unknown chunks, GIF application extensions).</summary>
    Other,
}

/// <summary>A metadata block: where it is and how large it is.</summary>
public sealed record MetadataBlock(MetadataBlockKind Kind, long Offset, long Length, string? Label = null);

/// <summary>GPS position (decimal degrees, metres).</summary>
public sealed record GpsPosition(double? Latitude, double? Longitude, double? Altitude, DateTime? TimestampUtc, double? Direction)
{
    public bool HasCoordinates => Latitude is not null && Longitude is not null;
}

/// <summary>An exposure time or other rational value as stored.</summary>
public readonly record struct RationalValue(long Numerator, long Denominator)
{
    public double Value => Denominator == 0 ? 0 : (double)Numerator / Denominator;
}

/// <summary>The camera and authoring information of an EXIF block (only values that are present).</summary>
public sealed record ExifInfo
{
    public string? Make { get; init; }
    public string? Model { get; init; }
    public string? LensMake { get; init; }
    public string? LensModel { get; init; }
    public string? Software { get; init; }
    public string? Artist { get; init; }
    public string? Copyright { get; init; }
    public string? Description { get; init; }
    public string? UserComment { get; init; }
    public string? OwnerName { get; init; }
    public string? BodySerialNumber { get; init; }
    public string? LensSerialNumber { get; init; }
    public string? ImageUniqueId { get; init; }
    public string? XpTitle { get; init; }
    public string? XpComment { get; init; }
    public string? XpAuthor { get; init; }
    public string? XpKeywords { get; init; }
    public string? XpSubject { get; init; }
    public DateTime? DateTimeOriginal { get; init; }
    public string? OffsetTimeOriginal { get; init; }
    public DateTime? DateTimeDigitized { get; init; }
    public DateTime? DateTimeModified { get; init; }
    public RationalValue? ExposureTime { get; init; }
    public double? FNumber { get; init; }
    public long? Iso { get; init; }
    public double? FocalLength { get; init; }
    public long? FocalLength35mm { get; init; }
    public double? ExposureBias { get; init; }
    public long? ExposureProgram { get; init; }
    public long? MeteringMode { get; init; }
    public long? Flash { get; init; }
    public long? WhiteBalance { get; init; }
    public long? Orientation { get; init; }
    public long? PixelWidth { get; init; }
    public long? PixelHeight { get; init; }
    public GpsPosition? Gps { get; init; }

    /// <summary>The GPS directory exists (even without usable coordinates).</summary>
    public bool HasGpsDirectory { get; init; }

    public long MakerNoteBytes { get; init; }

    public long ThumbnailBytes { get; init; }

    /// <summary>"Canon EOS R5" without repeating the maker ("Canon Canon EOS R5").</summary>
    public string? Camera => (Make, Model) switch
    {
        (null, null) => null,
        (null, { } m) => m,
        ({ } mk, null) => mk,
        ({ } mk, { } m) when m.StartsWith(mk.Split(' ')[0], StringComparison.OrdinalIgnoreCase) => m,
        ({ } mk, { } m) => mk + " " + m,
    };
}

/// <summary>What an XMP packet says (read as text; no XML parser, so no entity expansion or external references).</summary>
public sealed record XmpSummary(long Bytes, string? CreatorTool, bool HasHistory, bool HasLocation, bool HasCreator, bool HasDescription, bool HasCaptureTime);

/// <summary>What an IPTC block says.</summary>
public sealed record IptcSummary(long Bytes, bool HasCreator, bool HasLocation, bool HasCaption, bool HasKeywords, bool HasCopyright);

/// <summary>Everything Mavue found in an image's metadata.</summary>
public sealed record ImageMetadata(
    MetadataContainer Container,
    IReadOnlyList<MetadataBlock> Blocks,
    ExifInfo? Exif,
    TiffStructure? ExifStructure,
    XmpSummary? Xmp,
    IptcSummary? Iptc,
    IReadOnlyList<string> TextKeys,
    IReadOnlyList<string> Comments)
{
    public static ImageMetadata Unsupported { get; } = new(MetadataContainer.Unsupported, [], null, null, null, null, [], []);

    public bool Has(MetadataBlockKind kind) => Blocks.Any(b => b.Kind == kind);

    /// <summary>Bytes of metadata (everything but the image data).</summary>
    public long MetadataBytes => Blocks.Sum(b => b.Length);
}
