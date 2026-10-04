using System.Xml.Linq;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Shell;

/// <summary>
/// AppxManifest.xml of the full MSIX package (Mavue + the Quick View host in one package). Layout of the package:
/// <c>Mavue\</c> (the release folder of tools/build-release.ps1: Mavue.exe and Mavue.QuickView.Host.exe, published
/// self-contained into one folder (their shared .NET / Windows App SDK files are identical), with
/// Mavue.Shell.Preview.dll, pdfium.dll, Mavue.Shell.Native.dll and licenses\), <c>Assets\</c> (logos).
/// What the unpackaged build writes to HKCU at <c>--register</c> is declared here instead (docs/PACKAGING.md):
/// file type associations (Open with / Default apps), the preview handler and thumbnail provider (desktop2 handlers on
/// those associations, COM classes in a surrogate), the Windows 11 context-menu command, and Quick View at sign-in
/// (StartupTask). The file type lists are the same as the unpackaged registration's.
/// </summary>
public static class MsixPackageManifest
{
    public const string PackageName = "Mavue";
    public const string AppFolder = "Mavue";

    /// <summary>The Quick View host lives in the same folder as Mavue.exe (one self-contained runtime for both).</summary>
    public const string QuickViewFolder = AppFolder;
    public const string StartupTaskId = "MavueQuickView";

    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Desktop2 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    /// <summary>File type association groups: (name, display name, extensions, thumbnail provider too).</summary>
    public static IReadOnlyList<(string Name, string DisplayName, IReadOnlyList<string> Extensions, bool Thumbnails)> Associations { get; } = BuildAssociations();

