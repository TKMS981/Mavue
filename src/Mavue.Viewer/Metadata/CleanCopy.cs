using Mavue.Core.IO;
using Mavue.Core.Viewing;
using Mavue.Core.Workers;
using Mavue.Image.Export;
using Mavue.Image.Wic;
using Mavue.Metadata;
using Mavue.Viewer.Workers;
using Microsoft.UI.Dispatching;

namespace Mavue.Viewer.Metadata;

/// <summary>A finished copy.</summary>
public sealed record CleanCopyResult(string Path, long RemovedBytes, bool Converted, bool KeptMarkOfTheWeb);

/// <summary>
/// Makes a copy of an image without its private metadata (SPEC §3.1 "Copy without private metadata", §7 "metadata
/// removal"; ARCHITECTURE §4.7.13 and §4.7.15). Shared by Quick View's check before sharing and the main app's
/// "Export without metadata" (the rules — names, messages, limits — are <see cref="CleanCopyRules"/>). The original is
/// only read and is never the destination. JPEG, PNG, WebP and GIF are copied without re-encoding, in this process
/// (streamed; little memory). Other types (HEIF, AVIF, raw, TIFF…) and conversions the user chose are made by the export
/// worker (the calling executable started with <c>--export-worker</c> in a memory- and CPU-limited job). Either way the
/// copy is written through <see cref="SafeFileWriter"/> and verified before it appears under its name
/// (<see cref="CleanExport.Verify"/>: none of the removed kinds left, no other metadata blocks, decodable with the
/// expected upright size); otherwise nothing is written. The original's Mark-of-the-Web is carried over.
/// </summary>
public sealed class CleanCopy
{
    private readonly DispatcherQueue _dispatcher;

    /// <param name="temporaryRoot">This app's private folder for clipboard copies and the worker's output.</param>
    /// <param name="dispatcher">The UI thread (worker lines and cancellation).</param>
    public CleanCopy(string temporaryRoot, DispatcherQueue dispatcher)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryRoot);
        ArgumentNullException.ThrowIfNull(dispatcher);
        TemporaryRoot = temporaryRoot;
        _dispatcher = dispatcher;
    }

    public string TemporaryRoot { get; }

    /// <summary>A new private folder for a clipboard copy (keeps the original's file name for the recipient).</summary>
    public string TemporaryPathFor(string original, string extension)
    {
        CleanCopyRules.DeleteOldTemporaryCopies(TemporaryRoot, DateTime.UtcNow);
        string folder = Path.Combine(TemporaryRoot, DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileNameWithoutExtension(original) + extension);
    }

    /// <exception cref="SafeWriteValidationException">The copy did not pass the verification (nothing was written).</exception>
    /// <exception cref="CleanExportException">The conversion failed (no codec, too large…).</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is the original.</exception>
    public async Task<CleanCopyResult> CreateAsync(string original, string destination, CleanExportOptions options, CleanExportPlan plan, PerformanceTier tier, CancellationToken cancellation)
    {
        ArgumentException.ThrowIfNullOrEmpty(original);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(plan);
        if (CleanCopyRules.IsOriginal(original, destination))
        {
            throw new ArgumentException("The original cannot be the destination.", nameof(destination));
        }

        (uint Width, uint Height)? size = await Task.Run(() => WicPreviewDecoder.TryReadDimensions(original), cancellation);
        long removed;
        bool converted = plan.Converts(options.Format);
        if (!converted)
        {
            StripResult? strip = null;
            await SafeFileWriter.WriteAsync(
                destination,
                (output, ct) => Task.Run(
                    () =>
                    {
                        using var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                        strip = MetadataStripper.Strip(input, output, options.Strip);
                    },
                    ct),
                (written, ct) => Task.Run(() => CleanExport.Verify(written, options.Strip, size), ct),
                cancellationToken: cancellation);
            removed = strip!.RemovedBytes;
        }
        else
        {
            // Converted by the worker into a private temporary file, then verified and copied into place.
            string work = Path.Combine(TemporaryRoot, CleanCopyRules.WorkFolder);
            Directory.CreateDirectory(work);
            string temporary = Path.Combine(work, Guid.NewGuid().ToString("N") + plan.ExtensionFor(options.Format));
            try
            {
                CleanExportMessage answer = await RunWorkerAsync(original, temporary, options, tier, cancellation);
                if (answer.Type != CleanExportMessage.DoneType)
                {
                    throw new CleanExportException(answer.Code ?? "failed");
                }

                await SafeFileWriter.WriteAsync(
                    destination,
                    async (output, ct) =>
                    {
                        await using var input = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                        await input.CopyToAsync(output, ct);
                    },
                    (written, ct) => Task.Run(() => CleanExport.Verify(written, options.Strip, size), ct),
                    cancellationToken: cancellation);
                removed = answer.RemovedBytes ?? 0;
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted with the work folder later.
                }
            }
        }

        bool motw = CopyMarkOfTheWeb(original, destination);
        return new CleanCopyResult(destination, removed, converted, motw);
    }

    /// <summary>Runs one conversion in the export worker (job-limited process) and returns its answer.</summary>
    private async Task<CleanExportMessage> RunWorkerAsync(string original, string temporary, CleanExportOptions options, PerformanceTier tier, CancellationToken cancellation)
    {
        long memory = CleanCopyRules.WorkerMemoryLimit(tier);
        var request = new CleanExportRequest(
            original,
            temporary,
            options.Strip.RemoveLocation,
            options.Strip.RemoveCaptureTime,
            options.Strip.RemoveCameraDetails,
            options.Strip.RemoveAuthorCopyright,
            options.Strip.KeepColorProfile,
            options.Format.ToString(),
            options.JpegQuality,
            Math.Min(options.MaxPixels, memory / 8)); // pixels and the encoder's copy fit under the job limit
        var answer = new TaskCompletionSource<CleanExportMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new WorkerSession(_dispatcher, memory, cpuPercent: 50);
        session.Line += line =>
        {
            if (CleanExportMessage.FromJson(line) is { } message)
            {
                answer.TrySetResult(message);
            }
        };
        session.Ended += code => answer.TrySetResult(new CleanExportMessage { Type = CleanExportMessage.ErrorType, Code = code is null ? "start-failed" : "failed" });
        if (!session.Start(ExportWorker.Argument, System.Text.Json.JsonSerializer.Serialize(request, CleanExportJson.Default.CleanExportRequest)))
        {
            return new CleanExportMessage { Type = CleanExportMessage.ErrorType, Code = "start-failed" };
        }

        using (cancellation.Register(() => _dispatcher.TryEnqueue(session.Cancel)))
        {
            CleanExportMessage result = await answer.Task;
            cancellation.ThrowIfCancellationRequested();
            return result;
        }
    }

    /// <summary>Copies the original's Zone.Identifier stream (Mark-of-the-Web) to the copy. False when it had none.</summary>
    private static bool CopyMarkOfTheWeb(string original, string copy)
    {
        try
        {
            string zone = File.ReadAllText(original + ":Zone.Identifier");
            File.WriteAllText(copy + ":Zone.Identifier", zone);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false; // none (or a file system without alternate streams)
        }
    }
}
