namespace Mavue.Metadata;

/// <summary>Categories of metadata that can be selectively preserved or removed on export (SPEC §7, §8).</summary>
[Flags]
public enum MetadataCategories
{
    None = 0,
    Exif = 1 << 0,
    Gps = 1 << 1,
    Xmp = 1 << 2,
    Iptc = 1 << 3,
    IccProfile = 1 << 4,
    All = Exif | Gps | Xmp | Iptc | IccProfile,
}

/// <summary>Reads and rewrites metadata without re-encoding pixel data where the format allows it.</summary>
public interface IMetadataService
{
    ValueTask<MetadataCategories> GetPresentCategoriesAsync(string path, CancellationToken cancellationToken);
}
