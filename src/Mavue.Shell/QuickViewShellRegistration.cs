using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Microsoft.Win32;

namespace Mavue.Shell;

/// <summary>
/// Per-user (HKCU, no administrator rights) registration of the Quick View integration for unpackaged use:
/// <list type="bullet">
/// <item>"Mavue Quick View" in the classic context menu of supported files
/// (<c>Software\Classes\SystemFileAssociations\.ext\shell\Mavue.QuickView</c>). SystemFileAssociations
/// adds the command for the file type whatever program it is associated with, so no association or
/// default app is changed. On Windows 11 the classic menu is under "Show more options"; the top-level
/// menu needs package identity and a native IExplorerCommand (docs/WINDOWS-INTEGRATION.md §14).</item>
/// <item>Optionally, starting the resident process at sign-in (<c>Software\Microsoft\Windows\CurrentVersion\Run</c>).</item>
/// </list>
/// Only keys and values with Mavue's own names are created or deleted; nothing else is modified.
/// </summary>
public sealed partial class QuickViewShellRegistration
{
    /// <summary>Key name of the context-menu command and of the Run value.</summary>
    public const string DefaultVerbName = "Mavue.QuickView";

    /// <summary>Menu text. "Mavue Quick View" is the feature's product name, the same in every language.</summary>
    public const string MenuText = "Mavue Quick View";

    /// <summary>Formats Quick View can display (JPEG 2000 and SVG have no decoder yet, so they are not offered).</summary>
    public static readonly IReadOnlyList<FileFormat> SupportedFormats =
    [
        FileFormat.Pdf, FileFormat.Jpeg, FileFormat.Png, FileFormat.Gif, FileFormat.Bmp, FileFormat.Tiff,
        FileFormat.WebP, FileFormat.Ico, FileFormat.Heif, FileFormat.Avif, FileFormat.JpegXl,
    ];

    private const string RunSubPath = @"Microsoft\Windows\CurrentVersion\Run";

    private readonly RegistryKey _hive;
    private readonly string _softwarePath;
    private readonly string _verbName;

    /// <param name="hive">Normally <see cref="Registry.CurrentUser"/>.</param>
    /// <param name="softwarePath">Normally "Software"; tests use a private subkey.</param>
    /// <param name="verbName">Normally <see cref="DefaultVerbName"/>; the E2E harness uses its own name.</param>
    public QuickViewShellRegistration(RegistryKey hive, string softwarePath = "Software", string verbName = DefaultVerbName)
    {
        ArgumentNullException.ThrowIfNull(hive);
        ArgumentException.ThrowIfNullOrEmpty(softwarePath);
        if (string.IsNullOrEmpty(verbName) || verbName.IndexOfAny(['\\', '"', '/']) >= 0)
        {
            throw new ArgumentException("Invalid verb name.", nameof(verbName));
        }

        _hive = hive;
        _softwarePath = softwarePath;
        _verbName = verbName;
    }

    public static IReadOnlyList<string> Extensions { get; } =
        SupportedFormats.SelectMany(FileFormatDetector.ExtensionsOf).Order(StringComparer.Ordinal).ToArray();

    private string AssociationsPath => $@"{_softwarePath}\Classes\SystemFileAssociations";

    private string RunPath => $@"{_softwarePath}\{RunSubPath}";

    /// <summary>Command line Explorer runs for one selected file.</summary>
    public static string CommandFor(string hostPath) => $"\"{hostPath}\" --quickview \"%1\"";

    /// <summary>Adds the context-menu command (and optionally sign-in start) for <paramref name="hostPath"/>.</summary>
    public void Register(string hostPath, bool startAtSignIn)
    {
        ValidateHostPath(hostPath);
        foreach (string extension in Extensions)
        {
            using RegistryKey verb = _hive.CreateSubKey($@"{AssociationsPath}\{extension}\shell\{_verbName}", writable: true);
            verb.SetValue("MUIVerb", MenuText, RegistryValueKind.String);
            verb.SetValue("Icon", $"\"{hostPath}\",0", RegistryValueKind.String);

            // Player: the command is offered for up to 100 selected items (Document would stop at 15).
            // Explorer still starts the command once per item; the resident process merges them.
            verb.SetValue("MultiSelectModel", "Player", RegistryValueKind.String);
            using RegistryKey command = verb.CreateSubKey("command", writable: true);
            command.SetValue(string.Empty, CommandFor(hostPath), RegistryValueKind.String);
        }

        if (startAtSignIn)
        {
            using RegistryKey run = _hive.CreateSubKey(RunPath, writable: true);
            run.SetValue(_verbName, $"\"{hostPath}\"", RegistryValueKind.String);
        }

        NotifyAssociationsChanged();
    }

    /// <summary>Removes every Mavue Quick View command (any extension) and the sign-in entry.</summary>
    /// <returns>Number of keys and values removed.</returns>
    public int Unregister()
    {
        int removed = 0;
        using (RegistryKey? associations = _hive.OpenSubKey(AssociationsPath, writable: false))
        {
            foreach (string extension in associations?.GetSubKeyNames() ?? [])
            {
                string shellPath = $@"{AssociationsPath}\{extension}\shell";
                using RegistryKey? shell = _hive.OpenSubKey(shellPath, writable: true);
                if (shell?.OpenSubKey(_verbName) is { } existing)
                {
                    existing.Dispose();
                    shell.DeleteSubKeyTree(_verbName, throwOnMissingSubKey: false);
                    removed++;
                    DeleteIfEmpty(shellPath);
                    DeleteIfEmpty($@"{AssociationsPath}\{extension}");
                }
            }
        }

        using (RegistryKey? run = _hive.OpenSubKey(RunPath, writable: true))
        {
            if (run?.GetValue(_verbName) is not null)
            {
                run.DeleteValue(_verbName, throwOnMissingValue: false);
                removed++;
            }
        }

        NotifyAssociationsChanged();
        return removed;
    }

    /// <summary>Current state, for <c>--registration-status</c> and tests.</summary>
    public RegistrationStatus Status()
    {
        var extensions = new List<string>();
        var commands = new HashSet<string>(StringComparer.Ordinal);
        using (RegistryKey? associations = _hive.OpenSubKey(AssociationsPath, writable: false))
        {
            foreach (string extension in associations?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? command = _hive.OpenSubKey($@"{AssociationsPath}\{extension}\shell\{_verbName}\command", writable: false);
                if (command?.GetValue(string.Empty) is string text)
                {
                    extensions.Add(extension);
                    commands.Add(text);
                }
            }
        }

        using RegistryKey? run = _hive.OpenSubKey(RunPath, writable: false);
        return new RegistrationStatus(extensions.Order(StringComparer.Ordinal).ToArray(), commands.ToArray(), run?.GetValue(_verbName) as string);
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

    private static void ValidateHostPath(string hostPath)
    {
        if (string.IsNullOrEmpty(hostPath) || !Path.IsPathFullyQualified(hostPath) || hostPath.Contains('"', StringComparison.Ordinal) || !File.Exists(hostPath))
        {
            throw new ArgumentException("The Quick View host path must be an existing, fully qualified file.", nameof(hostPath));
        }
    }

    private void NotifyAssociationsChanged()
    {
        if (ReferenceEquals(_hive, Registry.CurrentUser) && _softwarePath == "Software")
        {
            SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0 /* SHCNF_IDLIST */, 0, 0);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}

/// <summary>What is registered: extensions with the command, the distinct command lines, and the sign-in entry.</summary>
public sealed record RegistrationStatus(IReadOnlyList<string> Extensions, IReadOnlyList<string> Commands, string? StartAtSignIn);
