using System.Collections.Concurrent;

namespace Mavue.QuickView.Host;

/// <summary>
/// A dedicated single-threaded-apartment thread that runs queued work in order. Shell COM objects
/// (IShellWindows, IShellBrowser, shell items) are used only from such threads; see
/// ExplorerSelectionProvider for the measured MTA failure. Blocking waits on an STA thread pump COM
/// messages in .NET, so cross-process calls and their callbacks are serviced.
/// </summary>
internal sealed class StaThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public StaThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Queues fire-and-forget work.</summary>
    public void Post(Action action) => _work.Add(action);

    /// <summary>Runs <paramref name="func"/> on the STA thread and returns its result.</summary>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
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
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
    }

    private void Run()
    {
        foreach (Action action in _work.GetConsumingEnumerable())
        {
            action();
        }
    }
}
