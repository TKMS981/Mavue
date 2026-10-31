using System.Globalization;

namespace Mavue.Core.Viewing;

/// <summary>Keyboard media commands of Quick View (seek and volume steps) and how durations are shown.</summary>
public static class MediaControlMath
{
    public static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    public const double VolumeStep = 0.1;

    /// <summary>One wheel notch over a video or audio file in Quick View (up = forward).</summary>
    public static readonly TimeSpan WheelSeekStep = TimeSpan.FromSeconds(5);

    /// <summary>One wheel notch with Shift.</summary>
    public static readonly TimeSpan WheelSeekLargeStep = TimeSpan.FromSeconds(30);

    /// <summary>Wheel delta of one notch (WHEEL_DELTA).</summary>
    public const int WheelNotch = 120;

    /// <summary>
    /// The seek for a wheel event: whole notches of <paramref name="accumulated"/> plus <paramref name="wheelDelta"/>
    /// (fine-grained touchpad deltas add up); the rest stays in <paramref name="accumulated"/>. Up (positive) seeks forward.
    /// </summary>
    public static TimeSpan WheelSeek(int wheelDelta, bool large, ref int accumulated)
    {
        accumulated += wheelDelta;
        int notches = accumulated / WheelNotch;
        accumulated -= notches * WheelNotch;
        return (large ? WheelSeekLargeStep : WheelSeekStep) * notches;
    }

    /// <summary>
    /// Peaks (0–1) for <paramref name="columns"/> columns from <paramref name="buckets"/>: the largest bucket each column
    /// covers (no peak is lost when there are more buckets than columns; buckets repeat when there are fewer).
    /// </summary>
    public static float[] WaveformColumns(ReadOnlySpan<float> buckets, int columns)
    {
        if (columns <= 0 || buckets.IsEmpty)
        {
            return [];
        }

        var result = new float[columns];
        for (int x = 0; x < columns; x++)
        {
            int from = (int)((long)x * buckets.Length / columns);
            int to = Math.Max(from + 1, (int)((long)(x + 1) * buckets.Length / columns));
            float peak = 0;
            for (int i = from; i < to && i < buckets.Length; i++)
            {
                peak = Math.Max(peak, buckets[i]);
            }

            result[x] = Math.Clamp(peak, 0, 1);
        }

        return result;
    }

    /// <summary>Position after seeking by <paramref name="delta"/>, kept inside [0, duration] (duration 0 = unknown: only the lower bound).</summary>
    public static TimeSpan Seek(TimeSpan position, TimeSpan delta, TimeSpan duration)
    {
        TimeSpan target = position + delta;
        if (target < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return duration > TimeSpan.Zero && target > duration ? duration : target;
    }

    /// <summary>Volume after a step, kept inside [0, 1] and rounded to the step (no drift from repeated steps).</summary>
    public static double Volume(double volume, double delta) =>
        Math.Round(Math.Clamp(volume + delta, 0, 1) / VolumeStep, MidpointRounding.AwayFromZero) * VolumeStep;

    /// <summary>"m:ss", or "h:mm:ss" from one hour.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}:{duration.Seconds:00}");
    }
}
