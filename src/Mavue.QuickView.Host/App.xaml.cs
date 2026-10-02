using Mavue.Core.Ipc;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Ipc;
using Mavue.QuickView.Trigger;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mavue.QuickView.Host;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The Application lives for the whole process; owned resources are released in Shutdown().")]
public partial class App : Application
{
    private readonly HostOptions _options;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _shutdownEvent;
    private QuickViewTimeline? _timeline;
    private KeyboardHook? _hook;
    private ForegroundWatcher? _foregroundWatcher;
    private SelectionWorker? _selectionWorker;
    private QuickViewController? _controller;
    private QuickViewWindow? _window;
    private HookMaintenance? _hookMaintenance;
    private QuickViewPipeServer? _pipeServer;
    private DispatcherQueue? _dispatcher;

    internal App(HostOptions options)
    {
        _options = options;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Keep the resident process alive on unexpected UI errors; the failure is logged by type only.
            _timeline?.Mark(0, "unhandled-exception", QuickViewTimeline.Now, new Dictionary<string, object?> { ["type"] = e.Exception.GetType().Name });
            e.Handled = true;
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        long launched = QuickViewTimeline.Now;
        HostOptions options = _options;

        _instanceMutex = new Mutex(initiallyOwned: true, Program.InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Exit();
            return;
        }

        _timeline = new QuickViewTimeline(options.TimingLog);
        _timeline.Mark(0, "launched", launched, new Dictionary<string, object?>
        {
            ["activation"] = options.Activation.ToString(),
            ["decoder"] = options.Decoder.ToString(),
            ["prewarm"] = options.Prewarm,
        });

        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;
        _window = new QuickViewWindow();
        _controller = new QuickViewController(_window, _timeline, options);
        if (options.Prewarm)
        {
            await _controller.PrewarmAsync();
        }

        _selectionWorker = new SelectionWorker(_timeline, dispatcher, _controller, options.TraceShell);
        if (options.Prewarm)
        {
            await _selectionWorker.WarmAsync();
        }

        var classifier = new SpaceKeyClassifier(new SpaceKeyClassifierOptions { IncludeFileDialogs = options.IncludeFileDialogs });
        _hook = new KeyboardHook(classifier, _selectionWorker.Enqueue);
        _hook.Classified += (context, reason) =>
        {
            // Diagnostics only for shell windows; Space presses in other applications are not recorded.
            if (context.ForegroundWindowClass is SpaceKeyClassifier.ExplorerWindowClass or SpaceKeyClassifier.DesktopProgmanClass or SpaceKeyClassifier.DesktopWorkerClass)
            {
                _timeline.Mark(0, "space-classified", QuickViewTimeline.Now, new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["focusClass"] = context.FocusWindowClass,
                    ["focusParentClass"] = context.FocusParentWindowClass,
                });
            }
        };
        _hook.EscapeHandler = _controller.HandleEscapeFromHook;
        _controller.Attach(_selectionWorker, _hook);
        if (options.Activation is ActivationMode.HookGrant or ActivationMode.Auto)
        {
            uint self = (uint)System.Environment.ProcessId;
            _hook.AcceptedProbe = () => NativeMethods.AllowSetForegroundWindow(self);
        }

        _hook.Start();
        _hookMaintenance = new HookMaintenance(_hook, _timeline, options.OtherQuickLook);

        _foregroundWatcher = new ForegroundWatcher();
        _foregroundWatcher.ForegroundChanged += _controller.OnForegroundChanged;
        _foregroundWatcher.Start();

        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShutdownEventName);
        ThreadPool.RegisterWaitForSingleObject(_shutdownEvent, (_, _) => dispatcher.TryEnqueue(Shutdown), null, Timeout.Infinite, executeOnlyOnce: true);

        Dictionary<string, object?> ready = QuickViewController.MemorySnapshot();
        ready["hookInstalled"] = _hook.IsInstalled;
        _timeline.Mark(0, "ready", QuickViewTimeline.Now, ready);

        StartPipeServer();
        if (options.QuickViewPaths.Count > 0)
        {
            // Started by the context menu with no resident process running: show the files ourselves.
            try
            {
                QueueExternal([.. options.QuickViewPaths.Select(Path.GetFullPath)], Program.StartupForeground);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                _timeline.Mark(0, "external-rejected", QuickViewTimeline.Now, new Dictionary<string, object?> { ["error"] = "invalid-path" });
            }
        }
    }

    /// <summary>Requests from <c>--quickview</c> processes (context menu) through the user-only named pipe.</summary>
    private void StartPipeServer()
    {
        try
        {
            _pipeServer = new QuickViewPipeServer(QuickViewPipe.NameForCurrentUser(), request =>
            {
                long received = QuickViewTimeline.Now;
                bool queued = _dispatcher!.TryEnqueue(DispatcherQueuePriority.High, () => QueueExternal(request.Paths, (nint)request.ForegroundWindow, received));
                return new QuickViewResponse(QuickViewRequest.CurrentVersion, queued, queued ? null : "busy");
            });
            _pipeServer.Faulted += kind => _timeline?.Mark(0, "pipe-fault", QuickViewTimeline.Now, new Dictionary<string, object?> { ["kind"] = kind });
            _pipeServer.Start();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Space still works; only the context-menu command is unavailable.
            _timeline?.Mark(0, "pipe-unavailable", QuickViewTimeline.Now, new Dictionary<string, object?> { ["error"] = ex.GetType().Name });
        }
    }

    /// <summary>
    /// UI thread. Explorer starts the context-menu command once per selected file (measured); each request is
    /// resolved at once. The first one normally finds the whole Explorer selection, and the others are then
    /// recognized as already shown (or, without an Explorer view, added to the list) by the controller.
    /// </summary>
    private void QueueExternal(IReadOnlyList<string> paths, nint foreground, long? receivedQpc = null)
    {
        _timeline?.Mark(0, "external-received", receivedQpc ?? QuickViewTimeline.Now, new Dictionary<string, object?> { ["count"] = paths.Count });
        _selectionWorker?.ResolveExternal(paths, foreground);
    }

    private async void Shutdown()
    {
        if (_pipeServer is not null)
        {
            await _pipeServer.DisposeAsync();
        }

        _hookMaintenance?.Dispose();
        _hook?.Dispose();
        _foregroundWatcher?.Dispose();
        _selectionWorker?.Dispose();
        _controller?.Dispose();
        _window?.CloseForExit();
        if (_timeline is not null)
        {
            _timeline.Mark(0, "exit", QuickViewTimeline.Now);
            await _timeline.DisposeAsync();
        }

        _shutdownEvent?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        Exit();
    }
}
