using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Mavue.Core.Formats;
using Mavue.Core.Similarity;
using Mavue.Core.Viewing;
using Mavue.Image.Wic;

namespace Mavue.Image.Similarity;

/// <summary>
/// The similar-image search (ARCHITECTURE §4.7.14), run in the resource-limited worker process. Candidates are
/// narrowed in stages so the expensive work is done for few files:
/// <list type="number">
/// <item>Listing: image files in the folder (types Windows can decode), skipping cloud-only files (reading would
/// download them), very large files and anything past <see cref="SimilarSearchRequest.MaxFiles"/>.</item>
/// <item>Coarse: a 32-pixel decode per file (cached by path, size and time) → difference/average hash, mean color,
/// aspect. Files far from the target stop here.</item>
/// <item>Fine: a 128-pixel decode of the candidates (cached) → perceptual hash and color layout → similarity.</item>
/// <item>Thumbnails of the best results only.</item>
/// </list>
/// Decoding runs on a few dedicated threads (COM initialized, no thread pool), checks cancellation between files, and
/// never holds more than one small decode per thread. Signatures are saved to the bounded cache at the end.
/// </summary>
public static partial class SimilarImageSearch
{
    private const uint CoarseSize = 32;
    private const uint FineSize = 128;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const long MaxSourcePixels = 150_000_000;

    /// <summary>Runs a search; <paramref name="emit"/> receives progress, results, then "done" (or an error).</summary>
    public static void Run(SimilarSearchRequest request, Action<SimilarSearchMessage> emit, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(emit);
        cancellation.ThrowIfCancellationRequested();
        var clock = Stopwatch.StartNew();
        SignatureCache cache = SignatureCache.Load(request.CachePath);
        int hits = 0;

        // The target first: without its signatures there is nothing to compare with. With rotations, its signatures
        // are made for all 8 orientations from its own pixels (cheap: one image), so turned or mirrored copies match.
        List<Variant>? variants = RunOnComThread(() => TargetVariants(request.Target, request.DetectRotations, cancellation), cancellation);
        if (variants is not { Count: > 0 })
        {
            emit(new SimilarSearchMessage { Type = SimilarSearchMessage.ErrorType, Code = "target-unreadable" });
            return;
        }

        emit(Progress(SimilarSearchStage.Listing, 0, 0, 0));
        (List<FileInfo> files, int skipped, bool truncated) = List(request, cancellation);

        // Stage 1: coarse signatures of every file.
        var candidates = new ConcurrentBag<(FileInfo File, CoarseSignature Coarse)>();
        int done = 0;
        long lastReport = 0;
        ForEach(files, Math.Clamp(request.Parallelism, 1, 4), file =>
        {
            CoarseSignature? coarse = CoarseOf(file, cache, ref hits, cancellation);
            if (coarse is null)
            {
                Interlocked.Increment(ref skipped);
            }
            else if (variants.Exists(v => ImageSignatures.MayBeSimilar(v.Coarse, coarse.Value)))
            {
                candidates.Add((file, coarse.Value));
            }

            int now = Interlocked.Increment(ref done);
            Report(ref lastReport, clock, () => emit(Progress(SimilarSearchStage.Coarse, now, files.Count, candidates.Count)));
        }, cancellation);

        // Stage 2: fine signatures of the candidates only.
        var matches = new ConcurrentBag<(FileInfo File, int Similarity, ViewOrientation Orientation)>();
        List<(FileInfo File, CoarseSignature Coarse)> stage2 = [.. candidates];
        done = 0;
        ForEach(stage2, Math.Clamp(request.Parallelism, 1, 4), candidate =>
        {
            FineSignature? fine = FineOf(candidate.File, candidate.Coarse, cache, ref hits, cancellation);
            if (fine is { } f)
            {
                (int similarity, ViewOrientation orientation) = variants
                    .Select(v => (ImageSignatures.Similarity(v.Coarse, v.Fine, candidate.Coarse, f), v.Orientation))
                    .MaxBy(m => m.Item1);
                if (similarity >= request.Threshold)
                {
                    matches.Add((candidate.File, similarity, orientation));
                }
            }

            int now = Interlocked.Increment(ref done);
            Report(ref lastReport, clock, () => emit(Progress(SimilarSearchStage.Fine, now, stage2.Count, matches.Count)));
        }, cancellation);

        // Results, best first, with thumbnails.
        var best = matches.OrderByDescending(m => m.Similarity).ThenBy(m => m.File.FullName, StringComparer.OrdinalIgnoreCase).Take(Math.Max(1, request.MaxResults)).ToList();
        uint thumb = (uint)Math.Clamp(request.ThumbnailSize, 16, 256);
        int sent = 0;
        foreach ((FileInfo file, int similarity, ViewOrientation orientation) in best)
        {
            cancellation.ThrowIfCancellationRequested();
            DecodedPixels? picture = RunOnComThread(() => Decode(file.FullName, thumb, cancellation), cancellation);
            emit(new SimilarSearchMessage
            {
                Type = SimilarSearchMessage.ResultType,
                Path = file.FullName,
                Similarity = similarity,
                Width = (int?)picture?.SourceWidth,
                Height = (int?)picture?.SourceHeight,
                ThumbnailWidth = (int?)picture?.Width,
                ThumbnailHeight = (int?)picture?.Height,
                Thumbnail = picture is null ? null : Convert.ToBase64String(picture.Pixels),
                Rotation = orientation.IsIdentity ? null : orientation.Degrees,
                Mirrored = orientation.Mirrored ? true : null,
            });
            Report(ref lastReport, clock, () => emit(Progress(SimilarSearchStage.Thumbnails, ++sent, best.Count, matches.Count)));
        }

        if (cache.Dirty)
        {
            cache.Save(request.CachePath);
        }

        emit(new SimilarSearchMessage
        {
            Type = SimilarSearchMessage.DoneType,
            Examined = files.Count,
            Skipped = skipped,
            Matches = matches.Count,
            CacheHits = hits,
            Milliseconds = Math.Round(clock.Elapsed.TotalMilliseconds),
            Truncated = truncated,
        });
    }

