using System.Diagnostics;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Trigger;

namespace Mavue.QuickView.Host;

/// <summary>
/// Keeps the Space hook effective over time (docs/QUICKVIEW-POC.md §8):
/// <list type="bullet">
/// <item>Windows calls the most recently installed low-level hook first. QuickLook lets Space through, so
/// when it starts after Mavue both tools preview the file (measured 3/3). With the default policy the hook
/// is re-installed whenever such a tool started after Mavue's hook, restoring Mavue's priority.</item>
/// <item>Windows silently removes a hook whose callback timed out; a periodic re-install restores it.</item>
/// <item>With <see cref="OtherQuickLookPolicy.Yield"/>, Space is left to the other tool while it runs.</item>
/// </list>
/// The check reads the process list every few seconds (cheap); Explorer selection is never polled.
/// </summary>
internal sealed class HookMaintenance : IDisposable
{
    /// <summary>Process names of tools known to take Space in Explorer.</summary>
    internal static readonly string[] KnownSpacePreviewTools = ["QuickLook", "Seer"];

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(60);

    private readonly KeyboardHook _hook;
    private readonly QuickViewTimeline _timeline;
    private readonly OtherQuickLookPolicy _policy;
    private readonly Timer _timer;
    private DateTime _lastInstallUtc = DateTime.UtcNow;
    private bool _otherToolSeen;

    public HookMaintenance(KeyboardHook hook, QuickViewTimeline timeline, OtherQuickLookPolicy policy)
    {
        _hook = hook;
        _timeline = timeline;
        _policy = policy;
        Check(null);
        _timer = new Timer(Check, null, CheckInterval, CheckInterval);
    }

    public void Dispose() => _timer.Dispose();

    private void Check(object? state)
    {
        DateTime? newestToolStart = null;
        string? toolName = null;
        foreach (string name in KnownSpacePreviewTools)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        DateTime started = process.StartTime.ToUniversalTime();
                        if (newestToolStart is null || started > newestToolStart)
                        {
                            newestToolStart = started;
                            toolName = name;
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        newestToolStart ??= DateTime.UtcNow; // cannot read (exited or elevated): assume it is new
                        toolName ??= name;
                    }
                }
            }
        }

        bool running = newestToolStart is not null;
        if (running != _otherToolSeen)
        {
            _otherToolSeen = running;
            _timeline.Mark(0, running ? "other-quicklook-detected" : "other-quicklook-gone", QuickViewTimeline.Now, new Dictionary<string, object?>
            {
                ["tool"] = toolName,
                ["policy"] = _policy.ToString(),
            });
        }

        if (_policy == OtherQuickLookPolicy.Yield)
        {
            _hook.SuspendSpace = running;
            return;
        }

        string? reason = newestToolStart > _lastInstallUtc ? "other-tool-started-later"
            : DateTime.UtcNow - _lastInstallUtc >= WatchdogInterval ? "watchdog"
            : null;
        if (reason is not null)
        {
            _hook.Reinstall();
            _lastInstallUtc = DateTime.UtcNow;
            _timeline.Mark(0, "hook-reinstalled", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = reason, ["tool"] = toolName });
        }
    }
}
