namespace Mavue.Core.Formats;

/// <summary>
/// File formats Mavue recognizes. Recognition does not imply that a decoder is available
/// on the current machine; codec availability is resolved by Mavue.Codecs.
/// </summary>
public enum FileFormat
{
    Unknown = 0,
    Pdf,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Ico,
    Heif,
    Avif,
    Jpeg2000,
    JpegXl,
    Svg,
}
