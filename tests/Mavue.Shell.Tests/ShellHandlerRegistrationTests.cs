using Microsoft.Win32;

namespace Mavue.Shell.Tests;

/// <summary>
/// Runs against a private HKCU subkey with a simulated "which handler does Explorer use" lookup; the real
/// Software\Classes and PreviewHandlers are never touched.
/// </summary>
[Trait("Category", "Shell")]
public sealed class ShellHandlerRegistrationTests : IDisposable
{
    private static readonly Guid Other = new("3A84F9C2-6164-485C-A7D9-4B27F8AC009E"); // e.g. Edge's PDF previewer
    private static readonly Guid WindowsThumbnails = new("C7657C4A-9F68-40FA-A4DF-96BC08EB3551");

    private readonly string _root = @"Software\Mavue.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "シェル " + Guid.NewGuid().ToString("N"));
    private readonly string _dll;

    public ShellHandlerRegistrationTests()
    {
        Directory.CreateDirectory(_directory);
        _dll = Path.Combine(_directory, ShellHandlerRegistration.DllName);
        File.WriteAllText(_dll, "x");
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

    private static string B(Guid guid) => guid.ToString("B").ToUpperInvariant();

    private string? Read(string path, string name = "")
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey($@"{_root}\{path}");
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    private string? Binding(string path, Guid shellEx) => Read($@"Classes\{path}\ShellEx\{B(shellEx)}");

    /// <summary>
    /// What Explorer would resolve: Mavue's per-user extension binding first (when taken over), then the simulated
    /// existing handlers (Windows' image thumbnails, a PDF previewer), then Mavue's SystemFileAssociations binding.
    /// </summary>
    private Guid? Resolve(string extension, Guid shellEx, bool otherPdfPreview = true)
    {
        if (Guid.TryParse(Binding(extension, shellEx), out Guid perUser))
        {
            return perUser;
        }

        if (shellEx == ShellHandlerRegistration.ThumbnailProviderIid && extension is ".jpg" or ".png" or ".gif")
        {
            return WindowsThumbnails;
        }

        if (otherPdfPreview && shellEx == ShellHandlerRegistration.PreviewHandlerIid && extension == ".pdf")
        {
            return Other;
        }

        return Guid.TryParse(Binding($@"SystemFileAssociations\{extension}", shellEx), out Guid association) ? association : null;
    }

    private ShellHandlerRegistration Registration(Func<string, Guid, Guid?>? resolve = null) =>
        new(Registry.CurrentUser, _root, resolve ?? ((e, s) => Resolve(e, s)));

    [Fact]
    public void Register_WritesTheClasses_AndBindsOnlyWhereNoHandlerExists()
    {
        ShellHandlerSummary summary = Registration().Register(_dll);

        // COM classes: in-process, apartment; the preview handler in its own prevhost.exe (AppID surrogate).
        string preview = $@"Classes\CLSID\{B(ShellHandlerRegistration.PreviewHandlerClsid)}";
        Assert.Equal(_dll, Read($@"{preview}\InprocServer32"));
        Assert.Equal("Apartment", Read($@"{preview}\InprocServer32", "ThreadingModel"));
        Assert.Equal(B(ShellHandlerRegistration.PreviewAppId), Read(preview, "AppID"));
        Assert.Equal(ShellHandlerRegistration.PreviewSurrogate, Read($@"Classes\AppID\{B(ShellHandlerRegistration.PreviewAppId)}", "DllSurrogate"));
        Assert.Equal(_dll, Read($@"Classes\CLSID\{B(ShellHandlerRegistration.ThumbnailProviderClsid)}\InprocServer32"));
        Assert.Null(Read($@"Classes\CLSID\{B(ShellHandlerRegistration.ThumbnailProviderClsid)}", "DisableProcessIsolation"));
        Assert.Equal(ShellHandlerRegistration.PreviewHandlerName, Read(@"Microsoft\Windows\CurrentVersion\PreviewHandlers", B(ShellHandlerRegistration.PreviewHandlerClsid)));

        // Thumbnails: only where Windows has none (PDF, SVG); JPEG/PNG/GIF keep Windows' own.
        Assert.Equal(B(ShellHandlerRegistration.ThumbnailProviderClsid), Binding(@"SystemFileAssociations\.pdf", ShellHandlerRegistration.ThumbnailProviderIid));
        Assert.Equal(B(ShellHandlerRegistration.ThumbnailProviderClsid), Binding(@"SystemFileAssociations\.svg", ShellHandlerRegistration.ThumbnailProviderIid));
        Assert.Null(Binding(@"SystemFileAssociations\.jpg", ShellHandlerRegistration.ThumbnailProviderIid));
        Assert.Contains(".jpg", summary.ThumbnailKept);
        Assert.DoesNotContain(".mp4", summary.ThumbnailAdded.Concat(summary.ThumbnailKept)); // video/audio: Windows' own

        // Previews: images, video, audio get Mavue's; PDF keeps the other previewer by default.
        Assert.Equal(B(ShellHandlerRegistration.PreviewHandlerClsid), Binding(@"SystemFileAssociations\.jpg", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Equal(B(ShellHandlerRegistration.PreviewHandlerClsid), Binding(@"SystemFileAssociations\.mp4", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Null(Binding(@"SystemFileAssociations\.pdf", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Null(Binding(".pdf", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Equal([".pdf"], summary.PreviewKept);
        Assert.Empty(summary.PreviewReplaced);

        // Under Mavue's ProgIDs (used when Mavue is the default app), never as a default.
        Assert.Equal(B(ShellHandlerRegistration.PreviewHandlerClsid), Binding(AppRegistration.PdfProgId, ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Null(Read(@"Classes\.pdf"));
    }

    [Fact]
    public void PreferMavuePreview_TakesOver_AndUnregisterRestoresThePreviousValue()
    {
        // Another application's per-user binding at the same place.
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\.pdf\ShellEx\{B(ShellHandlerRegistration.PreviewHandlerIid)}"))
        {
            key.SetValue(string.Empty, B(Other));
        }

        ShellHandlerRegistration registration = Registration();
        ShellHandlerSummary summary = registration.Register(_dll, preferMavuePreview: true);

        Assert.Equal([".pdf"], summary.PreviewReplaced);
        Assert.Equal(B(ShellHandlerRegistration.PreviewHandlerClsid), Binding(".pdf", ShellHandlerRegistration.PreviewHandlerIid));

        // Registering again (e.g. a new build) keeps the saved value, not Mavue's own.
        registration.Register(_dll, preferMavuePreview: true);
        registration.Unregister();

        Assert.Equal(B(Other), Binding(".pdf", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Null(Read($@"Mavue\ShellExBackup"));
    }

    [Fact]
    public void PreferMavuePreview_WhenItWouldNotTakeEffect_LeavesEverythingAsItWas()
    {
        // The user's default app binds its own previewer at a higher level: Mavue's per-user binding would not win.
        ShellHandlerSummary summary = Registration((e, s) => e == ".pdf" && s == ShellHandlerRegistration.PreviewHandlerIid ? Other : Resolve(e, s)).Register(_dll, preferMavuePreview: true);

        Assert.Contains(".pdf", summary.PreviewKept);
        Assert.Null(Binding(".pdf", ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Null(Read(@"Classes\.pdf"));
    }

    [Fact]
    public void WithoutTheOtherHandler_PdfPreviewIsMavues()
    {
        ShellHandlerSummary summary = Registration((e, s) => Resolve(e, s, otherPdfPreview: false)).Register(_dll);

        Assert.Contains(".pdf", summary.PreviewAdded);
        Assert.Contains(".pdf", Registration().RegisteredExtensions(ShellHandlerRegistration.PreviewHandlerIid));
    }

    [Fact]
    public void Unregister_RemovesOnlyMavuesKeysAndValues()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\SystemFileAssociations\.gif\ShellEx\{B(ShellHandlerRegistration.ThumbnailProviderIid)}"))
        {
            key.SetValue(string.Empty, B(Other));
        }

        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{_root}\Microsoft\Windows\CurrentVersion\PreviewHandlers"))
        {
            key.SetValue(B(Other), "Other previewer");
        }

        ShellHandlerRegistration registration = Registration();
        registration.Register(_dll);
        Assert.True(registration.IsRegistered);
        Assert.True(registration.Unregister() > 10);

        Assert.False(registration.IsRegistered);
        Assert.Null(Read($@"Classes\CLSID\{B(ShellHandlerRegistration.PreviewHandlerClsid)}"));
        Assert.Null(Read($@"Classes\AppID\{B(ShellHandlerRegistration.PreviewAppId)}"));
        Assert.Empty(registration.RegisteredExtensions(ShellHandlerRegistration.PreviewHandlerIid));
        Assert.Empty(registration.RegisteredExtensions(ShellHandlerRegistration.ThumbnailProviderIid));
        Assert.Equal(B(Other), Binding(@"SystemFileAssociations\.gif", ShellHandlerRegistration.ThumbnailProviderIid));
        Assert.Equal("Other previewer", Read(@"Microsoft\Windows\CurrentVersion\PreviewHandlers", B(Other)));
        Assert.Null(Read(@"Microsoft\Windows\CurrentVersion\PreviewHandlers", B(ShellHandlerRegistration.PreviewHandlerClsid)));
        using RegistryKey? associations = Registry.CurrentUser.OpenSubKey($@"{_root}\Classes\SystemFileAssociations");
        Assert.Equal([".gif"], associations?.GetSubKeyNames() ?? []); // no empty keys left behind
    }

    [Fact]
    public void Register_RejectsAnythingButTheDll()
    {
        Assert.Throws<ArgumentException>(() => Registration().Register(Path.Combine(_directory, "other.dll")));
        Assert.Throws<ArgumentException>(() => Registration().Register(ShellHandlerRegistration.DllName));
        Assert.False(Registration().IsRegistered);
    }

    [Fact]
    public void AppRegistration_IncludesTheHandlers_WhenTheDllIsNextToMavueExe()
    {
        string exe = Path.Combine(_directory, AppRegistration.ExecutableName);
        File.WriteAllText(exe, "x");
        var app = new AppRegistration(Registry.CurrentUser, _root, (e, s) => Resolve(e, s));
        var text = new AppRegistrationText("Open in Mavue", "Viewer", "Image", "PDF", "Video", "Audio");

        ShellHandlerSummary? summary = app.Register(exe, text);
        Assert.NotNull(summary);
        Assert.True(app.Handlers.IsRegistered);

        File.Delete(_dll);
        Assert.Null(app.Register(exe, text)); // a copy without the DLL removes the stale handler registration
        Assert.False(app.Handlers.IsRegistered);

        File.WriteAllText(_dll, "x");
        app.Register(exe, text);
        app.Unregister();
        Assert.False(app.Handlers.IsRegistered);
        Assert.Null(Read(@"Classes\SystemFileAssociations"));
    }

    [Fact]
    public void Extensions_CoverTheViewer_ThumbnailsOnlyForImagesAndPdf()
    {
        Assert.Equal(Core.Viewing.ViewerFormats.Extensions, ShellHandlerRegistration.PreviewExtensions);
        Assert.Contains(".pdf", ShellHandlerRegistration.ThumbnailExtensions);
        Assert.Contains(".svg", ShellHandlerRegistration.ThumbnailExtensions);
        Assert.DoesNotContain(".mp4", ShellHandlerRegistration.ThumbnailExtensions);
        Assert.DoesNotContain(".mp3", ShellHandlerRegistration.ThumbnailExtensions);
    }

    [Fact]
    public void TakeOver_OnlyTheChosenExtensions_AndRegisteringWithoutGivesThemBack()
    {
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey($@"{_root}\Classes\.mp4\ShellEx\{B(ShellHandlerRegistration.PreviewHandlerIid)}"))
        {
            key.SetValue(string.Empty, B(Other)); // another previewer for MP4 too
        }

        ShellHandlerRegistration registration = Registration((e, s) => Resolve(e, s));
        ShellHandlerSummary summary = registration.Register(_dll, [".pdf"]);

        Assert.Equal([".pdf"], summary.PreviewReplaced);
        Assert.Equal([".pdf"], registration.TakenOverPreviews());
        Assert.Equal(B(Other), Binding(".mp4", ShellHandlerRegistration.PreviewHandlerIid)); // not chosen: kept

        registration.Register(_dll); // the choice turned off
        Assert.Empty(registration.TakenOverPreviews());
        Assert.Null(Binding(".pdf", ShellHandlerRegistration.PreviewHandlerIid));
    }

    [Fact]
    public void X86Build_IsRegisteredInThe32BitView_ForThirtyTwoBitApplications()
    {
        ShellHandlerRegistration registration = Registration();
        registration.Register(_dll);
        string preview32 = $@"Classes\Wow6432Node\CLSID\{B(ShellHandlerRegistration.PreviewHandlerClsid)}";
        Assert.Null(Read($@"{preview32}\InprocServer32")); // no x86 build next to the DLL: nothing in the 32-bit view

        string dll32 = Path.Combine(_directory, ShellHandlerRegistration.X86Folder, ShellHandlerRegistration.DllName);
        Directory.CreateDirectory(Path.GetDirectoryName(dll32)!);
        File.WriteAllText(dll32, "x");
        registration.Register(_dll);

        Assert.Equal(dll32, Read($@"{preview32}\InprocServer32"));
        Assert.Equal(B(ShellHandlerRegistration.PreviewHost32AppId), Read(preview32, "AppID")); // Windows' 32-bit prevhost
        Assert.Equal(dll32, Read($@"Classes\Wow6432Node\CLSID\{B(ShellHandlerRegistration.ThumbnailProviderClsid)}\InprocServer32"));

        registration.Unregister();
        Assert.Null(Read(preview32));
    }
}
