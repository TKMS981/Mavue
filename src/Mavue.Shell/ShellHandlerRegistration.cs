using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Microsoft.Win32;

namespace Mavue.Shell;

/// <summary>What <see cref="ShellHandlerRegistration.Register"/> did, per extension.</summary>
/// <param name="PreviewAdded">Extensions that had no preview handler and now use Mavue's.</param>
/// <param name="PreviewReplaced">Extensions whose existing preview handler Mavue now takes over (only when asked).</param>
/// <param name="PreviewKept">Extensions that keep another application's preview handler.</param>
/// <param name="ThumbnailAdded">Extensions that had no thumbnail provider and now use Mavue's.</param>
/// <param name="ThumbnailKept">Extensions that keep their existing thumbnail provider (Windows' own for most images).</param>
public sealed record ShellHandlerSummary(
    IReadOnlyList<string> PreviewAdded,
    IReadOnlyList<string> PreviewReplaced,
    IReadOnlyList<string> PreviewKept,
    IReadOnlyList<string> ThumbnailAdded,
    IReadOnlyList<string> ThumbnailKept);

/// <summary>
/// Per-user (HKCU) registration of Mavue's File Explorer preview handler and thumbnail provider
/// (native <c>Mavue.Shell.Preview.dll</c>), following Microsoft Learn "Registering preview handlers" and "Thumbnail
/// handlers":
/// <list type="bullet">
/// <item>The COM classes (in-process, apartment threaded). The preview handler has its own AppID whose surrogate is
/// <c>prevhost.exe</c>, so it runs in a preview host process of its own (a problem in a file never affects other
/// applications' previews). The thumbnail provider is initialized with a stream, so Explorer runs it in its isolated
/// thumbnail process.</item>
/// <item>Bindings (<c>ShellEx\{IID}</c>) only where Explorer finds no handler today, under
/// <c>SystemFileAssociations\.ext</c> (the lowest priority: a handler another application registers later still wins).
/// Existing handlers (Windows' image thumbnails, Edge's PDF preview, …) are kept, unless the user explicitly asks for
/// Mavue's preview (<c>preferMavuePreview</c>); then the previous per-user value is saved and restored on removal.</item>
/// <item>The preview handler under Mavue's ProgIDs too (used when the user has made Mavue the default app).</item>
/// </list>
/// Default apps are never changed. Removal deletes only keys and values that name Mavue's classes.
/// </summary>
public sealed partial class ShellHandlerRegistration
{
    public const string DllName = "Mavue.Shell.Preview.dll";

    /// <summary>Must match <c>PreviewHandlerClsid</c> in native/Mavue.Shell.Preview/src/Module.h.</summary>
    public static readonly Guid PreviewHandlerClsid = new("AB883DEA-90EE-4AF4-944A-45CEDD231E53");

    /// <summary>Must match <c>ThumbnailProviderClsid</c> in native/Mavue.Shell.Preview/src/Module.h.</summary>
    public static readonly Guid ThumbnailProviderClsid = new("B4E9FA4B-4DA4-4A1A-9DC7-DE422F056135");

    /// <summary>AppID of the preview handler: its own prevhost.exe instance.</summary>
    public static readonly Guid PreviewAppId = new("E2B69F32-83E0-4DF1-BC8A-0D556307129F");

    /// <summary>IID of IPreviewHandler: the ShellEx key name of preview handlers.</summary>
    public static readonly Guid PreviewHandlerIid = new("8895B1C6-B41F-4C1C-A562-0D564250836F");

    /// <summary>IID of IThumbnailProvider: the ShellEx key name of thumbnail providers.</summary>
    public static readonly Guid ThumbnailProviderIid = new("E357FCCD-A995-4576-B01F-234630154E96");

    public const string PreviewHandlerName = "Mavue Preview Handler";
    public const string ThumbnailProviderName = "Mavue Thumbnail Provider";

