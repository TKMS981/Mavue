using Mavue.Core.Formats;
using Mavue.Core.Viewing;
using Mavue.QuickView.Diagnostics;
using Mavue.QuickView.Host.Presentation;
using Mavue.Viewer.Playback;
using Microsoft.UI.Dispatching;

namespace Mavue.QuickView.Host.Content;

/// <summary>
/// Video and audio with Windows' media pipeline (MediaPlayerElement + Media Foundation, hardware decoding where the
/// system provides it). One media session at a time, streaming the file (the player reads what it plays; Quick View
/// never loads it). Every content change (another file, a page, closing) stops and disposes it; events of a replaced
/// session are ignored. Media keys (Enter, Ctrl+arrows) apply while the Quick View window has the keyboard.
/// Quick View draws its own light controls (<see cref="MediaOverlay"/>); the mouse wheel seeks (±5 s, Shift ±30 s,
/// one seek at a time); audio shows its waveform, computed on a low-priority thread only while the file is shown
/// (docs/ARCHITECTURE.md §4.7.11).
/// </summary>
internal sealed class MediaContent : IPreviewContent, IPlaybackContent
{
    private readonly PreviewServices _s;
    private readonly IPreviewHost _host;
    private MediaSession? _media;
    private string? _path;
    private MediaOverlay? _overlay;
    private DispatcherQueueTimer? _positionTimer;
    private CancellationTokenSource? _waveformWork;
    private int _wheelAccumulated;

    public MediaContent(PreviewServices services, IPreviewHost host)
    {
        _s = services;
        _host = host;
        services.Window.MediaCommandRequested += OnMediaCommand;
        services.Window.MediaWheelHandler = OnWheel;
        services.Window.Surface.UseOwnMediaControls = true;
    }

    public PreviewContentKind Kind => PreviewContentKind.Media;

    public async Task ShowAsync(PreviewItem item, CancellationToken cancellation)
    {
        long requestId = item.RequestId;
        FileFacts facts = item.Facts;
        Stop("replace");
        bool audio = FileFormatKinds.IsAudio(facts.Format);
        MediaSession session = await MediaSession.OpenAsync(item.Path, audio, _s.Dispatcher, cancellation);
        if (cancellation.IsCancellationRequested)
        {
            session.Dispose();
            cancellation.ThrowIfCancellationRequested();
        }

        _media = session;
        _path = item.Path;
        _wheelAccumulated = 0;
        session.Opened += s => OnOpened(s, requestId, facts);
        session.Failed += (s, error, hresult) => OnFailed(s, requestId, error, hresult);
        session.StateChanged += (s, state) => OnState(s, requestId, state);
        _s.Timeline.Mark(requestId, "media-open", QuickViewTimeline.Now, new Dictionary<string, object?> { ["kind"] = audio ? "audio" : "video", ["player"] = session.Id });
        _s.Window.Surface.ShowMedia(session.Player);
        if (_overlay is null)
        {
            _overlay = new MediaOverlay(_s.Window, _s.Strings, _s.Dispatcher);
            _overlay.PlayPauseClicked += () => RunCommand(MediaCommand.TogglePlay, "button");
            _overlay.MuteClicked += ToggleMute;
            _overlay.SeekRequested += SeekToFraction;
        }

        _overlay.Attach(video: !audio);
        _s.Window.ShowCard(_overlay.Root);
        _s.ImageView.ReleaseDisplayedBitmap(); // the previous item's picture is gone from the surface
        _s.ImageView.RecordShown(SurfaceImageKind.None, default, 0, _host.DisplayScale());
        _host.Display.MarkContent(requestId); // no "stale image" clearing needed
        if (audio)
        {
            _s.Window.Surface.SetAudioLayout();
        }

        _s.Window.Surface.SetStatus(_s.Strings.GetString("QuickView_Loading"));
        session.Start();
    }

    public void Reset(ContentReset scope, string reason) => Stop(reason);

    /// <summary>The player scales by itself; only the no-enlarge limit changes.</summary>
    public AreaResponse OnAreaChanged()
    {
        if (_media is null)
        {
            return AreaResponse.ReloadIfChanged;
        }

        UpdateVideoLayout();
        return AreaResponse.Handled;
    }

    public void PauseIfPlaying()
    {
        if (_media is { IsPlaying: true } playing)
        {
            playing.Execute(MediaCommand.TogglePlay);
        }
    }

