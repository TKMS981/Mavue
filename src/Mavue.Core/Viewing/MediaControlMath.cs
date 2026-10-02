using System.Globalization;

namespace Mavue.Core.Viewing;

/// <summary>Keyboard media commands of Quick View (seek and volume steps) and how durations are shown.</summary>
public static class MediaControlMath
{
    public static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    public const double VolumeStep = 0.1;

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
