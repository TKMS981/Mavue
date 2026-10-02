using System.Text.Json.Serialization;

namespace Mavue.Core.Ipc;

/// <summary>
/// Asks the resident Quick View process to preview files (sent by <c>Mavue.QuickView.Host.exe --quickview</c>,
/// which Explorer starts for the "Mavue Quick View" context-menu command). docs/ARCHITECTURE.md §9.
/// </summary>
/// <param name="Version">Contract version; a receiver rejects versions it does not know.</param>
/// <param name="Paths">Fully qualified file paths, in the order given on the command line.</param>
/// <param name="ForegroundWindow">
/// The window that was in the foreground when the request was made (normally the Explorer window the
/// command was invoked from), so Quick View can follow that window's selection. 0 if unknown.
/// </param>
public sealed record QuickViewRequest(int Version, IReadOnlyList<string> Paths, long ForegroundWindow)
{
    public const int CurrentVersion = 1;

    /// <summary>More paths than this are refused (Explorer passes at most 100 items to a command verb).</summary>
    public const int MaxPaths = 1000;

    /// <summary>Longest path accepted (the Windows extended-length limit).</summary>
    public const int MaxPathLength = 32767;

    /// <summary>Null if the request is acceptable; otherwise a short reason (never contains a path).</summary>
    public string? Validate()
    {
        if (Version != CurrentVersion)
        {
            return "unsupported-version";
        }

        if (Paths is null || Paths.Count == 0)
        {
            return "no-paths";
        }

        if (Paths.Count > MaxPaths)
        {
            return "too-many-paths";
        }

        foreach (string path in Paths)
        {
            if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength || path.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(path))
            {
                return "invalid-path";
            }
        }

        return null;
    }
}

/// <summary>Reply to a <see cref="QuickViewRequest"/>.</summary>
/// <param name="Accepted">True when the request was queued for display.</param>
/// <param name="Error">Short machine-readable reason when not accepted.</param>
public sealed record QuickViewResponse(int Version, bool Accepted, string? Error);

[JsonSerializable(typeof(QuickViewRequest))]
[JsonSerializable(typeof(QuickViewResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class IpcJson : JsonSerializerContext;
