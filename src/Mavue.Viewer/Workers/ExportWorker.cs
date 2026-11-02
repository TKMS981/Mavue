using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mavue.Core.Workers;
using Mavue.Image.Export;
using Mavue.Image.Wic;
using Mavue.Metadata;

namespace Mavue.Viewer.Workers;

/// <summary>
/// The export worker: the Quick View host or Mavue.exe started with <c>--export-worker</c> by itself (ARCHITECTURE
/// §4.7.15), before WinUI starts. Converts one image (HEIF, AVIF, raw, TIFF…) to JPEG or PNG without metadata, inside
/// the job object of <see cref="WorkerSession"/> (memory cap: a 100-megapixel raw image needs about 400 MB of pixels),
/// at lower priority, and exits. It writes only the temporary file the caller named; the caller verifies it before
/// anything appears under the final name.
/// </summary>
public static class ExportWorker
{
    public const string Argument = "--export-worker";

    public static int Run()
    {
        WorkerPriority.Lower();
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        CleanExportRequest? request;
        try
        {
            string? line = input.ReadLine();
            request = line is null ? null : System.Text.Json.JsonSerializer.Deserialize(line, CleanExportJson.Default.CleanExportRequest);
        }
        catch (System.Text.Json.JsonException)
        {
            request = null;
        }

        if (request is null || !Path.IsPathFullyQualified(request.Source) || !Path.IsPathFullyQualified(request.Destination))
        {
            output.WriteLine(new CleanExportMessage { Type = CleanExportMessage.ErrorType, Code = "bad-request" }.ToJson());
            return 2;
        }

        using var cancel = new CancellationTokenSource();
        var watcher = new Thread(() =>
        {
            try
            {
                while (input.ReadLine() is { } command && !command.Contains("cancel", StringComparison.Ordinal))
                {
                }
            }
            catch (IOException)
            {
                // The host went away.
            }

            cancel.Cancel();
        })
        { IsBackground = true };
        watcher.Start();

        var clock = Stopwatch.StartNew();
        var options = new CleanExportOptions(
            new StripOptions(request.RemoveLocation, request.RemoveCaptureTime, request.RemoveCameraDetails, request.RemoveAuthorCopyright, request.KeepColorProfile),
            Enum.TryParse(request.Format, out ExportFormat format) ? format : ExportFormat.Jpeg,
            request.JpegQuality,
            request.MaxPixels);
        try
        {
            CleanExportResult result;
            using (var stream = new FileStream(request.Destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                result = CleanExport.WriteAsync(request.Source, stream, options, cancel.Token).GetAwaiter().GetResult();
            }

            output.WriteLine(new CleanExportMessage
            {
                Type = CleanExportMessage.DoneType,
                Container = result.Strip.Container.ToString(),
                Converted = result.Converted,
                Format = result.Format.ToString(),
                RemovedBytes = result.Strip.RemovedBytes,
                WroteExif = result.Strip.WroteExif,
                Milliseconds = Math.Round(clock.Elapsed.TotalMilliseconds),
            }.ToJson());
            return 0;
        }
        catch (OperationCanceledException)
        {
            TryDelete(request.Destination);
            return 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or ImageTooLargeException or IOException or UnauthorizedAccessException or InvalidDataException or COMException or OutOfMemoryException)
        {
            TryDelete(request.Destination);
            Console.Error.WriteLine($"{ex.GetType().Name} 0x{ex.HResult:X8} {ex.InnerException?.GetType().Name} 0x{ex.InnerException?.HResult ?? 0:X8}"); // no paths or contents
            string code = ex switch
            {
                NotSupportedException => "unsupported",
                ImageTooLargeException or OutOfMemoryException => "too-large",
                IOException or UnauthorizedAccessException => "unreadable",
                _ => "failed",
            };
            output.WriteLine(new CleanExportMessage { Type = CleanExportMessage.ErrorType, Code = code }.ToJson());
            return 3;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The host removes its work folder.
        }
    }
}
