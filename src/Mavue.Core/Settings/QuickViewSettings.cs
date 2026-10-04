using System.Text.Json;
using System.Text.Json.Serialization;
using Mavue.Core.IO;
using Mavue.Core.Viewing;

namespace Mavue.Core.Settings;

/// <summary>
/// Persisted Quick View preferences (%LOCALAPPDATA%\Mavue\QuickView\settings.json). Unknown or
/// malformed files fall back to defaults instead of failing; saving goes through SafeFileWriter so a
/// crash never leaves a half-written file.
/// </summary>
public sealed record QuickViewSettings
{
    // Settable rather than init-only: System.Text.Json source generation assigns every init-only property, so a
    // property missing from the file would become default(T) instead of keeping its initializer below.
    /// <summary>Current file format version.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    [JsonConverter(typeof(JsonStringEnumConverter<ImageScaleMode>))]
    public ImageScaleMode ImageScale { get; set; } = ImageScaleMode.FitNoUpscale;

    /// <summary>How PDF pages are arranged in Quick View (the last choice is kept).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PdfLayoutMode>))]
    public PdfLayoutMode PdfLayout { get; set; } = PdfLayoutMode.Continuous;

    /// <summary>The PDF sidebar (pages, contents, results) is open.</summary>
    public bool ShowPdfSidebar { get; set; }

    /// <summary>Default location for the current user.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "QuickView", "settings.json");

    /// <summary>Loads settings; returns defaults if the file is missing, unreadable or invalid.</summary>
    public static QuickViewSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (!File.Exists(path))
            {
                return new QuickViewSettings();
            }

            using FileStream stream = File.OpenRead(path);
            QuickViewSettings? loaded = JsonSerializer.Deserialize(stream, QuickViewSettingsJson.Default.QuickViewSettings);
            if (loaded is null || !Enum.IsDefined(loaded.ImageScale))
            {
                return new QuickViewSettings();
            }

            return Enum.IsDefined(loaded.PdfLayout) ? loaded : loaded with { PdfLayout = PdfLayoutMode.Continuous };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new QuickViewSettings();
        }
    }

    /// <summary>Saves atomically (temporary file, then replace).</summary>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return SafeFileWriter.WriteAsync(
            path,
            (stream, ct) => JsonSerializer.SerializeAsync(stream, this, QuickViewSettingsJson.Default.QuickViewSettings, ct),
            cancellationToken: cancellationToken);
    }
}

/// <summary>Source-generated JSON metadata (trimming/NativeAOT friendly).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(QuickViewSettings))]
internal sealed partial class QuickViewSettingsJson : JsonSerializerContext
{
}
