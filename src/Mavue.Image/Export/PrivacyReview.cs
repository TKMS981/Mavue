using Mavue.Image.Wic;
using Mavue.Metadata;

namespace Mavue.Image.Export;

/// <summary>
/// What an image reveals and how a copy without it can be made — the first step of Quick View's check before sharing
/// and of the main app's "Export without metadata" (ARCHITECTURE §4.7.13, §4.7.15), so both show the same findings and
/// the same guidance.
/// </summary>
/// <param name="Metadata">Mavue's own reading (<see cref="MetadataContainer.Unsupported"/> for types it does not read).</param>
/// <param name="Findings">What the metadata reveals; for unread types, what the fallback (Windows' property system) knows.</param>
/// <param name="Plan">The type of the copy: the original's (no re-encoding) or a converted JPEG/PNG.</param>
/// <param name="CanDecode">Windows can decode the image, so a converted copy is possible (false: a codec extension is missing or the file is damaged).</param>
public sealed record PrivacyReview(ImageMetadata Metadata, IReadOnlyList<PrivacyFinding> Findings, CleanExportPlan Plan, bool CanDecode)
{
    /// <summary>Something private (anything but "kept") was found.</summary>
    public bool HasPrivateData => Findings.Any(f => f.Level != PrivacyLevel.Kept);

    /// <summary>No copy can be made at all: the type needs a conversion and Windows cannot decode it.</summary>
    public bool CannotCopy => !Plan.CanKeepFormat && !CanDecode;

    /// <summary>
    /// Reads <paramref name="path"/> off the calling thread. Never throws for unreadable files: they give an empty,
    /// unsupported review that cannot be copied.
    /// </summary>
    /// <param name="unsupportedFindings">Findings for types Mavue does not read itself (e.g. from Windows' property system); null for none.</param>
    public static async Task<PrivacyReview> ReadAsync(
        string path, Func<string, CancellationToken, Task<IReadOnlyList<PrivacyFinding>>>? unsupportedFindings, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ImageMetadata metadata;
        IReadOnlyList<PrivacyFinding> findings = [];
        try
        {
            metadata = await Task.Run(() => ImageMetadataReader.Read(path, findTrailer: true), cancellation);
            if (metadata.Container == MetadataContainer.Unsupported && unsupportedFindings is not null)
            {
                findings = await unsupportedFindings(path, cancellation);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            metadata = ImageMetadata.Unsupported;
        }

        bool canStrip = MetadataStripper.CanStrip(metadata.Container);
        var plan = new CleanExportPlan(metadata.Container, canStrip, Path.GetExtension(path).ToLowerInvariant());

        // Header only (no pixels): whether a converted copy can be made at all.
        bool canDecode = await Task.Run(() => WicPreviewDecoder.TryReadDimensions(path) is not null, cancellation);
        if (metadata.Container != MetadataContainer.Unsupported)
        {
            findings = PrivacyCheck.Review(metadata);
        }

        return new PrivacyReview(metadata, findings, plan, canDecode);
    }

    /// <summary>Segoe Fluent Icons glyph for a finding's level (the colour is the app's).</summary>
    public static string Glyph(PrivacyLevel level) => level switch
    {
        PrivacyLevel.High or PrivacyLevel.Medium => "", // warning
        PrivacyLevel.Kept => "",                        // check mark
        _ => "",                                        // info
    };
}
