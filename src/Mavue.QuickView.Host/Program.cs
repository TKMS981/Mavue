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

    /// <summary><c>--register [--no-startup]</c>, <c>--unregister</c>, <c>--registration-status</c> (per user, no administrator rights).</summary>
    private static int RunRegistration(HostOptions options)
    {
        AttachConsole(-1); // print to the console that started us (WinExe has none of its own)
        var registration = new QuickViewShellRegistration(Registry.CurrentUser, verbName: options.VerbName ?? QuickViewShellRegistration.DefaultVerbName);
        try
        {
            switch (options.Registration)
            {
                case RegistrationCommand.Register:
                    string host = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
                    registration.Register(host, startAtSignIn: !options.NoStartAtSignIn);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Registered \"{QuickViewShellRegistration.MenuText}\" for {QuickViewShellRegistration.Extensions.Count} file types; start at sign-in: {!options.NoStartAtSignIn}."));
                    break;
                case RegistrationCommand.Unregister:
                    int removed = registration.Unregister();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Removed {removed} registry entries."));
                    break;
                default:
                    RegistrationStatus status = registration.Status();
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Context menu: {status.Extensions.Count} file types{(status.Extensions.Count > 0 ? " (" + string.Join(' ', status.Extensions) + ")" : string.Empty)}"));
                    foreach (string command in status.Commands)
                    {
                        Console.WriteLine("  command: " + command);
                    }

                    Console.WriteLine("Start at sign-in: " + (status.StartAtSignIn ?? "no"));
                    break;
            }

            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            Console.Error.WriteLine("Registration failed: " + ex.Message);
            return 1;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}
