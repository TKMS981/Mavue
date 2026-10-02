using System.Text.Json;
using System.Text.Json.Serialization;
using Mavue.Core.IO;
using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Settings;

/// <summary>
/// Persisted Quick View preferences (%LOCALAPPDATA%\Mavue\QuickView\settings.json). Unknown or
/// malformed files fall back to defaults instead of failing; saving goes through SafeFileWriter so a
/// crash never leaves a half-written file.
/// </summary>
public sealed record QuickViewSettings
{
    /// <summary>Current file format version.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    [JsonConverter(typeof(JsonStringEnumConverter<ImageScaleMode>))]
    public ImageScaleMode ImageScale { get; init; } = ImageScaleMode.FitNoUpscale;

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
            return loaded is null || !Enum.IsDefined(loaded.ImageScale) ? new QuickViewSettings() : loaded;
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
