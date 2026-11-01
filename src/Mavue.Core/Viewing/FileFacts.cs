using Mavue.Core.Formats;

namespace Mavue.Core.Viewing;

/// <summary>File metadata gathered before any decoding.</summary>
/// <param name="LooksLikeText">The header has no zero bytes and few control characters (read with the format; false
/// when the content was not read, e.g. for cloud placeholders). Lets a file of unknown type be shown as text.</param>
public sealed record FileFacts(System.IO.FileAttributes Attributes, long Length, FileFormat Format, PreviewAccess Access, DateTime LastWriteUtc = default, bool LooksLikeText = false)
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
        bool looksLikeText = false;
        FileFormat format = access == PreviewAccess.Allowed
            ? FileFormatDetector.Detect(path, out looksLikeText)
            : FileFormatDetector.FromExtension(info.Name);
        return new FileFacts(info.Attributes, info.Length, format, access, info.LastWriteTimeUtc, looksLikeText);
    }
}