    /// <summary>Image files of the folder that may be examined (no file is opened here).</summary>
    public static (List<FileInfo> Files, int Skipped, bool Truncated) List(SimilarSearchRequest request, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        var files = new List<FileInfo>();
        int skipped = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = request.IncludeSubfolders,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            MaxRecursionDepth = 8,
            ReturnSpecialDirectories = false,
        };
        string target = Path.GetFullPath(request.Target);
        foreach (FileInfo file in new DirectoryInfo(request.Folder).EnumerateFiles("*", options))
        {
            cancellation.ThrowIfCancellationRequested();
            FileFormat format = FileFormatDetector.FromExtension(file.Extension);
            if (!FileFormatKinds.IsImage(format) || format == FileFormat.Svg || string.Equals(file.FullName, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PreviewSafetyPolicy.CheckAttributes(file.Attributes) != PreviewAccess.Allowed || file.Length == 0 || file.Length > MaxFileBytes)
            {
                skipped++; // cloud-only (reading would download it), empty or too large
                continue;
            }

            if (files.Count >= request.MaxFiles)
            {
                return (files, skipped, true);
            }

            files.Add(file);
        }

        return (files, skipped, false);
    }

    /// <summary>The target's signatures as it is, and (when asked) turned by 90, 180 and 270 degrees and mirrored.</summary>
    private sealed record Variant(ViewOrientation Orientation, CoarseSignature Coarse, FineSignature Fine);

    private static List<Variant>? TargetVariants(string path, bool rotations, CancellationToken cancellation)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        DecodedPixels? small = Decode(path, CoarseSize, cancellation);
        DecodedPixels? large = Decode(path, FineSize, cancellation);
        if (small is null || large is null)
        {
            return null;
        }

        double aspect = small.SourceHeight == 0 ? 1 : (double)small.SourceWidth / small.SourceHeight;
        var variants = new List<Variant>();
        foreach (ViewOrientation orientation in Orientations(rotations))
        {
            (byte[] coarsePixels, int cw, int ch) = Orient(small, orientation);
            (byte[] finePixels, int fw, int fh) = Orient(large, orientation);
            double turnedAspect = orientation.SwapsAxes ? 1 / aspect : aspect;
            variants.Add(new Variant(orientation, ImageSignatures.Coarse(coarsePixels, cw, ch, turnedAspect), ImageSignatures.Fine(finePixels, fw, fh)));
        }

