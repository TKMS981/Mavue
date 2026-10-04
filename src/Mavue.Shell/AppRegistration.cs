using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Microsoft.Win32;

namespace Mavue.Shell;

/// <summary>Texts written into the registration (in the user's language at the time of registering).</summary>
/// <param name="OpenCommand">Context-menu command, e.g. "Open in Mavue".</param>
/// <param name="Description">Shown in Settings › Default apps.</param>
/// <param name="ImageType">File type name of images opened with Mavue.</param>
/// <param name="PdfType">File type name of PDF documents.</param>
/// <param name="VideoType">File type name of videos.</param>
/// <param name="AudioType">File type name of audio files.</param>
public sealed record AppRegistrationText(string OpenCommand, string Description, string ImageType, string PdfType, string VideoType, string AudioType);

/// <summary>
/// Per-user (HKCU, no administrator rights) registration of the main application for unpackaged use, following
/// Microsoft Learn "Registering an application for use with File Explorer" / "Default Programs":
/// <list type="bullet">
/// <item>App Paths (Mavue.exe can be started by name, and Quick View finds it for "Open in Mavue").</item>
/// <item>ProgIDs Mavue.Image / Mavue.Pdf / Mavue.Video / Mavue.Audio and, for each viewable extension,
/// <c>OpenWithProgids</c>: Mavue appears in "Open with". No default is changed — Windows lets only the user choose
/// the default app (Settings › Default apps, where Mavue appears through <c>RegisteredApplications</c>).</item>
/// <item>"Open in Mavue" in the classic context menu of viewable files (SystemFileAssociations, for one selected file).</item>
/// <item>The File Explorer preview handler and thumbnail provider (<see cref="ShellHandlerRegistration"/>) when
/// Mavue.Shell.Preview.dll is next to Mavue.exe.</item>
/// </list>
/// Only keys and values with Mavue's own names are created or deleted.
/// </summary>
public sealed partial class AppRegistration
{
    public const string ExecutableName = "Mavue.exe";

    /// <summary>Name in RegisteredApplications and of the Capabilities key.</summary>
    public const string ApplicationName = "Mavue";

    /// <summary>Key name of the "Open in Mavue" context-menu command.</summary>
    public const string OpenVerbName = "Mavue.Open";

    public const string ImageProgId = "Mavue.Image";
    public const string PdfProgId = "Mavue.Pdf";
    public const string VideoProgId = "Mavue.Video";
    public const string AudioProgId = "Mavue.Audio";

    private readonly RegistryKey _hive;
    private readonly string _softwarePath;

