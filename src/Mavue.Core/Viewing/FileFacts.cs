using Mavue.Core.Formats;

namespace Mavue.Core.Viewing;

/// <summary>File metadata gathered before any decoding.</summary>
public sealed record FileFacts(System.IO.FileAttributes Attributes, long Length, FileFormat Format, PreviewAccess Access, DateTime LastWriteUtc = default)
{
    public static FileFacts Read(string path)
    {
        if (PreviewSafetyPolicy.IsDevicePath(path))
        {
            return new FileFacts(0, 0, FileFormat.Unknown, PreviewAccess.NotAFile);
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return new FileFacts(0, 0, FileFormat.Unknown, PreviewAccess.NotAFile);
        }

        PreviewAccess access = PreviewSafetyPolicy.CheckAttributes(info.Attributes);

        // Reading the header of a cloud placeholder would download it; rely on the extension instead.
        FileFormat format = access == PreviewAccess.Allowed
            ? FileFormatDetector.Detect(path)
            : FileFormatDetector.FromExtension(info.Extension);
        return new FileFacts(info.Attributes, info.Length, format, access, info.LastWriteTimeUtc);
    }
}
