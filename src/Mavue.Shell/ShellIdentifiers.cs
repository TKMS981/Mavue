namespace Mavue.Shell;

/// <summary>
/// Well-known Windows Shell registration identifiers (documented by Microsoft) used when
/// registering Mavue's handlers. Mavue's own CLSIDs are defined with the native shell extension.
/// </summary>
public static class ShellIdentifiers
{
    /// <summary>ShellEx key under a file type for an IThumbnailProvider.</summary>
    public const string ThumbnailProviderHandlerKey = "{E357FCCD-A995-4576-B01F-234630154E96}";

    /// <summary>ShellEx key under a file type for an IPreviewHandler.</summary>
    public const string PreviewHandlerKey = "{8895B1C6-B41F-4C1C-A562-0D564250836F}";

    /// <summary>AppID of the system preview handler surrogate host (prevhost.exe).</summary>
    public const string PrevhostAppId = "{6D2B5079-2F0B-48DD-AB7F-97CEC514D30B}";
}