    /// <summary>Windows' 32-bit preview host (SysWOW64\prevhost.exe): the AppID for 32-bit preview handlers on 64-bit Windows.</summary>
    public static readonly Guid PreviewHost32AppId = new("534A1E02-D58F-44F0-B58B-36CBED287C7C");

    /// <summary>Folder (next to the 64-bit DLL) of the x86 build for 32-bit applications.</summary>
    public const string X86Folder = "x86";

    /// <summary>prevhost.exe as the preview handler's surrogate (REG_EXPAND_SZ).</summary>
    public const string PreviewSurrogate = @"%SystemRoot%\system32\prevhost.exe";

    private readonly RegistryKey _hive;
    private readonly string _softwarePath;
    private readonly Func<string, Guid, Guid?> _currentHandler;

    /// <param name="hive">Normally <see cref="Registry.CurrentUser"/>.</param>
    /// <param name="softwarePath">Normally "Software"; tests use a private subkey.</param>
    /// <param name="currentHandler">
    /// The handler Explorer uses today for (extension, ShellEx IID), or null for none. Default: the real lookup
    /// (<c>AssocQueryString(ASSOCSTR_SHELLEXTENSION)</c>, the association order Explorer itself uses).
    /// </param>
    public ShellHandlerRegistration(RegistryKey hive, string softwarePath = "Software", Func<string, Guid, Guid?>? currentHandler = null)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrEmpty(softwarePath);
        _hive = hive;
        _softwarePath = softwarePath;
        _currentHandler = currentHandler ?? QueryShellExtension;
    }

    /// <summary>Extensions offered to the preview handler: everything the viewer opens.</summary>
    public static IReadOnlyList<string> PreviewExtensions { get; } = ViewerFormats.Extensions;

    /// <summary>Extensions offered to the thumbnail provider: images and PDF (video/audio have Windows' own).</summary>
    public static IReadOnlyList<string> ThumbnailExtensions { get; } = ViewerFormats.All
        .Where(f => !FileFormatKinds.IsMedia(f))
        .SelectMany(FileFormatDetector.ExtensionsOf)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static readonly string[] ProgIds = [AppRegistration.ImageProgId, AppRegistration.PdfProgId, AppRegistration.VideoProgId, AppRegistration.AudioProgId];

    private string ClassesPath => $@"{_softwarePath}\Classes";

    /// <summary>Where the 32-bit registry view of HKCU\Software\Classes\CLSID is stored.</summary>
    private string Wow64ClassesPath => $@"{_softwarePath}\Classes\Wow6432Node";

    private string PreviewHandlersPath => $@"{_softwarePath}\Microsoft\Windows\CurrentVersion\PreviewHandlers";

    private string BackupPath => $@"{_softwarePath}\{AppRegistration.ApplicationName}\ShellExBackup";

    private static string Braced(Guid guid) => guid.ToString("B").ToUpperInvariant();

    /// <summary>Registers <paramref name="dll"/>; with <paramref name="preferMavuePreview"/> takes over every existing preview.</summary>
    public ShellHandlerSummary Register(string dll, bool preferMavuePreview) => Register(dll, preferMavuePreview ? PreviewExtensions : null);

    /// <summary>
    /// Registers <paramref name="dll"/> (Mavue.Shell.Preview.dll) for the current user; replaces an earlier registration.
    /// </summary>
    /// <param name="takeOverPreviewFor">
    /// Extensions whose existing preview handler Mavue's replaces (the user's explicit choice, e.g. ".pdf"); null or
    /// empty: none (existing handlers are kept, and earlier take-overs are given back).
    /// </param>
    public ShellHandlerSummary Register(string dll, IReadOnlyCollection<string>? takeOverPreviewFor = null)
    {
        if (string.IsNullOrEmpty(dll) || !Path.IsPathFullyQualified(dll) || dll.Contains('"', StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(dll), DllName, StringComparison.OrdinalIgnoreCase) || !File.Exists(dll))
        {
            throw new ArgumentException($"The path must be an existing, fully qualified {DllName}.", nameof(dll));
        }

        WriteClass(PreviewHandlerClsid, PreviewHandlerName, dll, PreviewAppId);
        using (RegistryKey appId = _hive.CreateSubKey($@"{ClassesPath}\AppID\{Braced(PreviewAppId)}", writable: true))
        {
            appId.SetValue(string.Empty, PreviewHandlerName, RegistryValueKind.String);
            appId.SetValue("DllSurrogate", PreviewSurrogate, RegistryValueKind.ExpandString);
        }

        using (RegistryKey handlers = _hive.CreateSubKey(PreviewHandlersPath, writable: true))
        {
            handlers.SetValue(Braced(PreviewHandlerClsid), PreviewHandlerName, RegistryValueKind.String);
        }

        WriteClass(ThumbnailProviderClsid, ThumbnailProviderName, dll, appId: null);

        // 32-bit applications read CLSIDs from the 32-bit view (Classes\Wow6432Node\CLSID) but share the file-type
        // bindings: without an x86 class they would find Mavue's binding and fail (REGDB_E_CLASSNOTREG, measured).
        string dll32 = Path.Combine(Path.GetDirectoryName(dll)!, X86Folder, DllName);
        if (File.Exists(dll32))
        {
            WriteClass(PreviewHandlerClsid, PreviewHandlerName, dll32, PreviewHost32AppId, Wow64ClassesPath);
            WriteClass(ThumbnailProviderClsid, ThumbnailProviderName, dll32, appId: null, Wow64ClassesPath);
        }
        else
        {
            DeleteTree($@"{Wow64ClassesPath}\CLSID\{Braced(PreviewHandlerClsid)}");
            DeleteTree($@"{Wow64ClassesPath}\CLSID\{Braced(ThumbnailProviderClsid)}");
        }

        var previewAdded = new List<string>();
        var previewReplaced = new List<string>();
        var previewKept = new List<string>();
        foreach (string extension in PreviewExtensions)
        {
            bool takeOver = takeOverPreviewFor?.Contains(extension, StringComparer.OrdinalIgnoreCase) == true;
            switch (Bind(extension, PreviewHandlerIid, PreviewHandlerClsid, takeOver))
            {
                case BindResult.Added: previewAdded.Add(extension); break;
                case BindResult.Replaced: previewReplaced.Add(extension); break;
                default: previewKept.Add(extension); break;
            }
        }

        var thumbnailAdded = new List<string>();
        var thumbnailKept = new List<string>();
        foreach (string extension in ThumbnailExtensions)
        {
            (Bind(extension, ThumbnailProviderIid, ThumbnailProviderClsid, takeOver: false) == BindResult.Added ? thumbnailAdded : thumbnailKept).Add(extension);
        }

        foreach (string progId in ProgIds)
        {
            SetShellEx($@"{ClassesPath}\{progId}", PreviewHandlerIid, PreviewHandlerClsid);
        }

        return new ShellHandlerSummary(previewAdded, previewReplaced, previewKept, thumbnailAdded, thumbnailKept);
    }

    /// <summary>Removes everything <see cref="Register"/> wrote (and restores saved values). Returns the number of keys and values removed.</summary>
    public int Unregister()
    {
        int removed = 0;
        foreach (Guid clsid in new[] { PreviewHandlerClsid, ThumbnailProviderClsid })
        {
            removed += DeleteTree($@"{ClassesPath}\CLSID\{Braced(clsid)}");
            removed += DeleteTree($@"{Wow64ClassesPath}\CLSID\{Braced(clsid)}");
        }

        removed += DeleteTree($@"{ClassesPath}\AppID\{Braced(PreviewAppId)}");
        using (RegistryKey? handlers = _hive.OpenSubKey(PreviewHandlersPath, writable: true))
        {
            if (handlers?.GetValue(Braced(PreviewHandlerClsid)) is not null)
            {
                handlers.DeleteValue(Braced(PreviewHandlerClsid), throwOnMissingValue: false);
                removed++;
            }
        }

        using (RegistryKey? classes = _hive.OpenSubKey(ClassesPath, writable: false))
        {
            foreach (string name in classes?.GetSubKeyNames() ?? [])
            {
                if (name.StartsWith('.') || ProgIds.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    removed += RemoveBindings($@"{ClassesPath}\{name}", restore: name.StartsWith('.'));
                }
            }
        }

        using (RegistryKey? associations = _hive.OpenSubKey($@"{ClassesPath}\SystemFileAssociations", writable: false))
        {
            foreach (string extension in associations?.GetSubKeyNames() ?? [])
            {
                removed += RemoveBindings($@"{ClassesPath}\SystemFileAssociations\{extension}", restore: false);
                DeleteIfEmpty($@"{ClassesPath}\SystemFileAssociations\{extension}");
            }
        }

        DeleteTree(BackupPath);
        DeleteIfEmpty($@"{_softwarePath}\{AppRegistration.ApplicationName}");
        return removed;
    }

    /// <summary>Extensions bound to Mavue's handler (preview or thumbnail) in this hive, at any level.</summary>
    public IReadOnlyList<string> RegisteredExtensions(Guid shellEx)
    {
        Guid clsid = shellEx == PreviewHandlerIid ? PreviewHandlerClsid : ThumbnailProviderClsid;
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string extension in shellEx == PreviewHandlerIid ? PreviewExtensions : ThumbnailExtensions)
        {
            if (ReadShellEx($@"{ClassesPath}\SystemFileAssociations\{extension}", shellEx) == clsid ||
                ReadShellEx($@"{ClassesPath}\{extension}", shellEx) == clsid)
            {
                result.Add(extension);
            }
        }

        return result.ToArray();
    }

    /// <summary>Extensions whose existing preview Mavue has taken over (per-user extension key), e.g. ".pdf".</summary>
    public IReadOnlyList<string> TakenOverPreviews() =>
        PreviewExtensions.Where(e => ReadShellEx($@"{ClassesPath}\{e}", PreviewHandlerIid) == PreviewHandlerClsid).ToArray();

    /// <summary>True when the COM classes are registered in this hive.</summary>
    public bool IsRegistered
    {
        get
        {
            using RegistryKey? key = _hive.OpenSubKey($@"{ClassesPath}\CLSID\{Braced(PreviewHandlerClsid)}\InprocServer32", writable: false);
            return key is not null;
        }
    }

    private enum BindResult
    {
        Added,
        Replaced,
        Kept,
    }

    private BindResult Bind(string extension, Guid shellEx, Guid clsid, bool takeOver)
    {
        string associationPath = $@"{ClassesPath}\SystemFileAssociations\{extension}";
        string extensionPath = $@"{ClassesPath}\{extension}";

        // Start from a clean state for this extension: an earlier registration must not count as "another handler".
        RemoveBinding(associationPath, shellEx, clsid, restore: false);
        RemoveBinding(extensionPath, shellEx, clsid, restore: true);

        Guid? current = _currentHandler(extension, shellEx);
        if (current is null || current == clsid)
        {
            SetShellEx(associationPath, shellEx, clsid);
            return BindResult.Added;
        }

        if (!takeOver)
        {
            return BindResult.Kept;
        }

        // Taking over: the per-user extension key comes before the machine-wide one (and SystemFileAssociations).
        string? previous = ReadShellExText(extensionPath, shellEx);
        if (previous is not null)
        {
            using RegistryKey backup = _hive.CreateSubKey(BackupPath, writable: true);
            backup.SetValue(BackupName(extension, shellEx), previous, RegistryValueKind.String);
        }

        SetShellEx(extensionPath, shellEx, clsid);
        if (_currentHandler(extension, shellEx) == clsid)
        {
            return BindResult.Replaced;
        }

        // Not effective (the user's default app binds its own handler at a higher level): leave everything as it was.
        RemoveBinding(extensionPath, shellEx, clsid, restore: true);
        return BindResult.Kept;
    }

    private static string BackupName(string extension, Guid shellEx) => $"{extension}|{Braced(shellEx)}";

    private void WriteClass(Guid clsid, string name, string dll, Guid? appId, string? classesPath = null)
    {
        using RegistryKey key = _hive.CreateSubKey($@"{classesPath ?? ClassesPath}\CLSID\{Braced(clsid)}", writable: true);
        key.SetValue(string.Empty, name, RegistryValueKind.String);
        key.SetValue("DisplayName", name, RegistryValueKind.String);
        if (appId is { } id)
        {
            key.SetValue("AppID", Braced(id), RegistryValueKind.String);
        }

        using RegistryKey server = key.CreateSubKey("InprocServer32", writable: true);
        server.SetValue(string.Empty, dll, RegistryValueKind.String);
        server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
    }

    private void SetShellEx(string path, Guid shellEx, Guid clsid)
    {
        using RegistryKey key = _hive.CreateSubKey($@"{path}\ShellEx\{Braced(shellEx)}", writable: true);
        key.SetValue(string.Empty, Braced(clsid), RegistryValueKind.String);
    }

    private string? ReadShellExText(string path, Guid shellEx)
    {
        using RegistryKey? key = _hive.OpenSubKey($@"{path}\ShellEx\{Braced(shellEx)}", writable: false);
        return key?.GetValue(string.Empty) as string;
    }

    private Guid? ReadShellEx(string path, Guid shellEx) => Guid.TryParse(ReadShellExText(path, shellEx), out Guid value) ? value : null;

    private int RemoveBindings(string path, bool restore)
    {
        int removed = 0;
        removed += RemoveBinding(path, PreviewHandlerIid, PreviewHandlerClsid, restore);
        removed += RemoveBinding(path, ThumbnailProviderIid, ThumbnailProviderClsid, restore);
        return removed;
    }

    /// <summary>Deletes <c>path\ShellEx\{shellEx}</c> when it names <paramref name="clsid"/>; restores a saved value.</summary>
    private int RemoveBinding(string path, Guid shellEx, Guid clsid, bool restore)
    {
        string keyPath = $@"{path}\ShellEx\{Braced(shellEx)}";
        if (ReadShellEx(path, shellEx) != clsid)
        {
            return 0;
        }

        string extension = path[(path.LastIndexOf('\\') + 1)..];
        string? saved = null;
        if (restore)
        {
            using RegistryKey? backup = _hive.OpenSubKey(BackupPath, writable: true);
            saved = backup?.GetValue(BackupName(extension, shellEx)) as string;
            if (saved is not null)
            {
                backup!.DeleteValue(BackupName(extension, shellEx), throwOnMissingValue: false);
            }
        }

        if (saved is not null)
        {
            using RegistryKey key = _hive.CreateSubKey(keyPath, writable: true);
            key.SetValue(string.Empty, saved, RegistryValueKind.String);
            return 1;
        }

        _hive.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        DeleteIfEmpty($@"{path}\ShellEx");
        DeleteIfEmpty(path);
        return 1;
    }

    private int DeleteTree(string path)
    {
        using (RegistryKey? key = _hive.OpenSubKey(path, writable: false))
        {
            if (key is null)
            {
                return 0;
            }
        }

        _hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        return 1;
    }

    private void DeleteIfEmpty(string path)
    {
        using (RegistryKey? key = _hive.OpenSubKey(path, writable: false))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }

        _hive.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    /// <summary>The handler Explorer resolves for the extension (default association, user choice included).</summary>
    public static Guid? QueryShellExtension(string extension, Guid shellEx)
    {
        ArgumentException.ThrowIfNullOrEmpty(extension);
        char[] buffer = new char[64];
        uint length = (uint)buffer.Length;
        int hr = AssocQueryStringW(0, 16 /* ASSOCSTR_SHELLEXTENSION */, extension, Braced(shellEx), buffer, ref length);
        return hr == 0 && Guid.TryParse(new string(buffer, 0, (int)Math.Max(0, length - 1)), out Guid clsid) ? clsid : null;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AssocQueryStringW(uint flags, int str, string association, string extra, [Out] char[] output, ref uint length);
}