        return variants;
    }

    /// <summary>Identity first; with rotations, the other 7 of the 8 orientations of a picture.</summary>
    public static IEnumerable<ViewOrientation> Orientations(bool rotations)
    {
        yield return ViewOrientation.Identity;
        if (!rotations)
        {
            yield break;
        }

        for (int turns = 0; turns < 4; turns++)
        {
            foreach (bool mirrored in new[] { false, true })
            {
                if (turns != 0 || mirrored)
                {
                    yield return new ViewOrientation(turns, mirrored);
                }
            }
        }
    }

    private static (byte[] Pixels, int Width, int Height) Orient(DecodedPixels source, ViewOrientation orientation)
    {
        int width = (int)source.Width, height = (int)source.Height;
        if (orientation.IsIdentity)
        {
            return (source.Pixels, width, height);
        }

        (int w, int h) = orientation.Oriented(width, height);
        byte[] output = new byte[w * h * 4];
        PixelOrientation.Apply(source.Pixels, width, height, width * 4, output, w * 4, orientation);
        return (output, w, h);
    }

    private static CoarseSignature? CoarseOf(FileInfo file, SignatureCache cache, ref int hits, CancellationToken cancellation)
    {
        if (cache.TryGet(file.FullName, file.Length, file.LastWriteTimeUtc, out CoarseSignature cached, out _))
        {
            Interlocked.Increment(ref hits);
            return cached;
        }

        DecodedPixels? pixels = Decode(file.FullName, CoarseSize, cancellation);
        if (pixels is null)
        {
            return null;
        }

        var coarse = ImageSignatures.Coarse(pixels.Pixels, (int)pixels.Width, (int)pixels.Height, pixels.SourceHeight == 0 ? 1 : (double)pixels.SourceWidth / pixels.SourceHeight);
        cache.Set(file.FullName, file.Length, file.LastWriteTimeUtc, coarse, null);
        return coarse;
    }

    private static FineSignature? FineOf(FileInfo file, CoarseSignature coarse, SignatureCache cache, ref int hits, CancellationToken cancellation)
    {
        if (cache.TryGet(file.FullName, file.Length, file.LastWriteTimeUtc, out _, out FineSignature? cached) && cached is { } hit)
        {
            Interlocked.Increment(ref hits);
            return hit;
        }

        DecodedPixels? pixels = Decode(file.FullName, FineSize, cancellation);
        if (pixels is null)
        {
            return null;
        }

        FineSignature fine = ImageSignatures.Fine(pixels.Pixels, (int)pixels.Width, (int)pixels.Height);
        cache.Set(file.FullName, file.Length, file.LastWriteTimeUtc, coarse, fine);
        return fine;
    }

    /// <summary>A small decode (EXIF orientation applied, no color management); null when it cannot be decoded.</summary>
    private static DecodedPixels? Decode(string path, uint size, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try
        {
            return WicPreviewDecoder.DecodeForAnalysis(path, size, size, MaxSourcePixels, cancellation);
        }
        catch (Exception ex) when (ex is COMException or ImageTooLargeException or IOException or UnauthorizedAccessException or ArgumentException or InvalidCastException)
        {
            return null; // damaged, unsupported or too large: skipped
        }
    }

    /// <summary>Runs <paramref name="action"/> for each item on <paramref name="threads"/> dedicated COM (MTA) threads.</summary>
    private static void ForEach<T>(IReadOnlyList<T> items, int threads, Action<T> action, CancellationToken cancellation)
    {
        int next = -1;
        Exception? failure = null;
        var workers = new Thread[Math.Min(threads, Math.Max(1, items.Count))];
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = new Thread(() =>
            {
                int hr = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
                try
                {
                    int i;
                    while (!cancellation.IsCancellationRequested && (i = Interlocked.Increment(ref next)) < items.Count)
                    {
                        action(items[i]);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Stopped.
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
                finally
                {
                    if (hr >= 0)
                    {
                        CoUninitialize();
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Mavue similar search",
                Priority = ThreadPriority.BelowNormal,
            };
            workers[w].Start();
        }

        foreach (Thread worker in workers)
        {
            worker.Join();
        }

        cancellation.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfoThrow(failure);
        }
    }

    private static T RunOnComThread<T>(Func<T> work, CancellationToken cancellation)
    {
        T result = default!;
        ForEach([0], 1, _ => result = work(), cancellation); // a cancellation inside is rethrown, not taken for "unreadable"
        return result;
    }

    private static void ExceptionDispatchInfoThrow(Exception ex) => System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

    private static SimilarSearchMessage Progress(SimilarSearchStage stage, int done, int total, int candidates) =>
        new() { Type = SimilarSearchMessage.ProgressType, Stage = stage, Done = done, Total = total, Candidates = candidates };

    /// <summary>At most about four progress messages a second.</summary>
    private static void Report(ref long last, Stopwatch clock, Action send)
    {
        long now = clock.ElapsedMilliseconds;
        long previous = Interlocked.Read(ref last);
        if (now - previous >= 250 && Interlocked.CompareExchange(ref last, now, previous) == previous)
        {
            send();
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
