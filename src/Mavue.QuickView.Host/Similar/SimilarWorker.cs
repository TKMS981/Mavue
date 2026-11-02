using System.Text;
using Mavue.Core.Similarity;
using Mavue.Image.Similarity;

namespace Mavue.QuickView.Host.Similar;

/// <summary>
/// The search worker: this executable started with <c>--similar-worker</c> by the resident Quick View process
/// (ARCHITECTURE §4.7.14). It runs before WinUI starts (no window, no hooks, no single-instance mutex), at lower CPU and
/// memory priority (<see cref="Mavue.Viewer.Workers.WorkerPriority"/>), inside a job object set up by <see cref="Mavue.Viewer.Workers.WorkerSession"/>
/// (memory and CPU caps, no child processes, killed with its parent). It reads one request line, writes JSON lines,
/// stops on "cancel" or when its input closes, and exits — releasing everything the search used.
/// </summary>
internal static class SimilarWorker
{
    public const string Argument = "--similar-worker";

    public static int Run()
    {
        Mavue.Viewer.Workers.WorkerPriority.Lower();
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var gate = new Lock();
        void Emit(SimilarSearchMessage message)
        {
            lock (gate)
            {
                output.WriteLine(message.ToJson());
            }
        }

        SimilarSearchRequest? request;
        try
        {
            string? line = input.ReadLine();
            request = line is null ? null : System.Text.Json.JsonSerializer.Deserialize(line, SimilarSearchJson.Default.SimilarSearchRequest);
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        if (request is null || !Path.IsPathFullyQualified(request.Target) || !Path.IsPathFullyQualified(request.Folder) || !Path.IsPathFullyQualified(request.CachePath))
        {
            Emit(new SimilarSearchMessage { Type = SimilarSearchMessage.ErrorType, Code = "bad-request" });
            return 2;
        }

        using var cancel = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is { } command)
                {
                    if (command.Contains("cancel", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
                // The host went away.
            }

            cancel.Cancel(); // "cancel", or the host closed our input
        })
        { IsBackground = true, Name = "Mavue similar search input" };
        watcher.Start();

        try
        {
            SimilarImageSearch.Run(request, Emit, cancel.Token);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            Emit(new SimilarSearchMessage { Type = SimilarSearchMessage.ErrorType, Code = "folder-unreadable" });
            return 3;
        }
    }
}
