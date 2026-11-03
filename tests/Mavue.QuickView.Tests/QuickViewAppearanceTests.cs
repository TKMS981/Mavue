using Mavue.Core.Settings;
using Mavue.Core.Viewing;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class QuickViewWindowSizeTests
{
    [Theory]
    [InlineData(3840, 2100, 1.5, 2150, 1344)] // 4K at 150 %: 60 % × 64 %, then 16:10 at most
    [InlineData(1920, 1032, 1.0, 1057, 660)]  // full HD
    [InlineData(3840, 2112, 1.0, 1600, 1040)] // 4K at 100 %: capped in DIPs
    [InlineData(5120, 1400, 1.0, 1434, 896)]  // ultrawide: no strip
    [InlineData(1080, 1872, 1.0, 648, 810)]   // portrait monitor: 4:5 at most
    [InlineData(1366, 728, 1.0, 745, 480)]    // small laptop: at least 480 DIPs high
    [InlineData(800, 600, 1.5, 800, 600)]     // tiny work area: never larger than it
    [InlineData(1920, 1032, 0.0, 1057, 660)]  // unknown scale counts as 100 %
    public void InitialSize_FollowsTheWorkArea(int workWidth, int workHeight, double scale, int width, int height)
    {
        Assert.Equal((width, height), QuickViewWindowSize.For(workWidth, workHeight, scale));
    }

    [Theory]
    [InlineData(3840, 2100, 1.5)]
    [InlineData(2560, 1392, 1.0)]
    [InlineData(1920, 1032, 1.25)]
    [InlineData(2560, 1080, 1.0)]
    public void InitialSize_IsSmallerThanBefore(int workWidth, int workHeight, double scale)
    {
        (int width, int height) = QuickViewWindowSize.For(workWidth, workHeight, scale);

        Assert.True(width < workWidth * 0.7 && height < workHeight * 0.75, $"{width}x{height}");
        Assert.InRange(width / (double)height, QuickViewWindowSize.TallestAspect, QuickViewWindowSize.WidestAspect + 0.01);
    }
}

[Trait("Category", "QuickView")]
public sealed class QuickViewTransparencySettingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-appearance-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task OlderFile_GetsTheDefaultTransparency()
    {
        string path = Path.Combine(_dir, "older.json");
        await File.WriteAllTextAsync(path, "{\"version\": 1, \"imageScale\": \"FitNoUpscale\"}", TestContext.Current.CancellationToken);

        Assert.Equal(QuickViewSettings.DefaultBackgroundTransparency, QuickViewSettings.Load(path).BackgroundTransparency);
        Assert.Equal(QuickViewSettings.DefaultBackgroundTransparency, QuickViewSettings.Load(Path.Combine(_dir, "none.json")).BackgroundTransparency);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 20)]
    [InlineData(-5, 0)]
    [InlineData(95, QuickViewSettings.MaxBackgroundTransparency)]
    public async Task Transparency_IsKeptWithinRange(int written, int loaded)
    {
        string path = Path.Combine(_dir, $"t{written}.json");
        await File.WriteAllTextAsync(path, $"{{\"version\": 1, \"imageScale\": \"ActualSize\", \"backgroundTransparency\": {written}}}", TestContext.Current.CancellationToken);

        QuickViewSettings settings = QuickViewSettings.Load(path);
        Assert.Equal(loaded, settings.BackgroundTransparency);
        Assert.Equal(ImageScaleMode.ActualSize, settings.ImageScale); // other choices untouched
    }

    [Fact]
    public async Task Transparency_RoundTrips_WithoutTouchingOtherChoices()
    {
        string path = Path.Combine(_dir, "settings.json");
        await new QuickViewSettings { ShowFilmstrip = true }.SaveAsync(path, TestContext.Current.CancellationToken);
        await (QuickViewSettings.Load(path) with { BackgroundTransparency = 0 }).SaveAsync(path, TestContext.Current.CancellationToken);

        QuickViewSettings settings = QuickViewSettings.Load(path);
        Assert.Equal(0, settings.BackgroundTransparency);
        Assert.True(settings.ShowFilmstrip);
        Assert.Contains("\"backgroundTransparency\": 0", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
