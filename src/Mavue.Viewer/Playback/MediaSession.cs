using Mavue.Core.Viewing;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Mavue.Viewer.Playback;

/// <summary>Keyboard media commands: play/pause, seek ±10 s, volume ±10 %.</summary>
public enum MediaCommand
{
    TogglePlay,
    SeekBackward,
    SeekForward,
    VolumeUp,
    VolumeDown,
}

/// <summary>
/// One video or audio file playing through Windows' media pipeline (MediaPlayer + Media Foundation). The file is
/// handed over as a <see cref="StorageFile"/> and streamed by the player; the viewer never reads it into memory.
/// Events are raised on the UI thread and never after <see cref="Dispose"/>, which stops playback and releases the
/// player and the file. A viewer keeps at most one session.
/// </summary>
public sealed class MediaSession : IDisposable
{
    private static int s_nextId;

    private readonly DispatcherQueue _dispatcher;
    private readonly MediaSource _source;
    private bool _disposed;

    private MediaSession(MediaSource source, bool audioOnly, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _source = source;
        AudioOnly = audioOnly;
        Id = Interlocked.Increment(ref s_nextId);
        Player = new MediaPlayer
        {
            AutoPlay = true,
            AudioCategory = MediaPlayerAudioCategory.Media,
        };
        Player.MediaOpened += (_, _) => OnUiThread(OnOpened);
        Player.MediaFailed += (_, args) =>
        {
            string error = args.Error.ToString();
            int hresult = args.ExtendedErrorCode?.HResult ?? 0;
            OnUiThread(() => Failed?.Invoke(this, error, hresult));
        };
        Player.MediaEnded += (_, _) => OnUiThread(() => StateChanged?.Invoke(this, MediaStates.Ended));
        Player.PlaybackSession.PlaybackStateChanged += (session, _) =>
        {
            string state = session.PlaybackState.ToString();
            OnUiThread(() => StateChanged?.Invoke(this, state));
        };
    }

    /// <summary>Distinguishes sessions in diagnostics (one must stop before the next starts).</summary>
    public int Id { get; }

    /// <summary>The player to attach to a MediaPlayerElement.</summary>
    public MediaPlayer Player { get; }

    /// <summary>The file is an audio format (no picture is expected).</summary>
    public bool AudioOnly { get; }

    /// <summary>Picture size once opened; 0 × 0 for audio.</summary>
    public (uint Width, uint Height) NaturalSize { get; private set; }

    /// <summary>True once opened when the file has a picture.</summary>
    public bool HasVideo => !AudioOnly && NaturalSize.Width > 0 && NaturalSize.Height > 0;

    public TimeSpan Duration { get; private set; }

    public bool IsPlaying => !_disposed && Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    /// <summary>The file was opened and playback starts (UI thread).</summary>
    public event Action<MediaSession>? Opened;

    /// <summary>The file cannot be played: damaged, or no decoder for the format (UI thread). Arguments: error, HRESULT.</summary>
    public event Action<MediaSession, string, int>? Failed;

    /// <summary>Playback state (UI thread): a <see cref="MediaPlaybackState"/> name or <see cref="MediaStates.Ended"/>.</summary>
    public event Action<MediaSession, string>? StateChanged;

    /// <summary>Prepares a session for <paramref name="path"/>; playback begins with <see cref="Start"/>.</summary>
    public static async Task<MediaSession> OpenAsync(string path, bool audioOnly, DispatcherQueue dispatcher, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellation);
        cancellation.ThrowIfCancellationRequested();
        return new MediaSession(MediaSource.CreateFromStorageFile(file), audioOnly, dispatcher);
    }

    /// <summary>Starts opening the file; it plays as soon as it is open (call after the player is attached).</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Player.Source = _source;
    }

    /// <summary>Runs a keyboard command and returns the resulting position and volume.</summary>
    public (TimeSpan Position, double Volume) Execute(MediaCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MediaPlaybackSession session = Player.PlaybackSession;
        switch (command)
        {
            case MediaCommand.TogglePlay:
                if (session.PlaybackState == MediaPlaybackState.Playing)
                {
                    Player.Pause();
                }
                else
                {
                    Player.Play();
                }

                break;
            case MediaCommand.SeekBackward or MediaCommand.SeekForward:
                TimeSpan step = command == MediaCommand.SeekForward ? MediaControlMath.SeekStep : -MediaControlMath.SeekStep;
                session.Position = MediaControlMath.Seek(session.Position, step, session.NaturalDuration);
                break;
            case MediaCommand.VolumeUp or MediaCommand.VolumeDown:
                Player.Volume = MediaControlMath.Volume(Player.Volume, command == MediaCommand.VolumeUp ? MediaControlMath.VolumeStep : -MediaControlMath.VolumeStep);
                break;
        }

        return (session.Position, Player.Volume);
    }

    /// <summary>Stops playback and releases the player and the file. Later events are dropped.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Player.Pause();
        Player.Source = null;
        Player.Dispose();
        _source.Dispose();
    }

    private void OnOpened()
    {
        MediaPlaybackSession session = Player.PlaybackSession;
        Duration = session.NaturalDuration;
        NaturalSize = (session.NaturalVideoWidth, session.NaturalVideoHeight);
        Opened?.Invoke(this);
    }

    /// <summary>Media events arrive on other threads; act on the UI thread, and only while this session is alive.</summary>
    private void OnUiThread(Action action) => _dispatcher.TryEnqueue(() =>
    {
        if (!_disposed)
        {
            action();
        }
    });
}

/// <summary>State names used by <see cref="MediaSession.StateChanged"/> besides <see cref="MediaPlaybackState"/>.</summary>
public static class MediaStates
{
    public const string Ended = "ended";
}
