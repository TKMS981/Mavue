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

    /// <summary>Camera raw images (CR2, CR3, NEF, ARW, DNG, ORF, RW2, RAF…), decoded by the Raw Image Extension (WIC).</summary>
    CameraRaw,

    // Video (played through Media Foundation; codecs depend on what is installed).
    Mp4,
    QuickTime,
    Matroska,
    WebM,
    Avi,
    WindowsMediaVideo,
    MpegTransportStream,
    MpegProgramStream,
    OggVideo,

    // Audio.
    Mp3,
    Aac,
    M4a,
    Wav,
    Flac,
    OggAudio,
    WindowsMediaAudio,
}

/// <summary>Groups of <see cref="FileFormat"/> values.</summary>
public static class FileFormatKinds
{
    /// <summary>Raster and vector image formats (not PDF or media).</summary>
    public static bool IsImage(FileFormat format) => format is FileFormat.Jpeg or FileFormat.Png or FileFormat.Gif or FileFormat.Bmp
        or FileFormat.Tiff or FileFormat.WebP or FileFormat.Ico or FileFormat.Heif or FileFormat.Avif or FileFormat.Jpeg2000
        or FileFormat.JpegXl or FileFormat.Svg or FileFormat.CameraRaw;

    public static bool IsVideo(FileFormat format) => format is FileFormat.Mp4 or FileFormat.QuickTime or FileFormat.Matroska
        or FileFormat.WebM or FileFormat.Avi or FileFormat.WindowsMediaVideo or FileFormat.MpegTransportStream
        or FileFormat.MpegProgramStream or FileFormat.OggVideo;

    public static bool IsAudio(FileFormat format) => format is FileFormat.Mp3 or FileFormat.Aac or FileFormat.M4a
        or FileFormat.Wav or FileFormat.Flac or FileFormat.OggAudio or FileFormat.WindowsMediaAudio;

    public static bool IsMedia(FileFormat format) => IsVideo(format) || IsAudio(format);
}
