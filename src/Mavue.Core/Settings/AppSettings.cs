using System.Text.Json;
using System.Text.Json.Serialization;
using Mavue.Core.IO;
using Mavue.Core.Viewing;

namespace Mavue.Core.Settings;

/// <summary>Light or dark appearance of the main window.</summary>
public enum AppTheme
{
    /// <summary>Follow the Windows setting.</summary>
    System = 0,
    Light,
    Dark,
}

/// <summary>Where the main window was, restored at the next start.</summary>
/// <param name="X">Left edge in screen pixels.</param>
/// <param name="Y">Top edge in screen pixels.</param>
/// <param name="Width">Outer width in pixels.</param>
/// <param name="Height">Outer height in pixels.</param>
/// <param name="Maximized">The window was maximized.</param>
public sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized);

/// <summary>
/// Preferences of the main application (%LOCALAPPDATA%\Mavue\settings.json). Missing, unreadable or invalid files
/// give defaults; saving goes through <see cref="SafeFileWriter"/> so a crash never leaves a half-written file.
/// Several Mavue windows may run at once: callers re-read before changing a value (<see cref="Update"/>).
/// </summary>
public sealed record AppSettings
{
    // Settable rather than init-only: System.Text.Json source generation assigns every init-only property, so a
    // property missing from the file would become default(T) instead of keeping its initializer below.
    public const int CurrentVersion = 1;

    /// <summary>Most recently opened files kept in the list.</summary>
    public const int MaxRecentFiles = 15;

    public int Version { get; set; } = CurrentVersion;

    [JsonConverter(typeof(JsonStringEnumConverter<AppTheme>))]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>How an opened image or page is sized at first (fit without enlarging, or actual size).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ImageScaleMode>))]
    public ImageScaleMode DefaultScale { get; set; } = ImageScaleMode.FitNoUpscale;

    /// <summary>The information pane is open.</summary>
    public bool ShowInfoPane { get; set; }

    /// <summary>The page thumbnails of a PDF are shown.</summary>
    public bool ShowPageThumbnails { get; set; } = true;

    /// <summary>How PDF pages are arranged (single page, continuous, two pages); the last choice is kept.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<PdfLayoutMode>))]
    public PdfLayoutMode PdfLayout { get; set; } = PdfLayoutMode.Continuous;

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<string> RecentFiles { get; set; } = [];

    public WindowPlacement? Window { get; set; }

    /// <summary>Default location for the current user.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "settings.json");

    /// <summary>Loads settings; returns defaults if the file is missing, unreadable or invalid.</summary>
    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            AppSettings? loaded = JsonSerializer.Deserialize(stream, AppSettingsJson.Default.AppSettings);
            return loaded is null ? new AppSettings() : loaded.Sanitized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves atomically (temporary file, then replace).</summary>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return SafeFileWriter.WriteAsync(
            path,
            (stream, ct) => JsonSerializer.SerializeAsync(stream, this, AppSettingsJson.Default.AppSettings, ct),
            cancellationToken: cancellationToken);
    }

    /// <summary>Re-reads the file, applies <paramref name="change"/> and saves; returns the saved settings.</summary>
    public static async Task<AppSettings> Update(string path, Func<AppSettings, AppSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings updated = change(Load(path)).Sanitized();
        await updated.SaveAsync(path, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary><paramref name="file"/> moved to the front of the recent files (case-insensitive, at most <see cref="MaxRecentFiles"/>).</summary>
    public AppSettings WithRecentFile(string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        return this with
        {
            RecentFiles = [file, .. RecentFiles.Where(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)).Take(MaxRecentFiles - 1)],
        };
    }

    /// <summary>Unknown enum values and bad entries replaced by defaults.</summary>
    public AppSettings Sanitized() => this with
    {
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.System,
        DefaultScale = Enum.IsDefined(DefaultScale) ? DefaultScale : ImageScaleMode.FitNoUpscale,
        PdfLayout = Enum.IsDefined(PdfLayout) ? PdfLayout : PdfLayoutMode.Continuous,
        RecentFiles = (RecentFiles ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxRecentFiles)
            .ToArray(),
        Window = Window is { Width: > 0, Height: > 0 } ? Window : null,
    };
}

/// <summary>Source-generated JSON metadata (trimming/NativeAOT friendly).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext
{
}
