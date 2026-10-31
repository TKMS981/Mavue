using System.Runtime.InteropServices;

namespace Mavue.Viewer.Playback;

/// <summary>A waveform being computed: peak per time bucket (0–1) and how much of it is exact.</summary>
/// <param name="Peaks">Peak absolute sample value per bucket (all channels).</param>
/// <param name="ExactFraction">Buckets before this fraction come from every sample; later ones from the coarse pass (or 0).</param>
public sealed record AudioWaveform(float[] Peaks, double ExactFraction);

/// <summary>
/// Computes the waveform of an audio file with the Media Foundation Source Reader (the same decoders the player uses),
/// decoding faster than real time on the calling thread (docs/ARCHITECTURE.md §4.7.11). Memory is proportional to
/// the number of buckets, never to the file. First a coarse pass reads a short stretch at evenly spaced positions
/// (an overview within a fraction of a second, also for hours of audio), then a full pass refines it from the start;
/// progress is reported a few times per second. Very long files keep the coarse overview. Cancellable at every sample.
/// The file is only read (Media Foundation opens it for shared reading); nothing is written.
/// </summary>
public static unsafe partial class AudioWaveformReader
{
    /// <summary>Buckets of the waveform (fixed: memory does not grow with the file).</summary>
    public const int Buckets = 1024;

    /// <summary>Positions read by the coarse pass.</summary>
    public const int CoarsePositions = 128;

    /// <summary>Files longer than this keep the coarse overview (a full decode would take too long on a slow machine).</summary>
    public static readonly TimeSpan MaxRefinedDuration = TimeSpan.FromHours(2);

    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint AllStreams = 0xFFFFFFFE;
    private const uint EndOfStream = 0x2;
    private const ushort VtI8 = 20;

    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioMajorType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00AA00389B71");

    /// <summary>
    /// Computes the waveform of <paramref name="path"/>, reporting snapshots to <paramref name="progress"/> (on this thread).
    /// </summary>
    /// <param name="path">Audio file.</param>
    /// <param name="duration">Duration known from the player (the buckets span it).</param>
    /// <param name="progress">Receives a fresh snapshot after the coarse pass and a few times per second during refinement.</param>
    /// <param name="cancellation">Stops at the next sample.</param>
    /// <returns>The final waveform, or null when the file cannot be decoded.</returns>
    public static AudioWaveform? Read(string path, TimeSpan duration, Action<AudioWaveform> progress, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(progress);
        if (duration <= TimeSpan.Zero || MFStartup(MfVersion, MfStartupLite) < 0)
        {
            return null;
        }

        try
        {
            var peaks = new float[Buckets];
            nint reader = OpenReader(path);
            if (reader == 0)
            {
                return null;
            }

            try
            {
                long total = duration.Ticks; // 100 ns units, as Media Foundation
                if (duration > TimeSpan.FromSeconds(20))
                {
                    Coarse(reader, total, peaks, cancellation);
                    progress(new AudioWaveform((float[])peaks.Clone(), 0));
                    if (duration > MaxRefinedDuration)
                    {
                        return new AudioWaveform(peaks, 0);
                    }

                    Seek(reader, 0);
                }

                return Refine(reader, total, peaks, progress, cancellation);
            }
            finally
            {
                Release(reader);
            }
        }
        finally
        {
            _ = MFShutdown();
        }
    }

    /// <summary>A short stretch at each of <see cref="CoarsePositions"/> positions fills its share of the buckets.</summary>
    private static void Coarse(nint reader, long total, float[] peaks, CancellationToken cancellation)
    {
        int perPosition = Buckets / CoarsePositions;
        for (int k = 0; k < CoarsePositions; k++)
        {
            cancellation.ThrowIfCancellationRequested();
            long position = total * k / CoarsePositions;
            Seek(reader, position);
            float peak = 0;
            if (ReadOne(reader, out bool end, (s, _) => peak = Math.Max(peak, PeakOf(s))) && !end)
            {
                for (int b = k * perPosition; b < (k + 1) * perPosition; b++)
                {
                    peaks[b] = peak;
                }
            }
        }
    }

