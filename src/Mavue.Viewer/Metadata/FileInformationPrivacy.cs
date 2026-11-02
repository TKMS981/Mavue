using Mavue.Metadata;

namespace Mavue.Viewer.Metadata;

/// <summary>
/// Findings from what Windows' property system knows (<see cref="FileInformation"/>), for image types Mavue does not
/// read itself (such as JPEG XL): the fallback of <see cref="Image.Export.PrivacyReview.ReadAsync"/> in both apps.
/// </summary>
public static class FileInformationPrivacy
{
    public static async Task<IReadOnlyList<PrivacyFinding>> FindingsAsync(string path, CancellationToken cancellation)
    {
        IReadOnlyList<FileInfoItem> items = await FileInformation.ReadAsync(path, cancellation);
        var findings = new List<PrivacyFinding>();
        string? Get(FileInfoField field) => items.FirstOrDefault(i => i.Field == field)?.Value;
        if (Get(FileInfoField.Location) is { } location)
        {
            findings.Add(new(PrivacyKind.Location, PrivacyLevel.High, location));
        }

        if (Get(FileInfoField.DateTaken) is { } taken)
        {
            findings.Add(new(PrivacyKind.CaptureTime, PrivacyLevel.Medium, taken));
        }

        if (Get(FileInfoField.Camera) is { } camera)
        {
            findings.Add(new(PrivacyKind.CameraDetails, PrivacyLevel.Low, camera));
        }

        if ((Get(FileInfoField.Authors) ?? Get(FileInfoField.Copyright)) is { } author)
        {
            findings.Add(new(PrivacyKind.AuthorCopyright, PrivacyLevel.Medium, author));
        }

        if ((Get(FileInfoField.Title) ?? Get(FileInfoField.Comment) ?? Get(FileInfoField.Keywords)) is { } words)
        {
            findings.Add(new(PrivacyKind.Descriptions, PrivacyLevel.Medium, words));
        }

        return findings;
    }
}
