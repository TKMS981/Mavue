using Microsoft.Win32;

namespace Mavue.Shell.Tests;

/// <summary>Runs against a private HKCU subkey; the real Software\Classes, App Paths and RegisteredApplications are never touched.</summary>
[Trait("Category", "Shell")]
public sealed class AppRegistrationTests : IDisposable
{
    private static readonly AppRegistrationText Text = new("Open in Mavue", "Viewer", "Image (Mavue)", "PDF (Mavue)", "Video (Mavue)", "Audio (Mavue)");

    private readonly string _root = @"Software\Mavue.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "アプリ " + Guid.NewGuid().ToString("N"));
    private readonly string _exe;

    public AppRegistrationTests()
    {
        Directory.CreateDirectory(_directory);
        _exe = Path.Combine(_directory, AppRegistration.ExecutableName);
        File.WriteAllText(_exe, "x");
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using (RegistryKey? parent = Registry.CurrentUser.OpenSubKey(@"Software\Mavue.Tests"))
        {
            if (parent is { SubKeyCount: 0, ValueCount: 0 })
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\Mavue.Tests", throwOnMissingSubKey: false);
            }
        }

        Directory.Delete(_directory, recursive: true);
    }

    private AppRegistration Registration() => new(Registry.CurrentUser, _root);

    private RegistryKey? Open(string path) => Registry.CurrentUser.OpenSubKey($@"{_root}\{path}");

    [Fact]
    public void ProgIds_CoverEveryViewableExtension()
    {
        Assert.Equal(AppRegistration.PdfProgId, AppRegistration.ProgIdsByExtension[".pdf"]);
        Assert.Equal(AppRegistration.ImageProgId, AppRegistration.ProgIdsByExtension[".jpg"]);
        Assert.Equal(AppRegistration.ImageProgId, AppRegistration.ProgIdsByExtension[".svg"]);
        Assert.Equal(AppRegistration.VideoProgId, AppRegistration.ProgIdsByExtension[".mp4"]);
        Assert.Equal(AppRegistration.AudioProgId, AppRegistration.ProgIdsByExtension[".flac"]);
        Assert.Equal(Core.Viewing.ViewerFormats.Extensions.Count, AppRegistration.ProgIdsByExtension.Count);
    }

    [Fact]
    public void Register_WritesOpenWith_DefaultApps_AppPaths_AndTheMenu_WithoutChangingDefaults()
    {
        // Another app's default and Open-with entry must survive.
        using (RegistryKey other = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\.jpg"))
        {
            other.SetValue(string.Empty, "Other.Jpeg");
            using RegistryKey otherOpenWith = other.CreateSubKey("OpenWithProgids");
            otherOpenWith.SetValue("Other.Jpeg", Array.Empty<byte>(), RegistryValueKind.None);
        }

        Registration().Register(_exe, Text);

        using (RegistryKey? appPath = Open($@"Microsoft\Windows\CurrentVersion\App Paths\{AppRegistration.ExecutableName}"))
        {
            Assert.Equal(_exe, appPath?.GetValue(string.Empty));
        }

        using (RegistryKey? command = Open($@"Classes\{AppRegistration.PdfProgId}\shell\open\command"))
        {
            Assert.Equal($"\"{_exe}\" \"%1\"", command?.GetValue(string.Empty));
        }

        using (RegistryKey? jpg = Open(@"Classes\.jpg"))
        {
            Assert.Equal("Other.Jpeg", jpg?.GetValue(string.Empty)); // default untouched
        }

        using (RegistryKey? openWith = Open(@"Classes\.jpg\OpenWithProgids"))
        {
            Assert.Contains(AppRegistration.ImageProgId, openWith!.GetValueNames());
            Assert.Contains("Other.Jpeg", openWith.GetValueNames());
        }

        using (RegistryKey? capabilities = Open($@"{AppRegistration.ApplicationName}\Capabilities\FileAssociations"))
        {
            Assert.Equal(AppRegistration.VideoProgId, capabilities?.GetValue(".mkv"));
        }

        using (RegistryKey? registered = Open("RegisteredApplications"))
        {
            Assert.Equal(@"Software\Mavue\Capabilities", registered?.GetValue(AppRegistration.ApplicationName));
        }

        using (RegistryKey? verb = Open($@"Classes\SystemFileAssociations\.png\shell\{AppRegistration.OpenVerbName}"))
        {
            Assert.Equal("Open in Mavue", verb?.GetValue("MUIVerb"));
            Assert.Equal("Single", verb?.GetValue("MultiSelectModel"));
        }

        Assert.Equal(_exe, Registration().RegisteredExecutable());
        Assert.Equal(AppRegistration.ProgIdsByExtension.Count, Registration().RegisteredExtensions().Count);
    }

    [Fact]
    public void Unregister_RemovesOnlyMavuesEntries()
    {
        using (RegistryKey other = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\.jpg\OpenWithProgids"))
        {
            other.SetValue("Other.Jpeg", Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (RegistryKey otherVerb = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\SystemFileAssociations\.jpg\shell\Other.Verb"))
        {
            otherVerb.SetValue("MUIVerb", "Other");
        }

        Registration().Register(_exe, Text);
        int removed = Registration().Unregister();

        Assert.True(removed > AppRegistration.ProgIdsByExtension.Count);
        Assert.Null(Registration().RegisteredExecutable());
        Assert.Empty(Registration().RegisteredExtensions());
        Assert.Null(Open($@"Classes\{AppRegistration.ImageProgId}"));
        Assert.Null(Open(@"Classes\.png")); // created only for Mavue: removed when empty
        using (RegistryKey? openWith = Open(@"Classes\.jpg\OpenWithProgids"))
        {
            Assert.Equal(["Other.Jpeg"], openWith!.GetValueNames());
        }

        Assert.NotNull(Open(@"Classes\SystemFileAssociations\.jpg\shell\Other.Verb"));
        Assert.Null(Open($@"Classes\SystemFileAssociations\.png"));
    }

    [Fact]
    public void Register_RejectsOtherExecutables()
    {
        string other = Path.Combine(_directory, "Other.exe");
        File.WriteAllText(other, "x");
        Assert.Throws<ArgumentException>(() => Registration().Register(other, Text));
        Assert.Throws<ArgumentException>(() => Registration().Register("Mavue.exe", Text));
        Assert.Throws<ArgumentException>(() => Registration().Register(Path.Combine(_directory, "missing", AppRegistration.ExecutableName), Text));
    }
}
