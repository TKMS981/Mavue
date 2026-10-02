using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Viewer")]
public sealed class MediaControlMathTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(30, 10, 60, 40)]
    [InlineData(55, 10, 60, 60)]  // stops at the end
    [InlineData(5, -10, 60, 0)]   // stops at the start
    [InlineData(5, 10, 0, 15)]    // unknown duration: no upper bound
    public void Seek_StaysInsideTheMedia(double position, double delta, double duration, double expected) =>
        Assert.Equal(S(expected), MediaControlMath.Seek(S(position), S(delta), S(duration)));

    [Theory]
    [InlineData(0.5, 0.1, 0.6)]
    [InlineData(0.95, 0.1, 1.0)]
    [InlineData(0.05, -0.1, 0.0)]
    [InlineData(1.0, 0.1, 1.0)]
    public void Volume_StaysInsideZeroToOne(double volume, double delta, double expected) =>
        Assert.Equal(expected, MediaControlMath.Volume(volume, delta), 6);

    [Fact]
    public void Volume_RepeatedSteps_DoNotDrift()
    {
        double v = 0;
        for (int i = 0; i < 7; i++)
        {
            v = MediaControlMath.Volume(v, MediaControlMath.VolumeStep);
        }

        Assert.Equal(0.7, v, 10);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(37230, "10:20:30")]
    [InlineData(-5, "0:00")]
    public void FormatDuration(double seconds, string expected) =>
        Assert.Equal(expected, MediaControlMath.FormatDuration(S(seconds)));
}
