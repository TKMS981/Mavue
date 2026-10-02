using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mavue.Shell.Tests;

[Trait("Category", "Shell")]
public sealed partial class IdentityPackageManifestTests
{
    private static readonly XNamespace F = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";
    private static readonly XNamespace Desktop4 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4";
    private static readonly XNamespace Desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";

    private static XElement Create() =>
        XElement.Parse(IdentityPackageManifest.Create("CN=Mavue Dev", new Version(0, 1, 2610, 212), QuickViewShellRegistration.Extensions));

    [Fact]
    public void Identity_AndSparseProperties()
    {
        XElement package = Create();
        XElement identity = package.Element(F + "Identity")!;
        Assert.Equal(IdentityPackageManifest.PackageName, (string?)identity.Attribute("Name"));
        Assert.Equal("CN=Mavue Dev", (string?)identity.Attribute("Publisher"));
        Assert.Equal("0.1.2610.212", (string?)identity.Attribute("Version"));
        Assert.Equal("neutral", (string?)identity.Attribute("ProcessorArchitecture"));
        Assert.Equal("true", package.Element(F + "Properties")!.Element(Uap10 + "AllowExternalContent")!.Value);

        XElement application = package.Descendants(F + "Application").Single();
        Assert.Equal(IdentityPackageManifest.HostExecutable, (string?)application.Attribute("Executable"));
        Assert.Equal("mediumIL", (string?)application.Attribute(Uap10 + "TrustLevel"));
        Assert.Equal("win32App", (string?)application.Attribute(Uap10 + "RuntimeBehavior"));
    }

    [Fact]
    public void ComServer_AndMenu_UseTheSameClsid_ForEveryExtension()
    {
        XElement package = Create();
        XElement @class = package.Descendants(Com + "SurrogateServer").Single().Element(Com + "Class")!;
        Assert.Equal(IdentityPackageManifest.CommandClsid, (string?)@class.Attribute("Id"));
        Assert.Equal(IdentityPackageManifest.CommandDll, (string?)@class.Attribute("Path"));
        Assert.Equal("STA", (string?)@class.Attribute("ThreadingModel"));

        XElement menus = package.Descendants(Desktop4 + "FileExplorerContextMenus").Single();
        Assert.Equal("windows.fileExplorerContextMenus", (string?)menus.Parent!.Attribute("Category"));
        var itemTypes = menus.Elements(Desktop5 + "ItemType").ToList();
        Assert.Equal(QuickViewShellRegistration.Extensions, itemTypes.Select(t => (string)t.Attribute("Type")!));
        Assert.All(itemTypes, t => Assert.Equal(IdentityPackageManifest.CommandClsid, (string?)t.Element(Desktop5 + "Verb")!.Attribute("Clsid")));
    }

    [Fact]
    public void Clsid_MatchesTheNativeCommand()
    {
        string header = File.ReadAllText(Path.Combine(RepoRoot(), "native", "Mavue.Shell.Native", "src", "ExplorerCommand.h"));
        Match uuid = UuidPattern().Match(header);
        Assert.True(uuid.Success);
        Assert.Equal(IdentityPackageManifest.CommandClsid, uuid.Groups[1].Value, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Mavue Dev")]
    [InlineData("CN=\"x\"")]
    public void RejectsBadPublisher(string publisher) =>
        Assert.Throws<ArgumentException>(() => IdentityPackageManifest.Create(publisher, new Version(1, 0, 0, 0), [".jpg"]));

    [Theory]
    [InlineData("jpg")]
    [InlineData(".JPG")]
    [InlineData(".j\"pg")]
    public void RejectsBadExtension(string extension) =>
        Assert.Throws<ArgumentException>(() => IdentityPackageManifest.Create("CN=Mavue Dev", new Version(1, 0, 0, 0), [extension]));

    [Fact]
    public void RejectsTwoPartVersion() =>
        Assert.Throws<ArgumentException>(() => IdentityPackageManifest.Create("CN=Mavue Dev", new Version(1, 0), [".jpg"]));

    [Fact]
    public void VersionFor_IsFourPartAndIncreases()
    {
        Version a = IdentityPackageManifest.VersionFor(new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc));
        Version b = IdentityPackageManifest.VersionFor(new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc));
        Version c = IdentityPackageManifest.VersionFor(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.True(a < b && b < c);
        Assert.Equal("0.1.2610.209", a.ToString(4));
    }

    internal static string RepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Mavue.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"uuid\(""([0-9A-Fa-f-]{36})""\)")]
    private static partial Regex UuidPattern();
}