    private static AudioWaveform Refine(nint reader, long total, float[] coarse, Action<AudioWaveform> progress, CancellationToken cancellation)
    {
        var exact = new float[Buckets];
        int lastBucket = -1;
        long lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!ReadOne(reader, out bool end, (samples, timestamp) =>
                {
                    // One buffer is short (tens of milliseconds): it belongs to the bucket of its timestamp.
                    int bucket = (int)Math.Clamp(timestamp * Buckets / Math.Max(1, total), 0, Buckets - 1);
                    exact[bucket] = Math.Max(exact[bucket], PeakOf(samples));
                    lastBucket = Math.Max(lastBucket, bucket);
                }) || end)
            {
                break;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastReport) > TimeSpan.FromMilliseconds(250))
            {
                lastReport = System.Diagnostics.Stopwatch.GetTimestamp();
                progress(Merge(exact, coarse, lastBucket));
            }
        }

        AudioWaveform done = new(exact, 1);
        progress(done);
        return done;
    }

    /// <summary>Exact buckets up to <paramref name="lastBucket"/> (the one being filled is not complete), the overview after.</summary>
    private static AudioWaveform Merge(float[] exact, float[] coarse, int lastBucket)
    {
        var merged = (float[])coarse.Clone();
        Array.Copy(exact, merged, Math.Max(0, lastBucket));
        return new AudioWaveform(merged, Math.Max(0, lastBucket) / (double)Buckets);
    }

    private static float PeakOf(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float s in samples)
        {
            float a = Math.Abs(s);
            if (a > peak && float.IsFinite(a))
            {
                peak = a;
            }
        }

        return Math.Min(peak, 1);
    }

    /// <summary>A Source Reader for the first audio stream only, delivering 32-bit float samples.</summary>
    private static nint OpenReader(string path)
    {
        if (MFCreateSourceReaderFromURL(path, 0, out nint reader) < 0 || reader == 0)
        {
            return 0;
        }

        nint type = 0;
        try
        {
            if (Call(reader, 4, AllStreams, 0) < 0 || Call(reader, 4, FirstAudioStream, 1) < 0 || MFCreateMediaType(out type) < 0)
            {
                Release(reader);
                return 0;
            }

            Guid major = MajorType, audio = AudioMajorType, subtype = Subtype, pcm = FloatSubtype;
            var setGuid = (delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 24); // IMFAttributes::SetGUID
            if (setGuid(type, &major, &audio) < 0 || setGuid(type, &subtype, &pcm) < 0 ||
                ((delegate* unmanaged[Stdcall]<nint, uint, uint*, nint, int>)Slot(reader, 7))(reader, FirstAudioStream, null, type) < 0) // SetCurrentMediaType
            {
                Release(reader);
                return 0;
            }

            return reader;
        }
        finally
        {
            Release(type);
        }
    }

    private static void Seek(nint reader, long position)
    {
        PropVariant value = default;
        value.Type = VtI8;
        value.Int64 = position;
        Guid format = Guid.Empty;
        _ = ((delegate* unmanaged[Stdcall]<nint, Guid*, PropVariant*, int>)Slot(reader, 8))(reader, &format, &value); // SetCurrentPosition
    }

    private delegate void SampleHandler(ReadOnlySpan<float> samples, long timestamp);

    /// <summary>Reads one sample (buffer) of the audio stream; false on a decoding error.</summary>
    private static bool ReadOne(nint reader, out bool end, SampleHandler handle)
    {
        uint actual, flags;
        long time;
        nint sample;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Slot(reader, 9))(reader, FirstAudioStream, 0, &actual, &flags, &time, &sample); // ReadSample
        end = (flags & EndOfStream) != 0;
        if (hr < 0)
        {
            return false;
        }

        if (sample == 0)
        {
            return true; // a gap or a format change: nothing to measure
        }

        nint buffer = 0;
        try
        {
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer) < 0) // IMFSample::ConvertToContiguousBuffer
            {
                return false;
            }

            byte* data;
            uint max, length;
            if (((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length) < 0) // IMFMediaBuffer::Lock
            {
                return false;
            }

            try
            {
                handle(new ReadOnlySpan<float>(data, (int)(length / sizeof(float))), time);
            }
            finally
            {
                _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer); // Unlock
            }

            return true;
        }
        finally
        {
            Release(buffer);
            Release(sample);
        }
    }

    /// <summary>IMFSourceReader::SetStreamSelection (slot 4).</summary>
    private static int Call(nint reader, int slot, uint stream, int selected) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Slot(reader, slot))(reader, stream, selected);

    private static nint Slot(nint comObject, int index) => (*(nint**)comObject)[index];

    private static void Release(nint comObject)
    {
        if (comObject != 0)
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comObject, 2))(comObject);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public long Int64;
    }

    [LibraryImport("mfplat.dll")]
    private static partial int MFStartup(uint version, uint flags);

    [LibraryImport("mfplat.dll")]
    private static partial int MFShutdown();

    [LibraryImport("mfplat.dll")]
    private static partial int MFCreateMediaType(out nint mediaType);

    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
}
