using System.Globalization;
using Mavue.Core.Formats;

namespace Mavue.Core.Viewing;

/// <summary>What kind of thing a file is, for its icon on the information card.</summary>
public enum FileKindCategory
{
    Other = 0,
    Folder,
    Image,
    Video,
    Audio,
    Pdf,
    Document,
    Spreadsheet,
    Presentation,
    Text,
    Code,
    Archive,
    Application,
    Shortcut,
    Font,
    DiskImage,
}

/// <summary>
/// Mark-of-the-Web: the <c>Zone.Identifier</c> stream Windows attaches to downloaded files.
/// Only the zone and the host name of the source are kept (never full URLs, which may carry personal data).
/// </summary>
/// <param name="ZoneId">URL security zone (0 local machine, 1 intranet, 2 trusted, 3 Internet, 4 restricted).</param>
/// <param name="SourceHost">Host name of the download page or file, when recorded.</param>
public sealed record MarkOfTheWeb(int ZoneId, string? SourceHost)
{
    /// <summary>The Internet or restricted zone: Windows treats the file as coming from the web.</summary>
    public bool IsFromInternet => ZoneId >= 3;
}

/// <summary>
/// Universal Preview's facts about any file or folder (docs/ARCHITECTURE.md §4.7.8): read from the file system only —
/// attributes, size, dates and the Mark-of-the-Web stream. The file's contents are never read.
/// </summary>
public sealed record UniversalFileInfo(
    string Name,
    string Extension,
    bool Exists,
    bool IsFolder,
    long? Length,
    DateTime? CreatedUtc,
    DateTime? ModifiedUtc,
    FileAttributes Attributes,
    FileKindCategory Category,
    string? MimeType,
    MarkOfTheWeb? Origin)
{
    /// <summary>Largest Zone.Identifier stream read (real ones are a few hundred bytes).</summary>
    public const int MaxZoneIdentifierBytes = 16 * 1024;

    private static readonly Dictionary<string, (FileKindCategory Category, string? Mime)> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = (FileKindCategory.Text, "text/plain"),
        [".log"] = (FileKindCategory.Text, "text/plain"),
        [".md"] = (FileKindCategory.Text, "text/markdown"),
        [".markdown"] = (FileKindCategory.Text, "text/markdown"),
        [".csv"] = (FileKindCategory.Spreadsheet, "text/csv"),
        [".tsv"] = (FileKindCategory.Spreadsheet, "text/tab-separated-values"),
        [".rtf"] = (FileKindCategory.Document, "application/rtf"),
        [".doc"] = (FileKindCategory.Document, "application/msword"),
        [".docx"] = (FileKindCategory.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
        [".odt"] = (FileKindCategory.Document, "application/vnd.oasis.opendocument.text"),
        [".xls"] = (FileKindCategory.Spreadsheet, "application/vnd.ms-excel"),
        [".xlsx"] = (FileKindCategory.Spreadsheet, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
        [".ods"] = (FileKindCategory.Spreadsheet, "application/vnd.oasis.opendocument.spreadsheet"),
        [".ppt"] = (FileKindCategory.Presentation, "application/vnd.ms-powerpoint"),
        [".pptx"] = (FileKindCategory.Presentation, "application/vnd.openxmlformats-officedocument.presentationml.presentation"),
        [".odp"] = (FileKindCategory.Presentation, "application/vnd.oasis.opendocument.presentation"),
        [".epub"] = (FileKindCategory.Document, "application/epub+zip"),
        [".html"] = (FileKindCategory.Code, "text/html"),
        [".htm"] = (FileKindCategory.Code, "text/html"),
        [".css"] = (FileKindCategory.Code, "text/css"),
        [".js"] = (FileKindCategory.Code, "text/javascript"),
        [".mjs"] = (FileKindCategory.Code, "text/javascript"),
        [".ts"] = (FileKindCategory.Code, "text/typescript"),
        [".json"] = (FileKindCategory.Code, "application/json"),
        [".xml"] = (FileKindCategory.Code, "application/xml"),
        [".yaml"] = (FileKindCategory.Code, "application/yaml"),
        [".yml"] = (FileKindCategory.Code, "application/yaml"),
        [".toml"] = (FileKindCategory.Code, "application/toml"),
        [".ini"] = (FileKindCategory.Code, null),
        [".cs"] = (FileKindCategory.Code, "text/x-csharp"),
        [".c"] = (FileKindCategory.Code, "text/x-c"),
        [".h"] = (FileKindCategory.Code, "text/x-c"),
        [".cpp"] = (FileKindCategory.Code, "text/x-c++"),
        [".hpp"] = (FileKindCategory.Code, "text/x-c++"),
        [".py"] = (FileKindCategory.Code, "text/x-python"),
        [".java"] = (FileKindCategory.Code, "text/x-java"),
        [".go"] = (FileKindCategory.Code, "text/x-go"),
        [".rs"] = (FileKindCategory.Code, "text/x-rust"),
        [".sh"] = (FileKindCategory.Code, "application/x-sh"),
        [".ps1"] = (FileKindCategory.Code, null),
        [".bat"] = (FileKindCategory.Code, null),
        [".cmd"] = (FileKindCategory.Code, null),
        [".sql"] = (FileKindCategory.Code, "application/sql"),
        [".zip"] = (FileKindCategory.Archive, "application/zip"),
        [".7z"] = (FileKindCategory.Archive, "application/x-7z-compressed"),
        [".rar"] = (FileKindCategory.Archive, "application/vnd.rar"),
        [".tar"] = (FileKindCategory.Archive, "application/x-tar"),
        [".gz"] = (FileKindCategory.Archive, "application/gzip"),
        [".tgz"] = (FileKindCategory.Archive, "application/gzip"),
        [".bz2"] = (FileKindCategory.Archive, "application/x-bzip2"),
        [".xz"] = (FileKindCategory.Archive, "application/x-xz"),
        [".cab"] = (FileKindCategory.Archive, "application/vnd.ms-cab-compressed"),
        [".exe"] = (FileKindCategory.Application, "application/vnd.microsoft.portable-executable"),
        [".dll"] = (FileKindCategory.Application, "application/vnd.microsoft.portable-executable"),
        [".msi"] = (FileKindCategory.Application, "application/x-msi"),
        [".msix"] = (FileKindCategory.Application, null),
        [".appx"] = (FileKindCategory.Application, null),
        [".com"] = (FileKindCategory.Application, null),
        [".scr"] = (FileKindCategory.Application, null),
        [".lnk"] = (FileKindCategory.Shortcut, null),
        [".url"] = (FileKindCategory.Shortcut, null),
        [".ttf"] = (FileKindCategory.Font, "font/ttf"),
        [".otf"] = (FileKindCategory.Font, "font/otf"),
        [".ttc"] = (FileKindCategory.Font, "font/collection"),
        [".woff"] = (FileKindCategory.Font, "font/woff"),
        [".woff2"] = (FileKindCategory.Font, "font/woff2"),
        [".iso"] = (FileKindCategory.DiskImage, "application/x-iso9660-image"),
        [".img"] = (FileKindCategory.DiskImage, null),
        [".vhd"] = (FileKindCategory.DiskImage, null),
        [".vhdx"] = (FileKindCategory.DiskImage, null),
    };

    /// <summary>
    /// Reads the facts of <paramref name="path"/> (never its contents). <paramref name="format"/> is the format
    /// already detected from the file's first bytes, if any.
    /// </summary>
    public static UniversalFileInfo Read(string path, FileFormat format = FileFormat.Unknown)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (name.Length == 0)
        {
            name = path; // a drive root
        }

        if (PreviewSafetyPolicy.IsDevicePath(path))
        {
            return new UniversalFileInfo(name, string.Empty, false, false, null, null, null, 0, FileKindCategory.Other, null, null);
        }

        try
        {
            if (Directory.Exists(path))
            {
                var folder = new DirectoryInfo(path);
                return new UniversalFileInfo(name, string.Empty, true, true, null, folder.CreationTimeUtc, folder.LastWriteTimeUtc, folder.Attributes, FileKindCategory.Folder, null, null);
            }

            var file = new FileInfo(path);
            string extension = file.Extension;
            if (!file.Exists)
            {
                return new UniversalFileInfo(name, extension, false, false, null, null, null, 0, CategoryFor(extension, format), MimeTypeFor(extension, format), null);
            }

            return new UniversalFileInfo(
                name,
                extension,
                true,
                false,
                file.Length,
                file.CreationTimeUtc,
                file.LastWriteTimeUtc,
                file.Attributes,
                CategoryFor(extension, format),
                MimeTypeFor(extension, format),
                ReadMarkOfTheWeb(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new UniversalFileInfo(name, Path.GetExtension(path), false, false, null, null, null, 0, FileKindCategory.Other, null, null);
        }
    }

    /// <summary>The icon category from the detected format, else from the extension.</summary>
    public static FileKindCategory CategoryFor(string extension, FileFormat format = FileFormat.Unknown)
    {
        if (format == FileFormat.Pdf)
        {
            return FileKindCategory.Pdf;
        }

        if (FileFormatKinds.IsImage(format))
        {
            return FileKindCategory.Image;
        }

        if (FileFormatKinds.IsVideo(format))
        {
            return FileKindCategory.Video;
        }

        if (FileFormatKinds.IsAudio(format))
        {
            return FileKindCategory.Audio;
        }

        if (FileFormatKinds.IsArchive(format))
        {
            return FileKindCategory.Archive;
        }

        FileFormat byExtension = FileFormatDetector.FromExtension(extension ?? string.Empty);
        if (byExtension != FileFormat.Unknown)
        {
            return CategoryFor(string.Empty, byExtension);
        }

        return extension is not null && ByExtension.TryGetValue(extension, out var known) ? known.Category : FileKindCategory.Other;
    }

    /// <summary>The MIME type Mavue knows for the format or extension, or null (the host may ask Windows).</summary>
    public static string? MimeTypeFor(string extension, FileFormat format = FileFormat.Unknown)
    {
        string? fromFormat = (format == FileFormat.Unknown ? FileFormatDetector.FromExtension(extension ?? string.Empty) : format) switch
        {
            FileFormat.Pdf => "application/pdf",
            FileFormat.Zip => "application/zip",
            FileFormat.Jpeg => "image/jpeg",
            FileFormat.Png => "image/png",
            FileFormat.Gif => "image/gif",
            FileFormat.Bmp => "image/bmp",
            FileFormat.Tiff => "image/tiff",
            FileFormat.WebP => "image/webp",
            FileFormat.Ico => "image/vnd.microsoft.icon",
            FileFormat.Heif => "image/heif",
            FileFormat.Avif => "image/avif",
            FileFormat.Jpeg2000 => "image/jp2",
            FileFormat.JpegXl => "image/jxl",
            FileFormat.Svg => "image/svg+xml",
            FileFormat.Mp4 => "video/mp4",
            FileFormat.QuickTime => "video/quicktime",
            FileFormat.Matroska => "video/x-matroska",
            FileFormat.WebM => "video/webm",
            FileFormat.Avi => "video/x-msvideo",
            FileFormat.WindowsMediaVideo => "video/x-ms-wmv",
            FileFormat.MpegTransportStream => "video/mp2t",
            FileFormat.MpegProgramStream => "video/mpeg",
            FileFormat.OggVideo => "video/ogg",
            FileFormat.Mp3 => "audio/mpeg",
            FileFormat.Aac => "audio/aac",
            FileFormat.M4a => "audio/mp4",
            FileFormat.Wav => "audio/wav",
            FileFormat.Flac => "audio/flac",
            FileFormat.OggAudio => "audio/ogg",
            FileFormat.WindowsMediaAudio => "audio/x-ms-wma",
            _ => null,
        };
        return fromFormat ?? (extension is not null && ByExtension.TryGetValue(extension, out var known) ? known.Mime : null);
    }

    /// <summary>The file's Mark-of-the-Web, or null when it has none (or it cannot be read).</summary>
    public static MarkOfTheWeb? ReadMarkOfTheWeb(string path)
    {
        try
        {
            using var stream = new FileStream(path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxZoneIdentifierBytes)
            {
                return null;
            }

            using var reader = new StreamReader(stream);
            return ParseZoneIdentifier(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // no stream (the usual case), or the file system has none
        }
    }

    /// <summary>
    /// Parses a Zone.Identifier stream ("[ZoneTransfer]" with ZoneId, HostUrl, ReferrerUrl). Returns null when there
    /// is no valid ZoneId. Keeps only the source's host name.
    /// </summary>
    public static MarkOfTheWeb? ParseZoneIdentifier(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int? zone = null;
        string? host = null;
        string? referrer = null;
        bool inSection = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            if (line.StartsWith('['))
            {
                inSection = line.Equals("[ZoneTransfer]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (!inSection || equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Equals("ZoneId", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id is >= 0 and <= 4)
            {
                zone = id;
            }
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase))
            {
                host = HostOf(value);
            }
            else if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase))
            {
                referrer = HostOf(value);
            }
        }

        return zone is { } z ? new MarkOfTheWeb(z, referrer ?? host) : null;
    }

    /// <summary>
    /// Counts the entries of a folder, stopping at <paramref name="limit"/> (the card then shows "limit+").
    /// Returns null when the folder cannot be read.
    /// </summary>
    public static int? CountEntries(string folder, int limit, CancellationToken cancellation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        try
        {
            int count = 0;
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
            foreach (string _ in Directory.EnumerateFileSystemEntries(folder, "*", options))
            {
                if (++count >= limit || (count % 256 == 0 && cancellation.IsCancellationRequested))
                {
                    break;
                }
            }

            cancellation.ThrowIfCancellationRequested();
            return count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "ftp" && uri.Host.Length > 0 ? uri.Host : null;
}

/// <summary>Dragging the previewed file out of Quick View (docs/ARCHITECTURE.md §4.7.8; SPEC §3.1 requirements 4 and 7).</summary>
public static class DragOutPolicy
{
    /// <summary>Only regular files can be dragged (not folders, devices or missing files).</summary>
    public static bool CanDrag(FileFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.Access != PreviewAccess.NotAFile;
    }

    /// <summary>
    /// Quick View never moves the previewed file: the drop target may only copy it (also when the target would
    /// normally move, such as another folder on the same drive, or the user holds Shift).
    /// </summary>
    public static bool AllowsMove => false;
}
