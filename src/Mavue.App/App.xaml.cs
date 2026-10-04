using Mavue.Shell;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace Mavue.App;

public partial class App : Application
{
    /// <summary>
    /// Command-line switch used by tools/smoke-test.ps1: start, render the first frame, verify
    /// localized resources resolve, then exit with code 0 (or 1 on failure).
    /// </summary>
    internal const string SmokeTestSwitch = "--smoke-test";

    /// <summary>Diagnostics for automated tests: <c>--trace-file &lt;path&gt;</c> writes viewer events as JSON Lines.</summary>
    internal const string TraceFileSwitch = "--trace-file";

    /// <summary>Tests: <c>--settings-file &lt;path&gt;</c> uses another settings file than the user's.</summary>
    internal const string SettingsFileSwitch = "--settings-file";

    /// <summary>Tests: links in documents are reported in the trace but not opened in the browser.</summary>
    internal const string NoLaunchSwitch = "--no-launch";

    /// <summary>
    /// <c>--register</c>: adds Mavue to "Open with", Settings › Default apps, the "Open in Mavue" context-menu command,
    /// and File Explorer's preview pane and thumbnails (where no other handler exists) for the current user;
    /// <c>--unregister</c> removes all of it (also available in Settings).
    /// </summary>
    internal const string RegisterSwitch = "--register";

    internal const string UnregisterSwitch = "--unregister";

    /// <summary>With <c>--register</c>: Mavue's preview also for file types that have another preview handler (e.g. PDF).</summary>
    internal const string PreferPreviewSwitch = "--prefer-mavue-preview";

    /// <summary>
    /// With <c>--register</c>: keeps the previews the user chose Mavue for (Settings, e.g. PDF) instead of giving them back.
    /// Used by the installer when it re-registers an updated version (tools/installer/Install.ps1).
    /// </summary>
    internal const string KeepPreviewChoicesSwitch = "--keep-preview-choices";

    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A failure in an event handler must not close the window with the user's work; the viewer itself
            // already turns decoding errors into messages.
            e.Handled = true;
        };
    }

    /// <summary>Texts written into the Windows registration, in the current language.</summary>
    internal static AppRegistrationText RegistrationText() => new(
        MainWindow.Text("Registration_OpenCommand"),
        MainWindow.Text("Registration_Description"),
        MainWindow.Text("Registration_ImageType"),
        MainWindow.Text("Registration_PdfType"),
        MainWindow.Text("Registration_VideoType"),
        MainWindow.Text("Registration_AudioType"));

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Contains(RegisterSwitch, StringComparer.OrdinalIgnoreCase) || commandLine.Contains(UnregisterSwitch, StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunRegistration(
                commandLine.Contains(RegisterSwitch, StringComparer.OrdinalIgnoreCase),
                commandLine.Contains(PreferPreviewSwitch, StringComparer.OrdinalIgnoreCase),
                commandLine.Contains(KeepPreviewChoicesSwitch, StringComparer.OrdinalIgnoreCase));
            Exit();
            return;
        }

        bool smokeTest = commandLine.Contains(SmokeTestSwitch, StringComparer.OrdinalIgnoreCase);
        LaunchArguments parsed = ParseArguments(commandLine.Skip(1).ToList());

        _window = new MainWindow(parsed.TraceFile, parsed.SettingsFile, commandLine.Contains(NoLaunchSwitch, StringComparer.OrdinalIgnoreCase));
        _window.Activate();

        if (smokeTest)
        {
            _window.RunSmokeTestAndExit();
            return;
        }

        // "Mavue.exe <file> [<file> ...]" — from Explorer's "Open with", a file association, or a shortcut.
        _window.OpenFiles(parsed.Files);
    }

    /// <summary>Files to open (full paths), the optional trace file and settings file; unknown switches are ignored.</summary>
    internal static LaunchArguments ParseArguments(IReadOnlyList<string> arguments)
    {
        var files = new List<string>();
        string? traceFile = null;
        string? settingsFile = null;
        for (int i = 0; i < arguments.Count; i++)
        {
            string argument = arguments[i];
            if (string.Equals(argument, TraceFileSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
            {
                traceFile = arguments[++i];
                continue;
            }

            if (string.Equals(argument, SettingsFileSwitch, StringComparison.OrdinalIgnoreCase) && i + 1 < arguments.Count)
            {
                settingsFile = arguments[++i];
                continue;
            }

            if (argument.StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(argument))
            {
                continue;
            }

            try
            {
                files.Add(Path.GetFullPath(argument));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Not a usable path; skip it.
            }
        }

        return new LaunchArguments(files, traceFile, settingsFile);
    }

    private static int RunRegistration(bool register, bool preferMavuePreview, bool keepPreviewChoices)
    {
        NativeConsole.Attach(); // print to the console that started us (WinExe has none of its own)
        if (PackageInfo.IsPackaged)
        {
            Console.WriteLine("Mavue is installed as a package: file types, preview pane and thumbnails come from the package (nothing to register).");
            return 0;
        }

        var registration = new AppRegistration(Registry.CurrentUser);
        try
        {
            if (register)
            {
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
                IReadOnlyCollection<string>? takeOver = preferMavuePreview ? ShellHandlerRegistration.PreviewExtensions
                    : keepPreviewChoices ? registration.Handlers.TakenOverPreviews() : null;
                ShellHandlerSummary? handlers = registration.Register(executable, RegistrationText(), takeOver);
                Console.WriteLine($"Registered {executable} for {registration.RegisteredExtensions().Count} file types (Open with, Default apps, \"{MainWindow.Text("Registration_OpenCommand")}\").");
                if (handlers is null)
                {
                    Console.WriteLine($"{ShellHandlerRegistration.DllName} is not next to Mavue.exe: no preview pane or thumbnails (build it with tools/build-native.ps1).");
                }
                else
                {
                    Console.WriteLine($"Preview pane: Mavue for {handlers.PreviewAdded.Count + handlers.PreviewReplaced.Count} file types" +
                        (handlers.PreviewReplaced.Count > 0 ? $" (taken over: {string.Join(' ', handlers.PreviewReplaced)})" : string.Empty) +
                        (handlers.PreviewKept.Count > 0 ? $"; kept the existing handler for {string.Join(' ', handlers.PreviewKept)}" : string.Empty) + ".");
                    Console.WriteLine($"Thumbnails: Mavue for {string.Join(' ', handlers.ThumbnailAdded)}; {handlers.ThumbnailKept.Count} file types keep their existing provider.");
                    Console.WriteLine("Restart File Explorer if it was showing these folders.");
                }
            }
            else
            {
                Console.WriteLine($"Removed {registration.Unregister()} registry entries.");
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            Console.Error.WriteLine("Registration failed: " + ex.Message);
            return 1;
        }
    }
}

/// <summary>What the command line asks for.</summary>
internal sealed record LaunchArguments(IReadOnlyList<string> Files, string? TraceFile, string? SettingsFile);

internal static partial class NativeConsole
{
    public static void Attach() => AttachConsole(-1);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
