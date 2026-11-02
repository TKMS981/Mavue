using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Mavue.App;

/// <summary>
/// Entry point (replaces the XAML-generated Main, which it otherwise repeats). <c>Mavue.exe --export-worker</c> is
/// the export worker of "Export without metadata" (ARCHITECTURE §4.7.15): the app starts itself in that mode, inside
/// a memory- and CPU-limited job, to convert HEIF, raw and other images; it runs before WinUI starts (no window).
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == Viewer.Workers.ExportWorker.Argument)
        {
            return Viewer.Workers.ExportWorker.Run();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
