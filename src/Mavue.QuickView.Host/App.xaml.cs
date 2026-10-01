using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Trigger;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mavue.QuickView.Host;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The Application lives for the whole process; owned resources are released in Shutdown().")]
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\Mavue.QuickView.Host.Instance";
    private const string ShutdownEventName = @"Local\Mavue.QuickView.Host.Shutdown";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _shutdownEvent;
    private QuickViewTimeline? _timeline;
    private KeyboardHook? _hook;
    private ForegroundWatcher? _foregroundWatcher;
    private SelectionWorker? _selectionWorker;
    private QuickViewController? _controller;
    private QuickViewWindow? _window;

    public App()
    {
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
        HostOptions options = HostOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToList());

        if (options.Shutdown)
        {
            if (EventWaitHandle.TryOpenExisting(ShutdownEventName, out EventWaitHandle? running))
            {
                running.Set();
                running.Dispose();
            }

            Exit();
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
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
        if (options.Activation is ActivationMode.HookGrant or ActivationMode.Auto)
        {
            uint self = (uint)System.Environment.ProcessId;
            _hook.AcceptedProbe = () => NativeMethods.AllowSetForegroundWindow(self);
        }

        _hook.Start();

        _foregroundWatcher = new ForegroundWatcher();
        _foregroundWatcher.ForegroundChanged += _controller.OnForegroundChanged;
        _foregroundWatcher.Start();

        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName);
        ThreadPool.RegisterWaitForSingleObject(_shutdownEvent, (_, _) => dispatcher.TryEnqueue(Shutdown), null, Timeout.Infinite, executeOnlyOnce: true);

        _timeline.Mark(0, "ready", QuickViewTimeline.Now, new Dictionary<string, object?> { ["hookInstalled"] = _hook.IsInstalled });
    }

    private async void Shutdown()
    {
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
