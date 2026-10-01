using Mavue.Core.Formats;

namespace Mavue.Codecs;

/// <summary>Where a decoder for a given format comes from on this machine.</summary>
public enum CodecSource
{
    /// <summary>No decoder is available; the UI should explain how to get one (e.g. a Store extension).</summary>
    None = 0,

    /// <summary>Built into Windows Imaging Component.</summary>
    WicBuiltIn,

    /// <summary>WIC codec supplied by an optional Windows extension (HEIF, WebP, RAW, AV1, JPEG XL).</summary>
    WicExtension,

    /// <summary>Codec shipped with Mavue.</summary>
    Bundled,
}

/// <summary>Resolves which decoder handles a format, so callers never hard-code codec choices.</summary>
public interface IImageCodecRegistry
{
    CodecSource GetDecoderSource(FileFormat format);
}
