using System.Globalization;
using Mavue.Core.Viewing;
using Windows.Storage;

namespace Mavue.Viewer.Metadata;

/// <summary>Kinds of information shown about a file; the host supplies the label in the user's language.</summary>
public enum FileInfoField
{
    Name,
    Folder,
    Type,
    Size,
    Created,
    Modified,
    Dimensions,
    Resolution,
    BitDepth,
    DateTaken,
    Camera,
    Lens,
    Exposure,
    Aperture,
    Iso,
    FocalLength,
    Location,
    Altitude,
    Title,
    Authors,
    Keywords,
    Comment,
    Copyright,
    Duration,
    FrameRate,
    Bitrate,
    SampleRate,
    Channels,
    Artist,
    Album,
    Year,
    Genre,
    Pages,
}

/// <summary>One line of file information.</summary>
public sealed record FileInfoItem(FileInfoField Field, string Value);

/// <summary>
/// Reads what Windows knows about a file through the Windows property system (the same data as File Explorer's
/// Details pane): dates, dimensions and resolution, camera settings (EXIF), location (GPS), document properties and
/// media details. Read only when asked (the information pane); values stay in this process and are never logged.
/// </summary>
public static class FileInformation
{
    private static readonly string[] Keys =
    [
        "System.ItemTypeText", "System.DateCreated", "System.DateModified",
        "System.Image.HorizontalSize", "System.Image.VerticalSize", "System.Image.HorizontalResolution", "System.Image.BitDepth",
        "System.Photo.DateTaken", "System.Photo.CameraManufacturer", "System.Photo.CameraModel", "System.Photo.LensModel",
        "System.Photo.ExposureTime", "System.Photo.FNumber", "System.Photo.ISOSpeed", "System.Photo.FocalLength",
        "System.GPS.LatitudeDecimal", "System.GPS.LongitudeDecimal", "System.GPS.Altitude",
        "System.Title", "System.Author", "System.Keywords", "System.Comment", "System.Copyright",
        "System.Media.Duration", "System.Video.FrameWidth", "System.Video.FrameHeight", "System.Video.FrameRate",
        "System.Video.EncodingBitrate", "System.Audio.EncodingBitrate", "System.Audio.SampleRate", "System.Audio.ChannelCount",
        "System.Music.Artist", "System.Music.AlbumTitle", "System.Media.Year", "System.Music.Genre",
        "System.Document.PageCount",
    ];

    /// <summary>Information about <paramref name="path"/>; fields Windows does not know are left out.</summary>
    public static async Task<IReadOnlyList<FileInfoItem>> ReadAsync(string path, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var items = new List<FileInfoItem>
        {
            new(FileInfoField.Name, Path.GetFileName(path)),
            new(FileInfoField.Folder, Path.GetDirectoryName(path) ?? string.Empty),
        };

        StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation);
        IDictionary<string, object?> values = await file.Properties.RetrievePropertiesAsync(Keys).AsTask(cancellation);
        var info = new FileInfo(path);
        T? Get<T>(string key) => values.TryGetValue(key, out object? value) && value is T typed ? typed : default;

        Add(items, FileInfoField.Type, Get<string>("System.ItemTypeText"));
        if (info.Exists)
        {
            items.Add(new(FileInfoField.Size, string.Create(CultureInfo.CurrentCulture, $"{ByteSizeText.Format(info.Length)} ({info.Length:N0})")));
        }

        Add(items, FileInfoField.Created, Date(Get<DateTimeOffset?>("System.DateCreated")));
        Add(items, FileInfoField.Modified, Date(Get<DateTimeOffset?>("System.DateModified")));

        uint width = Get<uint?>("System.Image.HorizontalSize") ?? Get<uint?>("System.Video.FrameWidth") ?? 0;
        uint height = Get<uint?>("System.Image.VerticalSize") ?? Get<uint?>("System.Video.FrameHeight") ?? 0;
        if (width > 0 && height > 0)
        {
            items.Add(new(FileInfoField.Dimensions, string.Create(CultureInfo.CurrentCulture, $"{width} × {height}")));
        }

        if (Get<double?>("System.Image.HorizontalResolution") is > 0 and var dpi)
        {
            items.Add(new(FileInfoField.Resolution, string.Create(CultureInfo.CurrentCulture, $"{dpi:0.#} dpi")));
        }

        if (Get<uint?>("System.Image.BitDepth") is > 0 and var depth)
        {
            items.Add(new(FileInfoField.BitDepth, depth.ToString(CultureInfo.CurrentCulture)));
        }

