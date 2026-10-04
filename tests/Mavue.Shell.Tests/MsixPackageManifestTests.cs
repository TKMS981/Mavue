using System.Xml.Linq;

namespace Mavue.Shell.Tests;

[Trait("Category", "Shell")]
public sealed class MsixPackageManifestTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Desktop2 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static XDocument Manifest() => XDocument.Parse(MsixPackageManifest.Create("CN=Mavue Dev", new Version(1, 2, 3, 4), "x64"));

    [Fact]
    public void Package_HasMavueAndQuickView_WithTheirExecutables()
    {
        XDocument manifest = Manifest();
        XElement identity = manifest.Root!.Element(Foundation + "Identity")!;
        Assert.Equal("Mavue", (string?)identity.Attribute("Name"));
        Assert.Equal("1.2.3.4", (string?)identity.Attribute("Version"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));

        var applications = manifest.Descendants(Foundation + "Application").ToDictionary(a => (string)a.Attribute("Id")!, a => (string)a.Attribute("Executable")!);
        Assert.Equal(@"Mavue\Mavue.exe", applications["Mavue"]);
        Assert.Equal(@"Mavue\Mavue.QuickView.Host.exe", applications["QuickView"]); // one folder, one self-contained runtime
        Assert.Equal(MsixPackageManifest.StartupTaskId, (string?)manifest.Descendants(Desktop + "StartupTask").Single().Attribute("TaskId"));
    }

    [Fact]
    public void FileTypes_AreTheViewersOnes_WithPreview_AndThumbnailsOnlyForPdfAndSvg()
    {
        XDocument manifest = Manifest();
        string[] types = manifest.Descendants(Uap + "FileType").Select(e => e.Value).ToArray();
        Assert.Equal(Core.Viewing.ViewerFormats.Extensions.Order(StringComparer.Ordinal), types.Order(StringComparer.Ordinal));
        Assert.Equal(types.Length, types.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // each extension once

        var associations = manifest.Descendants(Uap + "FileTypeAssociation").ToList();
        Assert.All(associations, a => Assert.NotNull(a.Element(Desktop2 + "DesktopPreviewHandler")));
        Assert.Equal(["mavue.pdf", "mavue.svg"], associations.Where(a => a.Element(Desktop2 + "ThumbnailHandler") is not null).Select(a => (string)a.Attribute("Name")!));

        string[] classes = manifest.Descendants(Com + "Class").Select(c => ((string)c.Attribute("Id")!).ToUpperInvariant()).ToArray();
        Assert.Contains(ShellHandlerRegistration.PreviewHandlerClsid.ToString().ToUpperInvariant(), classes);
        Assert.Contains(ShellHandlerRegistration.ThumbnailProviderClsid.ToString().ToUpperInvariant(), classes);
        Assert.Contains(IdentityPackageManifest.CommandClsid, classes);
    }

    [Fact]
    public void UnknownArchitecture_IsRejected() =>
        Assert.Throws<ArgumentException>(() => MsixPackageManifest.Create("CN=Mavue Dev", new Version(1, 0, 0, 0), "x86"));
}
