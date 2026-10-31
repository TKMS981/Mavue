using Mavue.Core.Formats;
using Mavue.Core.Settings;

namespace Mavue.Core.Tests;

/// <summary>Which images get the checkerboard behind them (Quick Preview Phase 2, docs/ARCHITECTURE.md §4.7.9).</summary>
[Trait("Category", "QuickView")]
public sealed class TransparencyRuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-transparency-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(FileFormat.Png)]
    [InlineData(FileFormat.Gif)]
    [InlineData(FileFormat.WebP)]
    [InlineData(FileFormat.Avif)]
    [InlineData(FileFormat.Heif)]
    [InlineData(FileFormat.Tiff)]
    [InlineData(FileFormat.Bmp)]
    [InlineData(FileFormat.Ico)]
    [InlineData(FileFormat.Svg)]
    [InlineData(FileFormat.Jpeg2000)]
    [InlineData(FileFormat.JpegXl)]
    [InlineData(FileFormat.Unknown)] // an installed codec may decode it
    public void FormatsWithAlpha_GetTheCheckerboard(FileFormat format)
    {
        Assert.True(FileFormatKinds.MayHaveTransparency(format));
    }

    [Theory]
    [InlineData(FileFormat.Jpeg)]
    [InlineData(FileFormat.CameraRaw)]
    [InlineData(FileFormat.Pdf)]
    [InlineData(FileFormat.Mp4)]
    [InlineData(FileFormat.Mp3)]
    public void OpaqueFormats_DoNot(FileFormat format)
    {
        Assert.False(FileFormatKinds.MayHaveTransparency(format));
    }

    [Fact]
    public async Task Checkerboard_IsOnByDefault_AndCanBeTurnedOff()
    {
        Assert.True(new QuickViewSettings().ShowTransparencyGrid);

        string older = Path.Combine(_dir, "older.json");
        await File.WriteAllTextAsync(older, "{\"version\": 1, \"imageScale\": \"FitNoUpscale\"}", TestContext.Current.CancellationToken);
        Assert.True(QuickViewSettings.Load(older).ShowTransparencyGrid); // files written before Phase 2

        string path = Path.Combine(_dir, "off.json");
        await new QuickViewSettings { ShowTransparencyGrid = false }.SaveAsync(path, TestContext.Current.CancellationToken);
        Assert.False(QuickViewSettings.Load(path).ShowTransparencyGrid);
    }
}
