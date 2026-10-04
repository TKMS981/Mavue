using System.Globalization;
using Mavue.Core.Ipc;
using Mavue.QuickView.Ipc;
using Mavue.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;

namespace Mavue.QuickView.Host;

/// <summary>
/// Entry point (replaces the XAML-generated Main). Requests that a running instance can serve —
/// <c>--quickview</c> from the context menu, <c>--shutdown</c> — and the registration commands are handled
/// here before WinUI starts, so these short-lived processes stay cheap.
/// </summary>
internal static partial class Program
{
    internal const string InstanceMutexName = @"Local\Mavue.QuickView.Host.Instance";
    internal const string ShutdownEventName = @"Local\Mavue.QuickView.Host.Shutdown";

    /// <summary>Foreground window when this process started (the Explorer window a context-menu command came from).</summary>
    internal static nint StartupForeground { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        StartupForeground = NativeMethods.GetForegroundWindow();
        HostOptions options = HostOptions.Parse(args);

        if (options.Registration != RegistrationCommand.None)
        {
            return RunRegistration(options);
        }

        if (options.Shutdown)
        {
            if (EventWaitHandle.TryOpenExisting(ShutdownEventName, out EventWaitHandle? running))
            {
                running.Set();
                running.Dispose();
            }

            return 0;
        }

        if (options.QuickViewPaths.Count > 0 && TryForwardToRunningInstance(options.QuickViewPaths))
        {
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(callback =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(options);
        });
        return 0;
    }

    /// <summary>
    /// Sends the paths to the resident process. Returns false only when no resident process exists, so this
    /// process starts as the resident process and shows them itself.
    /// </summary>
    private static bool TryForwardToRunningInstance(IReadOnlyList<string> paths)
    {
        if (!Mutex.TryOpenExisting(InstanceMutexName, out Mutex? instance))
        {
            return false;
        }

        instance.Dispose();
        string[] full;
        try
        {
            full = paths.Select(Path.GetFullPath).ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // nothing sensible to show
        }

        var request = new QuickViewRequest(QuickViewRequest.CurrentVersion, full, StartupForeground);
        try
        {
            // The resident process may still be starting (sign-in); give it a few seconds to open the pipe.
            _ = QuickViewPipe.SendAsync(QuickViewPipe.NameForCurrentUser(), request, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or UnauthorizedAccessException)
        {
            // A second resident process would exit on the instance mutex anyway; give up quietly.
        }

        return true;
    }

    /// <summary>
    /// Per-user registration (no administrator rights):
    /// <list type="bullet">
    /// <item><c>--register [--no-startup]</c>: classic context-menu command (and sign-in start). Skipped for the menu
    /// when the Windows 11 command is registered, so "Mavue Quick View" never appears twice.</item>
    /// <item><c>--register-modern-menu &lt;package.msix&gt;</c>: Windows 11 context-menu command (signed identity package
    /// with this folder as external location); removes the classic command.</item>
    /// <item><c>--unregister-modern-menu</c>, <c>--unregister</c> (everything), <c>--registration-status</c>,
    /// <c>--write-identity-manifest &lt;path&gt; [--publisher CN=...] [--package-version a.b.c.d]</c>.</item>
    /// </list>
    /// </summary>
    private static int RunRegistration(HostOptions options)
    {
        AttachConsole(-1); // print to the console that started us (WinExe has none of its own)
        var registration = new QuickViewShellRegistration(Registry.CurrentUser, verbName: options.VerbName ?? QuickViewShellRegistration.DefaultVerbName);
        var modern = new ModernContextMenuRegistration();
        try
        {
            string host = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
            if (PackageInfo.IsPackaged && options.Registration is RegistrationCommand.Register or RegistrationCommand.Unregister or RegistrationCommand.RegisterModernMenu or RegistrationCommand.UnregisterModernMenu)
            {
                // The MSIX package declares the context menu and the start at sign-in (StartupTask, Settings › Apps › Startup).
                Console.WriteLine("Mavue Quick View is installed as a package: its Windows integration comes from the package (nothing to register).");
                return 0;
            }

            switch (options.Registration)
            {
                case RegistrationCommand.Register:
                    if (options.VerbName is null && modern.IsRegistered)
                    {
                        if (!options.NoStartAtSignIn)
                        {
                            registration.RegisterStartAtSignIn(host);
                        }

                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The Windows 11 context-menu command is registered; the classic command was not added. Start at sign-in: {!options.NoStartAtSignIn}."));
                        break;
                    }

                    registration.Register(host, startAtSignIn: !options.NoStartAtSignIn);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Registered \"{QuickViewShellRegistration.MenuText}\" for {QuickViewShellRegistration.Extensions.Count} file types; start at sign-in: {!options.NoStartAtSignIn}."));
                    break;

                case RegistrationCommand.RegisterModernMenu:
                    string package = Path.GetFullPath(options.RegistrationPath!);
                    Task.Run(() => modern.RegisterAsync(package, Path.GetDirectoryName(host)!)).GetAwaiter().GetResult();
                    int classicRemoved = registration.UnregisterContextMenu();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Registered the Windows 11 context-menu command ({string.Join(", ", modern.RegisteredPackages())}); removed {classicRemoved} classic menu entries. Restart File Explorer if the command does not appear."));
                    break;

                case RegistrationCommand.UnregisterModernMenu:
                    int packagesRemoved = Task.Run(modern.UnregisterAsync).GetAwaiter().GetResult();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Removed {packagesRemoved} identity package(s). Run --register to use the classic context menu."));
                    break;

                case RegistrationCommand.Unregister:
                    int removed = registration.Unregister();
                    int removedPackages = options.VerbName is null ? Task.Run(modern.UnregisterAsync).GetAwaiter().GetResult() : 0;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Removed {removed} registry entries and {removedPackages} identity package(s)."));
                    break;

                case RegistrationCommand.WritePackageManifest:
                    Version packageVersion = options.PackageVersion ?? IdentityPackageManifest.VersionFor(DateTime.UtcNow);
                    string full = MsixPackageManifest.Create(options.Publisher, packageVersion, options.PackageArchitecture);
                    string fullTarget = Path.GetFullPath(options.RegistrationPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                    File.WriteAllText(fullTarget, full, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {fullTarget} (full package, publisher {options.Publisher}, version {packageVersion.ToString(4)}, {options.PackageArchitecture})."));
                    break;

                case RegistrationCommand.WriteIdentityManifest:
                    Version version = options.PackageVersion ?? IdentityPackageManifest.VersionFor(DateTime.UtcNow);
                    string manifest = IdentityPackageManifest.Create(options.Publisher, version, QuickViewShellRegistration.Extensions);
                    string target = Path.GetFullPath(options.RegistrationPath!);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, manifest, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Wrote {target} (publisher {options.Publisher}, version {version.ToString(4)})."));
                    break;

                default:
                    RegistrationStatus status = registration.Status();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Classic context menu: {status.Extensions.Count} file types{(status.Extensions.Count > 0 ? " (" + string.Join(' ', status.Extensions) + ")" : string.Empty)}"));
                    foreach (string command in status.Commands)
                    {
                        Console.WriteLine("  command: " + command);
                    }

                    IReadOnlyList<string> packages = modern.RegisteredPackages();
                    Console.WriteLine("Windows 11 context menu: " + (packages.Count > 0 ? string.Join(", ", packages) : "no"));
                    Console.WriteLine("Start at sign-in: " + (status.StartAtSignIn ?? "no"));
                    break;
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine("Registration failed: " + ex.Message);
            return 1;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