    /// <param name="hive">Normally <see cref="Registry.CurrentUser"/>.</param>
    /// <param name="softwarePath">Normally "Software"; tests use a private subkey.</param>
    /// <param name="currentHandler">
    /// Which shell handler Explorer uses today (see <see cref="ShellHandlerRegistration"/>); default: the real lookup
    /// for the user's own registry, "none" for any other location (tests).
    /// </param>
    public AppRegistration(RegistryKey hive, string softwarePath = "Software", Func<string, Guid, Guid?>? currentHandler = null)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrEmpty(softwarePath);
        _hive = hive;
        _softwarePath = softwarePath;
        bool real = ReferenceEquals(hive, Registry.CurrentUser) && softwarePath == "Software";
        Handlers = new ShellHandlerRegistration(hive, softwarePath, currentHandler ?? (real ? ShellHandlerRegistration.QueryShellExtension : (_, _) => null));
    }

    /// <summary>The preview handler and thumbnail provider part of the registration.</summary>
    public ShellHandlerRegistration Handlers { get; }

    /// <summary>Every extension the viewer opens, with the ProgID it is registered under.</summary>
    public static IReadOnlyDictionary<string, string> ProgIdsByExtension { get; } = ViewerFormats.All
        .SelectMany(format => FileFormatDetector.ExtensionsOf(format).Select(extension => (extension, ProgIdFor(format))))
        .GroupBy(p => p.extension, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase);

    private string ClassesPath => $@"{_softwarePath}\Classes";

    private string AppPathsPath => $@"{_softwarePath}\Microsoft\Windows\CurrentVersion\App Paths\{ExecutableName}";

    private string CapabilitiesPath => $@"{_softwarePath}\{ApplicationName}\Capabilities";

    private string RegisteredApplicationsPath => $@"{_softwarePath}\RegisteredApplications";

    /// <summary>Command line Explorer runs to open a file.</summary>
    public static string OpenCommandFor(string executable) => $"\"{executable}\" \"%1\"";

    public static string ProgIdFor(FileFormat format) =>
        format == FileFormat.Pdf ? PdfProgId
        : FileFormatKinds.IsVideo(format) ? VideoProgId
        : FileFormatKinds.IsAudio(format) ? AudioProgId
        : ImageProgId;

    /// <summary>
    /// Registers <paramref name="executable"/> (Mavue.exe) for the current user; replaces an earlier registration.
    /// Returns what the preview/thumbnail registration did, or null when Mavue.Shell.Preview.dll is not next to it.
    /// </summary>
    /// <param name="takeOverPreviewFor">
    /// Extensions whose existing preview handler Mavue's replaces (opt-in, e.g. ".pdf"; <see cref="ShellHandlerRegistration.PreviewExtensions"/>
    /// for all). Null: none — earlier take-overs are given back.
    /// </param>
    public ShellHandlerSummary? Register(string executable, AppRegistrationText text, IReadOnlyCollection<string>? takeOverPreviewFor = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(executable) || !Path.IsPathFullyQualified(executable) || executable.Contains('"', StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(executable), ExecutableName, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
        {
            throw new ArgumentException($"The path must be an existing, fully qualified {ExecutableName}.", nameof(executable));
        }

        string command = OpenCommandFor(executable);
        string icon = $"\"{executable}\",0";

        using (RegistryKey appPath = _hive.CreateSubKey(AppPathsPath, writable: true))
        {
            appPath.SetValue(string.Empty, executable, RegistryValueKind.String);
            appPath.SetValue("Path", Path.GetDirectoryName(executable)!, RegistryValueKind.String);
        }

        foreach ((string progId, string name) in new[] { (ImageProgId, text.ImageType), (PdfProgId, text.PdfType), (VideoProgId, text.VideoType), (AudioProgId, text.AudioType) })
        {
            using RegistryKey key = _hive.CreateSubKey($@"{ClassesPath}\{progId}", writable: true);
            key.SetValue(string.Empty, name, RegistryValueKind.String);
            key.SetValue("FriendlyTypeName", name, RegistryValueKind.String);
            using (RegistryKey defaultIcon = key.CreateSubKey("DefaultIcon", writable: true))
            {
                defaultIcon.SetValue(string.Empty, icon, RegistryValueKind.String);
            }

            using RegistryKey open = key.CreateSubKey(@"shell\open\command", writable: true);
            open.SetValue(string.Empty, command, RegistryValueKind.String);
        }

        using (RegistryKey application = _hive.CreateSubKey($@"{ClassesPath}\Applications\{ExecutableName}", writable: true))
        {
            application.SetValue("FriendlyAppName", ApplicationName, RegistryValueKind.String);
            using (RegistryKey open = application.CreateSubKey(@"shell\open\command", writable: true))
            {
                open.SetValue(string.Empty, command, RegistryValueKind.String);
            }

            using RegistryKey supported = application.CreateSubKey("SupportedTypes", writable: true);
            foreach (string extension in ProgIdsByExtension.Keys)
            {
                supported.SetValue(extension, string.Empty, RegistryValueKind.String);
            }
        }

        using (RegistryKey capabilities = _hive.CreateSubKey(CapabilitiesPath, writable: true))
        {
            capabilities.SetValue("ApplicationName", ApplicationName, RegistryValueKind.String);
            capabilities.SetValue("ApplicationDescription", text.Description, RegistryValueKind.String);
            capabilities.SetValue("ApplicationIcon", icon, RegistryValueKind.String);
            using RegistryKey associations = capabilities.CreateSubKey("FileAssociations", writable: true);
            foreach ((string extension, string progId) in ProgIdsByExtension)
            {
                associations.SetValue(extension, progId, RegistryValueKind.String);
            }
        }

        using (RegistryKey registered = _hive.CreateSubKey(RegisteredApplicationsPath, writable: true))
        {
            registered.SetValue(ApplicationName, $@"Software\{ApplicationName}\Capabilities", RegistryValueKind.String);
        }

        foreach ((string extension, string progId) in ProgIdsByExtension)
        {
            using (RegistryKey openWith = _hive.CreateSubKey($@"{ClassesPath}\{extension}\OpenWithProgids", writable: true))
            {
                openWith.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
            }

            using RegistryKey verb = _hive.CreateSubKey($@"{ClassesPath}\SystemFileAssociations\{extension}\shell\{OpenVerbName}", writable: true);
            verb.SetValue("MUIVerb", text.OpenCommand, RegistryValueKind.String);
            verb.SetValue("Icon", icon, RegistryValueKind.String);
            verb.SetValue("MultiSelectModel", "Single", RegistryValueKind.String); // one window per command (several files: Quick View)
            using RegistryKey verbCommand = verb.CreateSubKey("command", writable: true);
            verbCommand.SetValue(string.Empty, command, RegistryValueKind.String);
        }

        string dll = Path.Combine(Path.GetDirectoryName(executable)!, ShellHandlerRegistration.DllName);
        ShellHandlerSummary? handlers = null;
        if (File.Exists(dll))
        {
            handlers = Handlers.Register(dll, takeOverPreviewFor);
        }
        else
        {
            Handlers.Unregister(); // an earlier registration of another copy must not point at a missing DLL
        }

        NotifyAssociationsChanged();
        return handlers;
    }

    /// <summary>Removes everything <see cref="Register"/> wrote. Returns the number of keys and values removed.</summary>
    public int Unregister()
    {
        int removed = Handlers.Unregister();
        removed += DeleteTree(AppPathsPath);
        foreach (string progId in new[] { ImageProgId, PdfProgId, VideoProgId, AudioProgId })
        {
            removed += DeleteTree($@"{ClassesPath}\{progId}");
        }

        removed += DeleteTree($@"{ClassesPath}\Applications\{ExecutableName}");
        removed += DeleteTree($@"{_softwarePath}\{ApplicationName}\Capabilities");
        DeleteIfEmpty($@"{_softwarePath}\{ApplicationName}");
        using (RegistryKey? registered = _hive.OpenSubKey(RegisteredApplicationsPath, writable: true))
        {
            if (registered?.GetValue(ApplicationName) is not null)
            {
                registered.DeleteValue(ApplicationName, throwOnMissingValue: false);
                removed++;
            }
        }

        using (RegistryKey? classes = _hive.OpenSubKey(ClassesPath, writable: false))
        {
            foreach (string extension in classes?.GetSubKeyNames().Where(n => n.StartsWith('.')) ?? [])
            {
                string openWithPath = $@"{ClassesPath}\{extension}\OpenWithProgids";
                using (RegistryKey? openWith = _hive.OpenSubKey(openWithPath, writable: true))
                {
                    foreach (string progId in new[] { ImageProgId, PdfProgId, VideoProgId, AudioProgId })
                    {
                        if (openWith?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
                        {
                            openWith.DeleteValue(progId, throwOnMissingValue: false);
                            removed++;
                        }
                    }
                }

                DeleteIfEmpty(openWithPath);
                DeleteIfEmpty($@"{ClassesPath}\{extension}");
            }
        }

        using (RegistryKey? associations = _hive.OpenSubKey($@"{ClassesPath}\SystemFileAssociations", writable: false))
        {
            foreach (string extension in associations?.GetSubKeyNames() ?? [])
            {
                string shellPath = $@"{ClassesPath}\SystemFileAssociations\{extension}\shell";
                removed += DeleteTree($@"{shellPath}\{OpenVerbName}");
                DeleteIfEmpty(shellPath);
                DeleteIfEmpty($@"{ClassesPath}\SystemFileAssociations\{extension}");
            }
        }

        NotifyAssociationsChanged();
        return removed;
    }

    /// <summary>The registered Mavue.exe (App Paths), or null when Mavue is not registered.</summary>
    public string? RegisteredExecutable()
    {
        using RegistryKey? key = _hive.OpenSubKey(AppPathsPath, writable: false);
        return key?.GetValue(string.Empty) as string;
    }

    /// <summary>Extensions that have Mavue in their "Open with" list.</summary>
    public IReadOnlyList<string> RegisteredExtensions()
    {
        var result = new List<string>();
        foreach ((string extension, string progId) in ProgIdsByExtension)
        {
            using RegistryKey? openWith = _hive.OpenSubKey($@"{ClassesPath}\{extension}\OpenWithProgids", writable: false);
            if (openWith?.GetValueNames().Contains(progId, StringComparer.OrdinalIgnoreCase) == true)
            {
                result.Add(extension);
            }
        }

        return result.Order(StringComparer.Ordinal).ToArray();
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

    private void NotifyAssociationsChanged()
    {
        if (ReferenceEquals(_hive, Registry.CurrentUser) && _softwarePath == "Software")
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, 0, 0);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
