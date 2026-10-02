using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Image.Gif;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Mavue.Viewer.Playback;

/// <summary>Receives playback events for diagnostics (Quick View writes them to its timing log).</summary>
/// <param name="name">Event name, e.g. "gif-frame".</param>
/// <param name="detail">Event properties.</param>
public delegate void PlaybackTrace(string name, IReadOnlyDictionary<string, object?> detail);

/// <summary>
/// Plays one animated GIF on a viewer surface. Runs on the UI thread, but every frame is decoded and
/// composed on a thread-pool thread while the previous frame is on screen; the UI thread only copies the
/// finished canvas into the bitmap and waits asynchronously, so very short or very long delays never block it.
/// Memory is bounded by the canvas (a few copies of width × height × 4), whatever the number of frames.
/// <see cref="Stop"/> cancels decoding and releases the file; a stopped player never touches the window again.
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private static int s_nextId;

    private readonly GifAnimationReader _reader;
    private readonly GifComposer _composer;
    private readonly WriteableBitmap _bitmap;
    private readonly PlaybackTrace? _trace;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="reader">Opened animation; the player owns it from now on and closes it when playback ends.</param>
    /// <param name="trace">Optional diagnostics callback (called on the UI thread).</param>
    public GifPlayer(GifAnimationReader reader, PlaybackTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _trace = trace;
        _composer = new GifComposer(reader.Width, reader.Height);
        _bitmap = new WriteableBitmap(reader.Width, reader.Height);
        Id = Interlocked.Increment(ref s_nextId);
    }

    /// <summary>Distinguishes players in the timing log (a switch must never leave two running).</summary>
    public int Id { get; }

    /// <summary>The image source the window shows; updated in place for every frame.</summary>
    public WriteableBitmap Source => _bitmap;

    public bool IsStopped => _stop.IsCancellationRequested;

    /// <summary>Starts playing. Call on the UI thread after <see cref="Source"/> was put on screen.</summary>
    public void Start()
    {
        _trace?.Invoke("gif-start", new Dictionary<string, object?>
        {
            ["player"] = Id,
            ["frames"] = _reader.FrameCount,
            ["width"] = _reader.Width,
            ["height"] = _reader.Height,
            ["loopCount"] = _reader.LoopCount,
        });
        _ = RunAsync(_stop.Token);
    }

    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished (finite loop count or error) and cleaned up.
        }
    }

    /// <summary>Same as <see cref="Stop"/>; the token source itself is released when playback ends.</summary>
    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellation)
    {
        int index = 0;
        int plays = 0;
        int shown = 0;
        long due = Stopwatch.GetTimestamp();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                // Decode + compose the next frame off the UI thread (during the current frame's delay).
                int frameIndex = index;
                GifFrame frame = await Task.Run(
                    async () =>
                    {
                        GifFrame decoded = await _reader.ReadFrameAsync(frameIndex, cancellation).ConfigureAwait(false);
                        if (frameIndex == 0)
                        {
                            _composer.Reset();
                        }

                        _composer.Apply(decoded);
                        return decoded;
                    },
                    cancellation);

                TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due); // due - now
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellation);
                }

                cancellation.ThrowIfCancellationRequested();
                Present();
                shown++;
                _trace?.Invoke("gif-frame", new Dictionary<string, object?>
                {
                    ["player"] = Id,
                    ["index"] = frameIndex,
                    ["play"] = plays,
                });
                due = Stopwatch.GetTimestamp() + (long)(frame.Delay.TotalSeconds * Stopwatch.Frequency);

                index++;
                if (index >= _reader.FrameCount)
                {
                    plays++;
                    if (_reader.TotalPlays is { } total && plays >= total)
                    {
                        break; // finite loop count: stay on the last frame
                    }

                    index = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped: another item, another page, or the viewer closed.
        }
        catch (Exception ex)
        {
            // A broken frame ends the animation; the frames already shown stay on screen.
            _trace?.Invoke("gif-error", new Dictionary<string, object?> { ["player"] = Id, ["type"] = ex.GetType().Name });
        }
        finally
        {
            _reader.Dispose(); // only here, so a frame read in flight never sees a closed stream
            _stop.Dispose();
            _trace?.Invoke("gif-stop", new Dictionary<string, object?>
            {
                ["player"] = Id,
                ["shown"] = shown,
                ["plays"] = plays,
            });
        }
    }

    /// <summary>UI thread: copies the composed canvas into the on-screen bitmap.</summary>
    private void Present()
    {
        using (Stream pixels = _bitmap.PixelBuffer.AsStream())
        {
            pixels.Write(_composer.Canvas, 0, _composer.Canvas.Length);
        }

        _bitmap.Invalidate();
    }
}
