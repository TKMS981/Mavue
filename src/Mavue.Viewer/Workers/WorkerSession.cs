using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace Mavue.Viewer.Workers;

/// <summary>
/// One run of a worker, seen from the process that started it (Quick View or the main app; ARCHITECTURE §4.7.14–15):
/// the similar-image search (<c>--similar-worker</c>) or a converted export without metadata (<c>--export-worker</c>).
/// The worker is the calling executable itself, placed in a job object before it does any work:
/// <list type="bullet">
/// <item>memory limit for the whole job (<see cref="MemoryLimitBytes"/>): a hostile or huge image cannot take the
/// machine's memory; the worker fails instead, the calling app does not;</item>
/// <item>CPU hard cap (<see cref="CpuPercent"/> % of the machine) and below-normal priority;</item>
/// <item>at most one process (the worker cannot start others);</item>
/// <item>killed when the job handle closes (the app closes, crashes, or the work is cancelled).</item>
/// </list>
/// Lines (JSON) arrive on the UI thread. Nothing of the work stays in the calling process except its results.
/// </summary>
public sealed partial class WorkerSession : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private nint _job;
    private Process? _process;
    private bool _finished;

    public WorkerSession(DispatcherQueue dispatcher, long memoryLimitBytes, int cpuPercent)
    {
        _dispatcher = dispatcher;
        MemoryLimitBytes = memoryLimitBytes;
        CpuPercent = cpuPercent;
    }

    public long MemoryLimitBytes { get; }

    public int CpuPercent { get; }

    /// <summary>The most memory the worker's job used (read when the job is released; 0 before).</summary>
    public long PeakMemoryBytes { get; private set; }

    /// <summary>A line from the worker (UI thread).</summary>
    public event Action<string>? Line;

    /// <summary>The worker ended (exit code, or null when it could not start); UI thread.</summary>
    public event Action<int?>? Ended;

    /// <summary>Starts the worker in <paramref name="mode"/> and sends <paramref name="request"/> (one JSON line). False when it could not be started.</summary>
    public bool Start(string mode, string request)
    {
        string? self = Environment.ProcessPath;
        if (self is null)
        {
            return false;
        }

        try
        {
            _job = CreateJob(MemoryLimitBytes, CpuPercent);
            var info = new ProcessStartInfo(self, mode)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            _process = Process.Start(info);
            if (_process is null)
            {
                return false;
            }

            if (_job != 0 && !AssignProcessToJobObject(_job, _process.Handle))
            {
                // Without its limits the worker must not run.
                _process.Kill();
                return false;
            }

            _process.StandardInput.NewLine = "\n";
            _process.StandardInput.WriteLine(request);
            _process.StandardInput.Flush();
            Process process = _process;
            _ = Task.Run(() => ReadAsync(process));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Dispose();
            return false;
        }
    }

    /// <summary>Asks the worker to stop; it is killed if it has not ended within a second.</summary>
    public void Cancel()
    {
        if (_process is not { } process || _finished)
        {
            return;
        }

        try
        {
            process.StandardInput.WriteLine("cancel");
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Already gone.
        }

        _ = Task.Delay(1000).ContinueWith(_ => _dispatcher.TryEnqueue(Dispose), TaskScheduler.Default);
    }

    /// <summary>Ends the worker (if still running) and releases the job.</summary>
    public void Dispose()
    {
        _finished = true;
        try
        {
            if (_process is { HasExited: false } running)
            {
                running.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }

        _process?.Dispose();
        _process = null;
        if (_job != 0)
        {
            PeakMemoryBytes = Math.Max(PeakMemoryBytes, PeakJobMemory(_job));
            CloseHandle(_job); // kills anything left in the job
            _job = 0;
        }
    }

    private async Task ReadAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    if (!_finished || line.Contains("\"done\"", StringComparison.Ordinal))
                    {
                        Line?.Invoke(line);
                    }
                });
            }

            await process.WaitForExitAsync();
            int code = process.ExitCode;
            _dispatcher.TryEnqueue(() => Ended?.Invoke(code));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _dispatcher.TryEnqueue(() => Ended?.Invoke(null));
        }
    }

    // ------------------------------------------------------------------ job object

    private const int JobObjectExtendedLimitInformation = 9;
    private const int JobObjectCpuRateControlInformation = 15;
    private const uint LimitActiveProcess = 0x8;
    private const uint LimitPriorityClass = 0x20;
    private const uint LimitJobMemory = 0x200;
    private const uint LimitDieOnUnhandledException = 0x400;
    private const uint LimitKillOnJobClose = 0x2000;
    private const uint BelowNormalPriorityClass = 0x4000;
    private const uint CpuRateEnable = 0x1;
    private const uint CpuRateHardCap = 0x4;

    private static unsafe nint CreateJob(long memoryLimit, int cpuPercent)
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0)
        {
            return 0;
        }

        var limits = new JobExtendedLimits
        {
            Basic = new JobBasicLimits
            {
                LimitFlags = LimitActiveProcess | LimitPriorityClass | LimitJobMemory | LimitDieOnUnhandledException | LimitKillOnJobClose,
                ActiveProcessLimit = 1,
                PriorityClass = BelowNormalPriorityClass,
            },
            JobMemoryLimit = (nuint)memoryLimit,
        };
        bool ok = SetInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobExtendedLimits));
        var cpu = new JobCpuRate { ControlFlags = CpuRateEnable | CpuRateHardCap, CpuRate = (uint)Math.Clamp(cpuPercent * 100, 100, 10000) };
        ok &= SetInformationJobObject(job, JobObjectCpuRateControlInformation, &cpu, (uint)sizeof(JobCpuRate));
        if (!ok)
        {
            CloseHandle(job);
            return 0;
        }

        return job;
    }

    private static unsafe long PeakJobMemory(nint job)
    {
        JobExtendedLimits limits = default;
        return QueryInformationJobObject(job, JobObjectExtendedLimitInformation, &limits, (uint)sizeof(JobExtendedLimits), 0)
            ? (long)limits.PeakJobMemoryUsed
            : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimits
    {
        public JobBasicLimits Basic;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobCpuRate
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryInformationJobObject(nint job, int infoClass, void* info, uint length, nint returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