    private static List<(string, string, IReadOnlyList<string>, bool)> BuildAssociations()
    {
        string[] Of(Func<FileFormat, bool> include) =>
            ViewerFormats.All.Where(include).SelectMany(FileFormatDetector.ExtensionsOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();

        // Thumbnails only where Windows has none of its own (as in the unpackaged registration): PDF and SVG.
        return
        [
            ("mavue.pdf", "PDF", Of(f => f == FileFormat.Pdf), true),
            ("mavue.svg", "SVG", Of(f => f == FileFormat.Svg), true),
            ("mavue.image", "Image", Of(f => f != FileFormat.Svg && f != FileFormat.Pdf && !FileFormatKinds.IsMedia(f)), false),
            ("mavue.video", "Video", Of(FileFormatKinds.IsVideo), false),
            ("mavue.audio", "Audio", Of(FileFormatKinds.IsAudio), false),
        ];
    }

    /// <param name="publisher">The signing certificate's subject, e.g. "CN=Mavue Dev".</param>
    /// <param name="architecture">"x64" or "arm64".</param>
    public static string Create(string publisher, Version version, string architecture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        ArgumentNullException.ThrowIfNull(version);
        if (architecture is not ("x64" or "arm64"))
        {
            throw new ArgumentException("Architecture must be x64 or arm64.", nameof(architecture));
        }

        const string Logo150 = @"Assets\Square150x150Logo.png";
        const string Logo44 = @"Assets\Square44x44Logo.png";
        string previewDll = $@"{AppFolder}\{ShellHandlerRegistration.DllName}";
        string preview = ShellHandlerRegistration.PreviewHandlerClsid.ToString("D").ToUpperInvariant();
        string thumbnail = ShellHandlerRegistration.ThumbnailProviderClsid.ToString("D").ToUpperInvariant();

        XElement Association((string Name, string DisplayName, IReadOnlyList<string> Extensions, bool Thumbnails) group)
        {
            var element = new XElement(Uap + "FileTypeAssociation", new XAttribute("Name", group.Name),
                new XElement(Uap + "DisplayName", group.DisplayName),
                new XElement(Uap + "Logo", Logo44),
                new XElement(Uap + "SupportedFileTypes", group.Extensions.Select(e => new XElement(Uap + "FileType", e))));
            if (group.Thumbnails)
            {
                element.Add(new XElement(Desktop2 + "ThumbnailHandler", new XAttribute("Clsid", thumbnail)));
            }

            element.Add(new XElement(Desktop2 + "DesktopPreviewHandler", new XAttribute("Clsid", preview)));
            return new XElement(Uap + "Extension", new XAttribute("Category", "windows.fileTypeAssociation"), element);
        }

        XElement Visuals(string name, bool listed) => new(Uap + "VisualElements",
            new XAttribute("DisplayName", name),
            new XAttribute("Description", name),
            new XAttribute("BackgroundColor", "transparent"),
            new XAttribute("Square150x150Logo", Logo150),
            new XAttribute("Square44x44Logo", Logo44),
            listed ? null : new XAttribute("AppListEntry", "none"));

        var mavue = new XElement(Foundation + "Application",
            new XAttribute("Id", "Mavue"),
            new XAttribute("Executable", $@"{AppFolder}\{AppRegistration.ExecutableName}"),
            new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
            Visuals("Mavue", listed: true),
            new XElement(Foundation + "Extensions",
                Associations.Select(Association),
                new XElement(Com + "Extension", new XAttribute("Category", "windows.comServer"),
                    new XElement(Com + "ComServer",
                        new XElement(Com + "SurrogateServer", new XAttribute("DisplayName", "Mavue Preview"),
                            new XElement(Com + "Class", new XAttribute("Id", preview), new XAttribute("Path", previewDll), new XAttribute("ThreadingModel", "STA"))),
                        new XElement(Com + "SurrogateServer", new XAttribute("DisplayName", "Mavue Thumbnails"),
                            new XElement(Com + "Class", new XAttribute("Id", thumbnail), new XAttribute("Path", previewDll), new XAttribute("ThreadingModel", "STA")))))));

        var quickView = new XElement(Foundation + "Application",
            new XAttribute("Id", "QuickView"),
            new XAttribute("Executable", $@"{QuickViewFolder}\{IdentityPackageManifest.HostExecutable}"),
            new XAttribute("EntryPoint", "Windows.FullTrustApplication"),
            Visuals("Mavue Quick View", listed: false),
            new XElement(Foundation + "Extensions",
                new XElement(Desktop + "Extension", new XAttribute("Category", "windows.startupTask"),
                    new XElement(Desktop + "StartupTask", new XAttribute("TaskId", StartupTaskId), new XAttribute("Enabled", "true"), new XAttribute("DisplayName", "Mavue Quick View"))),
                new XElement(Com + "Extension", new XAttribute("Category", "windows.comServer"),
                    new XElement(Com + "ComServer",
                        new XElement(Com + "SurrogateServer", new XAttribute("DisplayName", "Mavue Quick View"),
                            new XElement(Com + "Class",
                                new XAttribute("Id", IdentityPackageManifest.CommandClsid),
                                new XAttribute("Path", $@"{QuickViewFolder}\{IdentityPackageManifest.CommandDll}"),
                                new XAttribute("ThreadingModel", "STA"))))),
                new XElement(Desktop4 + "Extension", new XAttribute("Category", "windows.fileExplorerContextMenus"),
                    new XElement(Desktop4 + "FileExplorerContextMenus",
                        QuickViewShellRegistration.Extensions.Select(e => new XElement(Desktop5 + "ItemType", new XAttribute("Type", e),
                            new XElement(Desktop5 + "Verb", new XAttribute("Id", "MavueQuickView"), new XAttribute("Clsid", IdentityPackageManifest.CommandClsid))))))));

        var package = new XElement(Foundation + "Package",
            new XAttribute(XNamespace.Xmlns + "uap", Uap),
            new XAttribute(XNamespace.Xmlns + "uap10", Uap10),
            new XAttribute(XNamespace.Xmlns + "rescap", Rescap),
            new XAttribute(XNamespace.Xmlns + "com", Com),
            new XAttribute(XNamespace.Xmlns + "desktop", Desktop),
            new XAttribute(XNamespace.Xmlns + "desktop2", Desktop2),
            new XAttribute(XNamespace.Xmlns + "desktop4", Desktop4),
            new XAttribute(XNamespace.Xmlns + "desktop5", Desktop5),
            new XAttribute("IgnorableNamespaces", "uap uap10 rescap com desktop desktop2 desktop4 desktop5"),
            new XElement(Foundation + "Identity",
                new XAttribute("Name", PackageName),
                new XAttribute("Publisher", publisher),
                new XAttribute("Version", version.ToString(4)),
                new XAttribute("ProcessorArchitecture", architecture)),
            new XElement(Foundation + "Properties",
                new XElement(Foundation + "DisplayName", "Mavue"),
                new XElement(Foundation + "PublisherDisplayName", "Mavue"),
                new XElement(Foundation + "Logo", @"Assets\StoreLogo.png")),
            new XElement(Foundation + "Resources",
                new XElement(Foundation + "Resource", new XAttribute("Language", "ja-jp")),
                new XElement(Foundation + "Resource", new XAttribute("Language", "en-us"))),
            new XElement(Foundation + "Dependencies",
                new XElement(Foundation + "TargetDeviceFamily",
                    new XAttribute("Name", "Windows.Desktop"),
                    new XAttribute("MinVersion", "10.0.19041.0"),
                    new XAttribute("MaxVersionTested", "10.0.26100.0"))),
            new XElement(Foundation + "Capabilities", new XElement(Rescap + "Capability", new XAttribute("Name", "runFullTrust"))),
            new XElement(Foundation + "Applications", mavue, quickView));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), package).ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
