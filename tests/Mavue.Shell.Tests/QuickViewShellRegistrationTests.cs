using Microsoft.Win32;

namespace Mavue.Shell.Tests;

/// <summary>Runs against a private HKCU subkey; the real Software\Classes and Run keys are never touched.</summary>
[Trait("Category", "Shell")]
public sealed class QuickViewShellRegistrationTests : IDisposable
{
    private readonly string _root = @"Software\Mavue.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _host = Path.Combine(AppContext.BaseDirectory, "fake host.exe");

    public QuickViewShellRegistrationTests() => File.WriteAllText(_host, "x");

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

        File.Delete(_host);
    }

    private QuickViewShellRegistration Registration(string verb = QuickViewShellRegistration.DefaultVerbName) => new(Registry.CurrentUser, _root, verb);

    [Fact]
    public void Extensions_CoverDecodableFormats_Only()
    {
        Assert.Contains(".jpg", QuickViewShellRegistration.Extensions);
        Assert.Contains(".pdf", QuickViewShellRegistration.Extensions);
        Assert.Contains(".heic", QuickViewShellRegistration.Extensions);
        Assert.DoesNotContain(".svg", QuickViewShellRegistration.Extensions); // no decoder yet
        Assert.DoesNotContain(".jp2", QuickViewShellRegistration.Extensions);
    }

    [Fact]
    public void Register_WritesVerbForEveryExtension()
    {
        Registration().Register(_host, startAtSignIn: false);

        using RegistryKey verb = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\SystemFileAssociations\.jpg\shell\Mavue.QuickView")!;
        Assert.Equal("Mavue Quick View", verb.GetValue("MUIVerb"));
        Assert.Equal("Player", verb.GetValue("MultiSelectModel"));
        using RegistryKey command = verb.OpenSubKey("command")!;
        Assert.Equal($"\"{_host}\" --quickview \"%1\"", command.GetValue(string.Empty));

        RegistrationStatus status = Registration().Status();
        Assert.Equal(QuickViewShellRegistration.Extensions, status.Extensions);
        Assert.Single(status.Commands);
        Assert.Null(status.StartAtSignIn);
    }

    [Fact]
    public void Register_WithSignIn_AddsRunValue()
    {
        Registration().Register(_host, startAtSignIn: true);

        Assert.Equal($"\"{_host}\"", Registration().Status().StartAtSignIn);
    }

    [Fact]
    public void Unregister_RemovesOnlyMavueKeys()
    {
        // Another program's verb on the same file type, and another Run entry, must survive.
        using (RegistryKey other = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\SystemFileAssociations\.jpg\shell\OtherApp\command"))
        {
            other.SetValue(string.Empty, "other.exe \"%1\"");
        }

        using (RegistryKey run = Registry.CurrentUser.CreateSubKey($@"{_root}\Microsoft\Windows\CurrentVersion\Run"))
        {
            run.SetValue("OtherApp", "other.exe");
        }

        Registration().Register(_host, startAtSignIn: true);
        int removed = Registration().Unregister();

        Assert.Equal(QuickViewShellRegistration.Extensions.Count + 1, removed);
        RegistrationStatus status = Registration().Status();
        Assert.Empty(status.Extensions);
        Assert.Null(status.StartAtSignIn);
        using RegistryKey? kept = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\SystemFileAssociations\.jpg\shell\OtherApp\command");
        Assert.Equal("other.exe \"%1\"", kept?.GetValue(string.Empty));
        using RegistryKey? keptRun = Registry.CurrentUser.OpenSubKey($@"{_root}\Microsoft\Windows\CurrentVersion\Run");
        Assert.Equal("other.exe", keptRun?.GetValue("OtherApp"));

        // Keys Mavue created and left empty are cleaned up.
        using RegistryKey? png = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\SystemFileAssociations\.png");
        Assert.Null(png);
    }

    [Fact]
    public void Register_IsIdempotent_AndReplacesOldPath()
    {
        string other = Path.Combine(AppContext.BaseDirectory, "other host.exe");
        File.WriteAllText(other, "x");
        try
        {
            Registration().Register(_host, startAtSignIn: false);
            Registration().Register(other, startAtSignIn: false);

            Assert.Equal($"\"{other}\" --quickview \"%1\"", Assert.Single(Registration().Status().Commands));
        }
        finally
        {
            File.Delete(other);
        }
    }

    [Fact]
    public void VerbNames_AreIndependent()
    {
        Registration().Register(_host, startAtSignIn: false);
        Registration("Mavue.QuickView.E2E").Register(_host, startAtSignIn: false);

        Registration("Mavue.QuickView.E2E").Unregister();

        Assert.NotEmpty(Registration().Status().Extensions);
        Assert.Empty(Registration("Mavue.QuickView.E2E").Status().Extensions);
    }

    [Fact]
    public void UnregisterContextMenu_KeepsSignInEntry()
    {
        // Used when the Windows 11 menu command replaces the classic one.
        Registration().Register(_host, startAtSignIn: true);

        int removed = Registration().UnregisterContextMenu();

        Assert.Equal(QuickViewShellRegistration.Extensions.Count, removed);
        RegistrationStatus status = Registration().Status();
        Assert.Empty(status.Extensions);
        Assert.Equal($"\"{_host}\"", status.StartAtSignIn);
    }

    [Fact]
    public void RegisterStartAtSignIn_AddsOnlyTheRunValue()
    {
        Registration().RegisterStartAtSignIn(_host);

        RegistrationStatus status = Registration().Status();
        Assert.Empty(status.Extensions);
        Assert.Equal($"\"{_host}\"", status.StartAtSignIn);
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData(@"C:\does\not\exist.exe")]
    [InlineData("")]
    public void Register_RejectsBadHostPath(string path) =>
        Assert.Throws<ArgumentException>(() => Registration().Register(path, startAtSignIn: false));

    [Theory]
    [InlineData(@"a\b")]
    [InlineData("a\"b")]
    [InlineData("")]
    public void RejectsBadVerbName(string verb) =>
        Assert.Throws<ArgumentException>(() => new QuickViewShellRegistration(Registry.CurrentUser, _root, verb));
}
