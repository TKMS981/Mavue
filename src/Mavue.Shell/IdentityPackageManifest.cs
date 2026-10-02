using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mavue.Shell;

/// <summary>
/// Manifest of the identity ("sparse") package that puts "Mavue Quick View" in the Windows 11 File Explorer
/// context menu. The package contains only this manifest; it points at the Quick View host's folder
/// (external location), where the native command DLL lives. The host itself does not get package identity
/// (its exe has no msix element), so it keeps running exactly as before. See docs/WINDOWS-INTEGRATION.md §15
/// and Microsoft Learn: "Add a File Explorer context menu command to a packaged desktop app" and
/// "Grant package identity by packaging with external location manually".
/// </summary>
public static partial class IdentityPackageManifest
{
    /// <summary>Identity/Name of the package (also used to find it when unregistering).</summary>
    public const string PackageName = "Mavue.QuickView";

    /// <summary>CLSID of the native IExplorerCommand (native/Mavue.Shell.Native/src/ExplorerCommand.h).</summary>
    public const string CommandClsid = "3C34DBCC-2B28-45D3-A949-A83B0EC298EB";

    public const string CommandDll = "Mavue.Shell.Native.dll";
    public const string HostExecutable = "Mavue.QuickView.Host.exe";

    /// <summary>Logo relative to the host folder (Properties/Logo must resolve to an image there).</summary>
    public const string Logo = @"Assets\QuickViewLogo.png";

    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    /// <param name="publisher">Must equal the Subject of the signing certificate, e.g. "CN=Mavue Dev".</param>
    /// <param name="version">Four-part version; a registered version cannot be registered again.</param>
    public static string Create(string publisher, Version version, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(publisher) || !PublisherPattern().IsMatch(publisher))
        {
            throw new ArgumentException("Publisher must be a distinguished name such as \"CN=Mavue Dev\".", nameof(publisher));
        }

        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0 || version.Revision < 0)
        {
            throw new ArgumentException("Version must have four parts.", nameof(version));
        }

        if (extensions is null || extensions.Count == 0 || extensions.Any(e => !ExtensionPattern().IsMatch(e)))
        {
            throw new ArgumentException("Extensions must look like \".jpg\".", nameof(extensions));
        }

        var itemTypes = extensions.Select(e =>
            new XElement(Desktop5 + "ItemType",
                new XAttribute("Type", e),
                new XElement(Desktop5 + "Verb", new XAttribute("Id", "MavueQuickView"), new XAttribute("Clsid", CommandClsid))));

        var package = new XElement(Foundation + "Package",
            new XAttribute(XNamespace.Xmlns + "uap", Uap),
            new XAttribute(XNamespace.Xmlns + "uap10", Uap10),
            new XAttribute(XNamespace.Xmlns + "rescap", Rescap),
            new XAttribute(XNamespace.Xmlns + "com", Com),
            new XAttribute(XNamespace.Xmlns + "desktop4", Desktop4),
            new XAttribute(XNamespace.Xmlns + "desktop5", Desktop5),
            new XAttribute("IgnorableNamespaces", "uap uap10 rescap com desktop4 desktop5"),
            new XElement(Foundation + "Identity",
                new XAttribute("Name", PackageName),
                new XAttribute("Publisher", publisher),
                new XAttribute("Version", version.ToString(4)),
                new XAttribute("ProcessorArchitecture", "neutral")),
            new XElement(Foundation + "Properties",
                new XElement(Foundation + "DisplayName", "Mavue Quick View"),
                new XElement(Foundation + "PublisherDisplayName", "Mavue"),
                new XElement(Foundation + "Logo", Logo),
                new XElement(Uap10 + "AllowExternalContent", "true")),
            new XElement(Foundation + "Resources", new XElement(Foundation + "Resource", new XAttribute("Language", "en-us"))),
            new XElement(Foundation + "Dependencies",
                new XElement(Foundation + "TargetDeviceFamily",
                    new XAttribute("Name", "Windows.Desktop"),
                    new XAttribute("MinVersion", "10.0.19041.0"),
                    new XAttribute("MaxVersionTested", "10.0.26100.0"))),
            new XElement(Foundation + "Capabilities",
                new XElement(Rescap + "Capability", new XAttribute("Name", "runFullTrust")),
                new XElement(Rescap + "Capability", new XAttribute("Name", "unvirtualizedResources"))),
            new XElement(Foundation + "Applications",
                new XElement(Foundation + "Application",
                    new XAttribute("Id", "QuickView"),
                    new XAttribute("Executable", HostExecutable),
                    new XAttribute(Uap10 + "TrustLevel", "mediumIL"),
                    new XAttribute(Uap10 + "RuntimeBehavior", "win32App"),
                    new XElement(Uap + "VisualElements",
                        new XAttribute("AppListEntry", "none"),
                        new XAttribute("DisplayName", "Mavue Quick View"),
                        new XAttribute("Description", "Mavue Quick View"),
                        new XAttribute("BackgroundColor", "transparent"),
                        new XAttribute("Square150x150Logo", Logo),
                        new XAttribute("Square44x44Logo", Logo)),
                    new XElement(Foundation + "Extensions",
                        new XElement(Com + "Extension",
                            new XAttribute("Category", "windows.comServer"),
                            new XElement(Com + "ComServer",
                                new XElement(Com + "SurrogateServer",
                                    new XAttribute("DisplayName", "Mavue Quick View"),
                                    new XElement(Com + "Class",
                                        new XAttribute("Id", CommandClsid),
                                        new XAttribute("Path", CommandDll),
                                        new XAttribute("ThreadingModel", "STA"))))),
                        new XElement(Desktop4 + "Extension",
                            new XAttribute("Category", "windows.fileExplorerContextMenus"),
                            new XElement(Desktop4 + "FileExplorerContextMenus", itemTypes))))));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), package).ToString().Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Default version from the date, so every build can be registered after removing the previous one.</summary>
    public static Version VersionFor(DateTime utc) =>
        new(0, 1, int.Parse(utc.ToString("yyMM", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), utc.Day * 100 + utc.Hour);

    [GeneratedRegex(@"^(CN|O|OU|L|S|C)=[^""<>]+(, ?(CN|O|OU|L|S|C)=[^""<>]+)*$")]
    private static partial Regex PublisherPattern();

    [GeneratedRegex(@"^\.[a-z0-9]{1,10}$")]
    private static partial Regex ExtensionPattern();
}