        Add(items, FileInfoField.DateTaken, Date(Get<DateTimeOffset?>("System.Photo.DateTaken")));
        string camera = string.Join(' ', new[] { Get<string>("System.Photo.CameraManufacturer"), Get<string>("System.Photo.CameraModel") }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        Add(items, FileInfoField.Camera, camera);
        Add(items, FileInfoField.Lens, Get<string>("System.Photo.LensModel"));
        if (Get<double?>("System.Photo.ExposureTime") is > 0 and var exposure)
        {
            items.Add(new(FileInfoField.Exposure, exposure < 1
                ? string.Create(CultureInfo.CurrentCulture, $"1/{Math.Round(1 / exposure):0} s")
                : string.Create(CultureInfo.CurrentCulture, $"{exposure:0.#} s")));
        }

        if (Get<double?>("System.Photo.FNumber") is > 0 and var aperture)
        {
            items.Add(new(FileInfoField.Aperture, string.Create(CultureInfo.CurrentCulture, $"f/{aperture:0.0}")));
        }

        if (Get<ushort?>("System.Photo.ISOSpeed") is > 0 and var iso)
        {
            items.Add(new(FileInfoField.Iso, string.Create(CultureInfo.CurrentCulture, $"ISO {iso}")));
        }

        if (Get<double?>("System.Photo.FocalLength") is > 0 and var focal)
        {
            items.Add(new(FileInfoField.FocalLength, string.Create(CultureInfo.CurrentCulture, $"{focal:0.#} mm")));
        }

        if (Get<double?>("System.GPS.LatitudeDecimal") is { } latitude && Get<double?>("System.GPS.LongitudeDecimal") is { } longitude &&
            double.IsFinite(latitude) && double.IsFinite(longitude))
        {
            items.Add(new(FileInfoField.Location, string.Create(CultureInfo.InvariantCulture, $"{latitude:0.000000}, {longitude:0.000000}")));
        }

        if (Get<double?>("System.GPS.Altitude") is { } altitude && double.IsFinite(altitude))
        {
            items.Add(new(FileInfoField.Altitude, string.Create(CultureInfo.CurrentCulture, $"{altitude:0.#} m")));
        }

        Add(items, FileInfoField.Title, Get<string>("System.Title"));
        Add(items, FileInfoField.Authors, Join(Get<string[]>("System.Author")));
        Add(items, FileInfoField.Keywords, Join(Get<string[]>("System.Keywords")));
        Add(items, FileInfoField.Comment, Get<string>("System.Comment"));
        Add(items, FileInfoField.Copyright, Get<string>("System.Copyright"));
        if (Get<int?>("System.Document.PageCount") is > 0 and var pages)
        {
            items.Add(new(FileInfoField.Pages, pages.ToString(CultureInfo.CurrentCulture)));
        }

        if (Get<ulong?>("System.Media.Duration") is > 0 and var duration)
        {
            items.Add(new(FileInfoField.Duration, MediaControlMath.FormatDuration(TimeSpan.FromTicks((long)Math.Min(duration, long.MaxValue)))));
        }

        if (Get<uint?>("System.Video.FrameRate") is > 0 and var frameRate)
        {
            items.Add(new(FileInfoField.FrameRate, string.Create(CultureInfo.CurrentCulture, $"{frameRate / 1000.0:0.##} fps")));
        }

        if ((Get<uint?>("System.Video.EncodingBitrate") ?? Get<uint?>("System.Audio.EncodingBitrate")) is > 0 and var bitrate)
        {
            items.Add(new(FileInfoField.Bitrate, string.Create(CultureInfo.CurrentCulture, $"{bitrate / 1000.0:0} kbps")));
        }

        if (Get<uint?>("System.Audio.SampleRate") is > 0 and var sampleRate)
        {
            items.Add(new(FileInfoField.SampleRate, string.Create(CultureInfo.CurrentCulture, $"{sampleRate / 1000.0:0.#} kHz")));
        }

        if (Get<uint?>("System.Audio.ChannelCount") is > 0 and var channels)
        {
            items.Add(new(FileInfoField.Channels, channels.ToString(CultureInfo.CurrentCulture)));
        }

        Add(items, FileInfoField.Artist, Join(Get<string[]>("System.Music.Artist")));
        Add(items, FileInfoField.Album, Get<string>("System.Music.AlbumTitle"));
        if (Get<uint?>("System.Media.Year") is > 0 and var year)
        {
            items.Add(new(FileInfoField.Year, year.ToString(CultureInfo.InvariantCulture)));
        }

        Add(items, FileInfoField.Genre, Join(Get<string[]>("System.Music.Genre")));
        return items;
    }

    private static void Add(List<FileInfoItem> items, FileInfoField field, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            items.Add(new(field, value.Trim()));
        }
    }

    private static string? Date(DateTimeOffset? value) =>
        value is { } date && date.Year > 1601 ? date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : null;

    private static string? Join(string[]? values) =>
        values is { Length: > 0 } ? string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v))) : null;
}
