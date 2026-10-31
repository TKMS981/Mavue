using System.Runtime.InteropServices;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Resident;

namespace Mavue.QuickView.Host.Performance;

/// <summary>
/// "Always Ready, Never Always Working" for the resident process: the resource limits in force
/// (<see cref="PreviewResourcePolicy"/>, chosen from the machine's tier), giving memory back after Quick View
/// hides, and restarting after a crash. Nothing here runs on a timer: the tier is re-checked when Quick View opens
/// and when Windows reports an energy saver change.
/// </summary>
internal sealed class ResidentResources : IDisposable
{
    private readonly QuickViewTimeline _timeline;
    private readonly bool _idleTrim;
    private readonly int _logicalProcessors = Environment.ProcessorCount;
    private readonly long _totalMemory = TotalPhysicalMemory();
    private volatile bool _energySaver; // written by the energy saver event (thread pool)
    private bool _subscribed;

    public ResidentResources(QuickViewTimeline timeline, bool idleTrim)
    {
        _timeline = timeline;
        _idleTrim = idleTrim;
        Policy = PreviewResourcePolicy.High;
    }

    /// <summary>Limits in force now.</summary>
    public PreviewResourcePolicy Policy { get; private set; }

    /// <summary>The tier changed (raised from <see cref="Refresh"/>, on the UI thread).</summary>
    public event Action<PreviewResourcePolicy>? PolicyChanged;

    /// <summary>
    /// Re-evaluates the tier (a few microseconds: cached hardware facts, one system metric). Called when Quick View
    /// opens; the first call also starts listening for energy saver changes (an event, not a poll).
    /// </summary>
    public void Refresh()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            try
            {
                _energySaver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
                Windows.System.Power.PowerManager.EnergySaverStatusChanged += (_, _) =>
                    _energySaver = Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Not available (e.g. some server SKUs): treated as off.
            }
        }

        var snapshot = new SystemResourceSnapshot(
            _logicalProcessors,
            _totalMemory,
            _energySaver,
            NativeMethods.GetSystemMetrics(NativeMethods.SM_REMOTESESSION) != 0);
        PerformanceTier tier = PreviewResourcePolicy.SelectTier(snapshot);
        if (tier == Policy.Tier && _logged)
        {
            return;
        }

        _logged = true;
        Policy = PreviewResourcePolicy.For(tier);
        _timeline.Mark(0, "resource-tier", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["tier"] = tier.ToString(),
            ["logicalProcessors"] = snapshot.LogicalProcessors,
            ["memoryGb"] = Math.Round(snapshot.TotalMemoryBytes / (1024.0 * 1024 * 1024), 1),
            ["energySaver"] = snapshot.EnergySaver,
            ["remote"] = snapshot.RemoteSession,
        });
        PolicyChanged?.Invoke(Policy);
    }

    private bool _logged;

    /// <summary>
    /// Records (and optionally trims) the resident memory once Quick View has been hidden for a moment. Decoding
    /// creates large short-lived buffers; while hidden the process should give that memory back.
    /// </summary>
    /// <param name="isVisible">Checked after the delay: nothing happens if Quick View opened again.</param>
    public async Task TrimAfterHideAsync(long requestId, Func<bool> isVisible)
    {
        await Task.Delay(1000);
        if (isVisible())
        {
            return;
        }

        Dictionary<string, object?> before = MemorySnapshot();
        if (_idleTrim)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        Dictionary<string, object?> after = MemorySnapshot();
        foreach ((string k, object? v) in after)
        {
            before["after_" + k] = v;
        }

        before["trimmed"] = _idleTrim;
        _timeline.Mark(requestId, "idle-memory", QuickViewTimeline.Now, before);
    }

    public static Dictionary<string, object?> MemorySnapshot()
    {
        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new Dictionary<string, object?>
        {
            ["managedHeapMb"] = Math.Round(gc.HeapSizeBytes / 1048576.0, 1),
            ["managedCommittedMb"] = Math.Round(gc.TotalCommittedBytes / 1048576.0, 1),
            ["workingSetMb"] = Math.Round(self.WorkingSet64 / 1048576.0, 1),
            ["privateMb"] = Math.Round(self.PrivateMemorySize64 / 1048576.0, 1),
        };
    }

    /// <summary>
    /// Asks Windows (Windows Error Reporting) to start the resident process again if it crashes or hangs, so a
    /// malformed file that brings down a native decoder does not leave Space without Quick View until the next
    /// sign-in. Windows restarts only processes that ran for at least 60 seconds, which also prevents a restart loop.
    /// The original options are kept, except files to show (see <see cref="RestartArguments"/>).
    /// </summary>
    public void RegisterRestart(IReadOnlyList<string> originalArguments)
    {
        string? arguments = RestartArguments.Build(originalArguments);
        int hresult = NativeMethods.RegisterApplicationRestart(arguments, NativeMethods.RESTART_NO_PATCH | NativeMethods.RESTART_NO_REBOOT);
        _timeline.Mark(0, "restart-registered", QuickViewTimeline.Now, new Dictionary<string, object?> { ["hresult"] = hresult, ["arguments"] = arguments is not null });
    }

    public void Dispose()
    {
        PolicyChanged = null;
    }

    private static long TotalPhysicalMemory()
    {
        var status = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        return NativeMethods.GlobalMemoryStatusEx(ref status) ? (long)Math.Min(status.ullTotalPhys, long.MaxValue) : 0;
    }
}
