using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Mavue.Core.Viewing;
using Mavue.Image;
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
/// <para>
/// Playback can be paused and stepped frame by frame (a step back composes the animation again from the first
/// frame, as GIF frames build on each other). <see cref="Stop"/> cancels decoding and releases the file; a
/// stopped player never touches the window again.
/// </para>
/// </summary>
public sealed class GifPlayer : IDisposable
{
    private static int s_nextId;

    private readonly GifAnimationReader _reader;
    private readonly GifComposer _composer;
    private readonly WriteableBitmap _bitmap;
    private readonly ViewOrientation _orientation;
    private readonly byte[]? _oriented;
    private readonly PlaybackTrace? _trace;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _frameGate = new(1, 1);

    // Index of the frame composed last (on the canvas), -1 before the first one.
    private int _composed = -1;
    private TaskCompletionSource? _resume;

    /// <param name="reader">Opened animation; the player owns it from now on and closes it when playback ends.</param>
    /// <param name="trace">Optional diagnostics callback (called on the UI thread).</param>
    /// <param name="orientation">How the frames are turned on screen.</param>
    public GifPlayer(GifAnimationReader reader, PlaybackTrace? trace = null, ViewOrientation orientation = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _trace = trace;
        _orientation = orientation;
        _composer = new GifComposer(reader.Width, reader.Height);
        (int width, int height) = orientation.Oriented(reader.Width, reader.Height);
        _bitmap = new WriteableBitmap(width, height);
        _oriented = orientation.IsIdentity ? null : new byte[width * height * 4];
        Id = Interlocked.Increment(ref s_nextId);
    }

    /// <summary>Distinguishes players in the timing log (a switch must never leave two running).</summary>
    public int Id { get; }

    /// <summary>The image source the window shows; updated in place for every frame.</summary>
    public WriteableBitmap Source => _bitmap;

    public bool IsStopped => _stop.IsCancellationRequested;

    /// <summary>True while playback is paused (by <see cref="Pause"/> or a frame step).</summary>
    public bool IsPaused => _resume is not null;

    public int FrameCount => _reader.FrameCount;

    /// <summary>Zero-based frame on screen (-1 before the first frame).</summary>
    public int CurrentFrame => _composed;

    /// <summary>Raised on the UI thread when playback pauses, resumes or steps to another frame.</summary>
    public event Action<GifPlayer>? PlaybackChanged;

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

    public void Pause()
    {
        if (_resume is not null || IsStopped)
        {
            return;
        }

        _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _trace?.Invoke("gif-pause", new Dictionary<string, object?> { ["player"] = Id, ["frame"] = _composed });
        PlaybackChanged?.Invoke(this);
    }

    public void Resume()
    {
        if (_resume is not { } resume)
        {
            return;
        }

        _resume = null;
        resume.TrySetResult();
        _trace?.Invoke("gif-resume", new Dictionary<string, object?> { ["player"] = Id, ["frame"] = _composed });
        PlaybackChanged?.Invoke(this);
    }

    public void TogglePause()
    {
        if (IsPaused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    /// <summary>Pauses and shows the next (+1) or previous (-1) frame, wrapping around at the ends.</summary>
    public async Task StepAsync(int delta)
    {
        if (IsStopped || delta == 0)
        {
            return;
        }

        Pause();
        CancellationToken cancellation = _stop.Token;
        try
        {
            await _frameGate.WaitAsync(cancellation);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            int count = _reader.FrameCount;
            int target = (((_composed + delta) % count) + count) % count;
            int from = target > _composed ? _composed + 1 : 0; // going back: compose again from the first frame
            await Task.Run(
                async () =>
                {
                    for (int index = from; index <= target; index++)
                    {
                        await ComposeAsync(index, cancellation).ConfigureAwait(false);
                    }
                },
                cancellation);
            cancellation.ThrowIfCancellationRequested();
            Present();
            _trace?.Invoke("gif-step", new Dictionary<string, object?> { ["player"] = Id, ["frame"] = target });
            PlaybackChanged?.Invoke(this);
        }
        catch (OperationCanceledException)
        {
            // Stopped while stepping.
        }
        finally
        {
            ReleaseGate();
        }
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

        _resume?.TrySetCanceled();
    }

    /// <summary>Same as <see cref="Stop"/>; the token source itself is released when playback ends.</summary>
    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken cancellation)
    {
        int plays = 0;
        int shown = 0;
        long due = Stopwatch.GetTimestamp();
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (_resume is { } paused)
                {
                    await paused.Task.WaitAsync(cancellation);
                    due = Stopwatch.GetTimestamp();
                }

                await _frameGate.WaitAsync(cancellation);
                GifFrame frame;
                int frameIndex;
                try
                {
                    if (_resume is not null)
                    {
                        continue; // paused while waiting for the gate (a step owns the canvas now)
                    }

                    // Decode + compose the next frame off the UI thread (during the current frame's delay).
                    frameIndex = (_composed + 1) % _reader.FrameCount;
                    if (frameIndex == 0 && _composed >= 0)
                    {
                        plays++;
                        if (_reader.TotalPlays is { } total && plays >= total)
                        {
                            break; // finite loop count: stay on the last frame
                        }
                    }

                    frame = await Task.Run(() => ComposeAsync(frameIndex, cancellation), cancellation);
                    TimeSpan wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), due); // due - now
                    if (wait > TimeSpan.Zero)
                    {
                        await Task.Delay(wait, cancellation);
                    }

                    cancellation.ThrowIfCancellationRequested();
                    Present();
                }
                finally
                {
                    ReleaseGate();
                }

                shown++;
                _trace?.Invoke("gif-frame", new Dictionary<string, object?>
                {
                    ["player"] = Id,
                    ["index"] = frameIndex,
                    ["play"] = plays,
                });
                due = Stopwatch.GetTimestamp() + (long)(frame.Delay.TotalSeconds * Stopwatch.Frequency);
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
            // Only here, so a frame read in flight never sees a closed stream. A step in progress finishes first.
            if (!cancellation.IsCancellationRequested)
            {
                _stop.Cancel();
            }

            await _frameGate.WaitAsync(CancellationToken.None);
            _reader.Dispose();
            _stop.Dispose();
            _trace?.Invoke("gif-stop", new Dictionary<string, object?>
            {
                ["player"] = Id,
                ["shown"] = shown,
                ["plays"] = plays,
            });
        }
    }

    /// <summary>Thread pool: decodes frame <paramref name="index"/> and draws it on the canvas.</summary>
    private async Task<GifFrame> ComposeAsync(int index, CancellationToken cancellation)
    {
        GifFrame decoded = await _reader.ReadFrameAsync(index, cancellation).ConfigureAwait(false);
        if (index == 0)
        {
            _composer.Reset();
        }

        _composer.Apply(decoded);
        _composed = index;
        return decoded;
    }

    private void ReleaseGate()
    {
        try
        {
            _frameGate.Release();
        }
        catch (ObjectDisposedException)
        {
            // Never disposed; kept for safety.
        }
    }

    /// <summary>UI thread: copies the composed canvas (turned if needed) into the on-screen bitmap.</summary>
    private void Present()
    {
        byte[] pixels = _composer.Canvas;
        if (_oriented is { } turned)
        {
            (int width, _) = _orientation.Oriented(_composer.Width, _composer.Height);
            PixelOrientation.Apply(pixels, _composer.Width, _composer.Height, _composer.Width * 4, turned, width * 4, _orientation);
            pixels = turned;
        }

        using (Stream stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        _bitmap.Invalidate();
    }
}