    public void Dispose() => Stop("exit");

    private async void OnOpened(MediaSession session, long requestId, FileFacts facts)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        TimeSpan duration = session.Duration;
        _s.Window.Surface.SetStatus(string.Empty);
        if (session.HasVideo)
        {
            UpdateVideoLayout();
            _host.SetInfo(PreviewInfoText.Video(_s.Strings, facts, session.NaturalSize.Width, session.NaturalSize.Height, duration));
        }
        else
        {
            _s.Window.Surface.SetAudioLayout();
            _host.SetInfo(PreviewInfoText.Audio(_s.Strings, facts, duration));
        }

        _host.Display.MarkContent(requestId);
        UpdateOverlay();
        _positionTimer ??= CreatePositionTimer();
        _positionTimer.Start();
        if (!session.HasVideo && _path is { } path)
        {
            StartWaveform(session, path, duration, requestId);
        }

        _s.Timeline.Mark(requestId, "media-opened", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["kind"] = session.HasVideo ? "video" : "audio",
            ["player"] = session.Id,
            ["width"] = session.NaturalSize.Width,
            ["height"] = session.NaturalSize.Height,
            ["durationMs"] = Math.Round(duration.TotalMilliseconds),
        });
        await QuickViewPresence.NextFrameAsync();
        if (ReferenceEquals(session, _media))
        {
            _s.Timeline.Mark(requestId, "full-visible", QuickViewTimeline.Now);
        }
    }

    private void OnFailed(MediaSession session, long requestId, string error, int hresult)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        // Missing codec, unsupported container or a damaged file: say so; the resident process carries on.
        _s.Timeline.Mark(requestId, "media-failed", QuickViewTimeline.Now, new Dictionary<string, object?> { ["error"] = error, ["hresult"] = hresult, ["player"] = session.Id });
        Stop("failed");
        _s.Window.Surface.ShowStatusOnly(_s.Strings.GetString("QuickView_Error_Media"));
        _host.Display.MarkMessage(requestId);
        _s.Timeline.Mark(requestId, "full-skipped", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = "media-failed" });
    }

    private void OnState(MediaSession session, long requestId, string state)
    {
        if (!ReferenceEquals(session, _media))
        {
            return;
        }

        string? text = state switch
        {
            "Playing" => _s.Strings.GetString("QuickView_MediaPlaying"),
            "Paused" => _s.Strings.GetString("QuickView_MediaPaused"),
            MediaStates.Ended => _s.Strings.GetString("QuickView_MediaEnded"),
            _ => null,
        };
        if (text is not null)
        {
            _s.Window.Surface.SetAudioState(text);
        }

        UpdateOverlay();

        _s.Timeline.Mark(requestId, "media-state", QuickViewTimeline.Now, new Dictionary<string, object?> { ["state"] = state, ["player"] = session.Id });
    }

    /// <summary>Enter, Ctrl+←/→ and Ctrl+↑/↓ while the Quick View window has the keyboard.</summary>
    private void OnMediaCommand(MediaCommand command)
    {
        if (_media is not { } session)
        {
            return;
        }

        RunCommand(command, "key");
    }

    private void RunCommand(MediaCommand command, string source)
    {
        if (_media is not { } session)
        {
            return;
        }

        (TimeSpan position, double volume) = session.Execute(command);
        _s.Timeline.Mark(_host.CurrentRequest, "media-command", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["command"] = command.ToString(),
            ["positionMs"] = Math.Round(position.TotalMilliseconds),
            ["volume"] = Math.Round(volume, 2),
            ["player"] = session.Id,
            ["source"] = source,
        });
        _overlay?.Poke();
        UpdateOverlay();
    }

    /// <summary>The wheel over video or audio: ±5 s per notch (up = forward), ±30 s with Shift; one seek at a time.</summary>
    private bool OnWheel(int wheelDelta, bool large)
    {
        if (_media is not { } session)
        {
            return false;
        }

        TimeSpan step = MediaControlMath.WheelSeek(wheelDelta, large, ref _wheelAccumulated);
        if (step == TimeSpan.Zero || session.Duration <= TimeSpan.Zero)
        {
            return true; // part of a notch, or not seekable: the wheel still means nothing else here
        }

        TimeSpan target = session.RequestSeek(session.Position + step);
        _s.Timeline.Mark(_host.CurrentRequest, "media-wheel-seek", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["stepMs"] = Math.Round(step.TotalMilliseconds),
            ["targetMs"] = Math.Round(target.TotalMilliseconds),
            ["player"] = session.Id,
        });
        _overlay?.Poke();
        UpdateOverlay();
        return true;
    }

    private void SeekToFraction(double fraction, string source)
    {
        if (_media is not { } session || session.Duration <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan target = session.RequestSeek(session.Duration * fraction);
        _s.Timeline.Mark(_host.CurrentRequest, "media-seek", QuickViewTimeline.Now, new Dictionary<string, object?>
        {
            ["source"] = source,
            ["targetMs"] = Math.Round(target.TotalMilliseconds),
            ["player"] = session.Id,
        });
        UpdateOverlay();
    }

    private void ToggleMute()
    {
        if (_media is not { } session)
        {
            return;
        }

        session.IsMuted = !session.IsMuted;
        _s.Timeline.Mark(_host.CurrentRequest, "media-mute", QuickViewTimeline.Now, new Dictionary<string, object?> { ["muted"] = session.IsMuted, ["player"] = session.Id });
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (_media is { } session && _overlay is { } overlay)
        {
            overlay.Update(session.Position, session.Duration, session.IsPlaying, session.IsMuted);
        }
    }

    /// <summary>The position shown in the controls, only while a file is open (stopped with it).</summary>
    private DispatcherQueueTimer CreatePositionTimer()
    {
        DispatcherQueueTimer timer = _s.Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (_media is null || !_host.IsVisible)
            {
                timer.Stop();
                return;
            }

            UpdateOverlay();
        };
        return timer;
    }

    /// <summary>The waveform of the shown audio file, on a low-priority thread; cancelled with the file.</summary>
    private void StartWaveform(MediaSession session, string path, TimeSpan duration, long requestId)
    {
        CancelWaveform();
        var work = new CancellationTokenSource();
        _waveformWork = work;
        CancellationToken token = work.Token;
        var thread = new Thread(() =>
        {
            long start = QuickViewTimeline.Now;
            string outcome;
            try
            {
                AudioWaveform? result = AudioWaveformReader.Read(path, duration, snapshot => _s.Dispatcher.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested && ReferenceEquals(session, _media))
                    {
                        _overlay?.SetWaveform(snapshot);
                    }
                }), token);
                outcome = result is null ? "unavailable" : result.ExactFraction >= 1 ? "exact" : "coarse";
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                outcome = "error-" + ex.GetType().Name;
            }

            double ms = (QuickViewTimeline.Now - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _s.Timeline.Mark(requestId, "audio-waveform", QuickViewTimeline.Now, new Dictionary<string, object?> { ["outcome"] = outcome, ["ms"] = Math.Round(ms), ["player"] = session.Id });
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "Mavue waveform",
        };
        thread.Start();
    }

    private void CancelWaveform()
    {
        _waveformWork?.Cancel();
        _waveformWork?.Dispose();
        _waveformWork = null;
    }


    /// <summary>Video fits the area and is never enlarged (same rule as images).</summary>
    private void UpdateVideoLayout()
    {
        if (_media is not { HasVideo: true } session)
        {
            return;
        }

        (uint areaWidth, uint areaHeight) = _host.ImageArea();
        (uint width, uint height) = DisplaySizing.ExpectedDisplayPixels(ImageScaleMode.FitNoUpscale, areaWidth, areaHeight, session.NaturalSize.Width, session.NaturalSize.Height);
        (double w, double h) = DisplaySizing.ElementSize(width, height, _host.DisplayScale());
        _s.Window.Surface.SetVideoLayout(w, h);
    }

    private void Stop(string reason)
    {
        if (_media is not { } session)
        {
            return;
        }

        _media = null;
        CancelWaveform();
        _positionTimer?.Stop();
        if (_overlay is { } overlay)
        {
            overlay.Detach();
            if (ReferenceEquals(_s.Window.CardContent, overlay.Root))
            {
                _s.Window.ShowCard(null);
            }
        }

        _s.Window.Surface.ClearMedia();
        session.Dispose();
        _s.Timeline.Mark(_host.CurrentRequest, "media-stop", QuickViewTimeline.Now, new Dictionary<string, object?> { ["reason"] = reason, ["player"] = session.Id });
    }
}
