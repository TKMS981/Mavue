using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mavue.Core.Similarity;

/// <summary>What the Quick View host asks the search worker (one JSON line on the worker's standard input).</summary>
/// <param name="Target">The image to find similar images of.</param>
/// <param name="Folder">Where to look.</param>
/// <param name="IncludeSubfolders">Also look in subfolders (bounded by <paramref name="MaxFiles"/>).</param>
/// <param name="MaxFiles">At most this many files are examined.</param>
/// <param name="Threshold">Results need at least this similarity (percent).</param>
/// <param name="MaxResults">At most this many results are sent (best first).</param>
/// <param name="ThumbnailSize">Results carry a thumbnail at most this many pixels wide and high.</param>
/// <param name="CachePath">The signature cache file.</param>
/// <param name="Parallelism">Files decoded at the same time.</param>
/// <param name="DetectRotations">Also compare with the target turned and mirrored (8 orientations).</param>
public sealed record SimilarSearchRequest(
    string Target,
    string Folder,
    bool IncludeSubfolders,
    int MaxFiles,
    int Threshold,
    int MaxResults,
    int ThumbnailSize,
    string CachePath,
    int Parallelism,
    bool DetectRotations = false);

/// <summary>Stages of a search, reported in progress messages.</summary>
public enum SimilarSearchStage
{
    Listing,

    /// <summary>Stage 1: coarse signatures of every file (cached).</summary>
    Coarse,

    /// <summary>Stage 2: fine signatures of the candidates of stage 1.</summary>
    Fine,

    /// <summary>Thumbnails of the results.</summary>
    Thumbnails,
}

/// <summary>One line from the worker: progress, a result, the end, or an error.</summary>
public sealed record SimilarSearchMessage
{
    public const string ProgressType = "progress";
    public const string ResultType = "result";
    public const string DoneType = "done";
    public const string ErrorType = "error";

    public required string Type { get; init; }

    // progress
    public SimilarSearchStage? Stage { get; init; }
    public int? Done { get; init; }
    public int? Total { get; init; }
    public int? Candidates { get; init; }

    // result
    public string? Path { get; init; }
    public int? Similarity { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? ThumbnailWidth { get; init; }
    public int? ThumbnailHeight { get; init; }

    /// <summary>The result matches the target turned by this many degrees clockwise (0, 90, 180, 270).</summary>
    public int? Rotation { get; init; }

    /// <summary>The result matches the target mirrored (before the rotation).</summary>
    public bool? Mirrored { get; init; }

    /// <summary>BGRA pixels of the thumbnail, Base64.</summary>
    public string? Thumbnail { get; init; }

    // done
    public int? Examined { get; init; }
    public int? Skipped { get; init; }
    public int? Matches { get; init; }
    public int? CacheHits { get; init; }
    public double? Milliseconds { get; init; }
    public bool? Truncated { get; init; }

    // error
    public string? Code { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, SimilarSearchJson.Default.SimilarSearchMessage);

    public static SimilarSearchMessage? FromJson(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, SimilarSearchJson.Default.SimilarSearchMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Source-generated JSON (trimming/NativeAOT friendly).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SimilarSearchRequest))]
[JsonSerializable(typeof(SimilarSearchMessage))]
public sealed partial class SimilarSearchJson : JsonSerializerContext
{
}
