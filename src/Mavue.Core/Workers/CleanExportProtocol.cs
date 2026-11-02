using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mavue.Core.Workers;

/// <summary>
/// A converted export without metadata, run in the worker process (ARCHITECTURE §4.7.15): one JSON line on its
/// standard input. The worker writes <paramref name="Destination"/> (a temporary file the host chose); the host
/// verifies it and gives it its final name.
/// </summary>
/// <param name="Format">"Original", "Jpeg" or "Png".</param>
public sealed record CleanExportRequest(
    string Source,
    string Destination,
    bool RemoveLocation,
    bool RemoveCaptureTime,
    bool RemoveCameraDetails,
    bool RemoveAuthorCopyright,
    bool KeepColorProfile,
    string Format,
    int JpegQuality,
    long MaxPixels);

/// <summary>The worker's answer: "done" with what was written, or "error" with a code.</summary>
public sealed record CleanExportMessage
{
    public const string DoneType = "done";
    public const string ErrorType = "error";

    public required string Type { get; init; }

    public string? Container { get; init; }
    public bool? Converted { get; init; }
    public string? Format { get; init; }
    public long? RemovedBytes { get; init; }
    public bool? WroteExif { get; init; }
    public double? Milliseconds { get; init; }

    /// <summary>"unsupported" (no codec), "too-large", "unreadable", "failed".</summary>
    public string? Code { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, CleanExportJson.Default.CleanExportMessage);

    public static CleanExportMessage? FromJson(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, CleanExportJson.Default.CleanExportMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CleanExportRequest))]
[JsonSerializable(typeof(CleanExportMessage))]
public sealed partial class CleanExportJson : JsonSerializerContext
{
}
