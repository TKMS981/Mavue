using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Mavue.QuickView.Host;

/// <summary>
/// A dedicated single-threaded-apartment thread with a real Win32 message loop. Shell COM objects
/// (IShellWindows, IShellBrowser, shell items) are used only from such threads (ExplorerSelectionProvider
/// documents the measured MTA failure), and Explorer's events (DShellFolderViewEvents, DWebBrowserEvents2)
/// arrive as incoming cross-process COM calls that an STA only receives while it dispatches messages.
/// Queued work and incoming calls are processed in order on this one thread; there is no polling.
/// </summary>
internal sealed partial class StaThread : IDisposable
{
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x0004;
    private const uint PmRemove = 0x0001;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitObject0 = 0;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stopping;

    public StaThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Queues fire-and-forget work.</summary>
    public void Post(Action action)
    {
        _work.Enqueue(action);
        _signal.Set();
    }

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    public void Dispose()
    {
        _stopping = true;
        _signal.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _signal.Dispose();
    }

    private void Run()
    {
        nint handle = _signal.SafeWaitHandle.DangerousGetHandle();
        while (true)
        {
            uint result = MsgWaitForMultipleObjectsEx(1, ref handle, Infinite, QsAllInput, MwmoInputAvailable);
            if (result == WaitObject0)
            {
                while (_work.TryDequeue(out Action? action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        // Work items handle their own errors; never let one kill the shell thread.
                        System.Diagnostics.Debug.WriteLine($"Mavue STA work error: {ex.GetType().Name}");
                    }
                }

                if (_stopping)
                {
                    return;
                }
            }

            // Dispatch window messages, including the ones COM uses to deliver incoming calls.
            while (PeekMessageW(out Msg message, 0, 0, 0, PmRemove))
            {
                TranslateMessage(message);
                DispatchMessageW(message);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial uint MsgWaitForMultipleObjectsEx(uint count, ref nint handles, uint milliseconds, uint wakeMask, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg message, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(in Msg message);
}
