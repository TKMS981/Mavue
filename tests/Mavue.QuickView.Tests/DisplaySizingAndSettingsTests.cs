using Mavue.Core.Viewing;
using Mavue.QuickView.Preview;
using Mavue.QuickView.Settings;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class DisplaySizingTests
{
    [Fact]
    public void Fit_DecodesToTheImageArea()
    {
        Assert.Equal(new DecodeBox(1700, 900), DisplaySizing.DecodeBoxFor(ImageScaleMode.FitNoUpscale, 1700, 900, 6000, 4000));
    }

    [Fact]
    public void ActualSize_DecodesTheSourceSize()
    {
        Assert.Equal(new DecodeBox(6000, 4000), DisplaySizing.DecodeBoxFor(ImageScaleMode.ActualSize, 1700, 900, 6000, 4000));
    }

    [Fact]
    public void ActualSize_OverBudget_FallsBackToFit()
    {
        DecodeBox box = DisplaySizing.DecodeBoxFor(ImageScaleMode.ActualSize, 1700, 900, 16000, 12000);
        Assert.Equal(new DecodeBox(1700, 900, ActualSizeRefused: true), box);
    }

    [Fact]
    public void ActualSize_UnknownSource_Fits()
    {
        Assert.Equal(new DecodeBox(1700, 900), DisplaySizing.DecodeBoxFor(ImageScaleMode.ActualSize, 1700, 900, 0, 0));
    }

    [Theory]
    [InlineData(200u, 200u, 1.0, 200.0, 200.0)]
    [InlineData(300u, 150u, 1.5, 200.0, 100.0)]   // 150 % scaling: 300 physical px = 200 DIPs
    [InlineData(400u, 400u, 2.0, 200.0, 200.0)]
    [InlineData(100u, 100u, 0.0, 100.0, 100.0)]   // unknown scale treated as 1
    public void ElementSize_MapsPixelsOneToOne(uint w, uint h, double scale, double ew, double eh)
    {
        Assert.Equal((ew, eh), DisplaySizing.ElementSize(w, h, scale));
    }

    [Fact]
    public void SmallImage_IsNeverEnlargedWhenFitting()
    {
        // A 200x200 icon in a 1700x900 area stays 200x200 (the quality complaint that motivated this).
        Assert.Equal((200u, 200u), DisplaySizing.ExpectedDisplayPixels(ImageScaleMode.FitNoUpscale, 1700, 900, 200, 200));
    }

    [Fact]
    public void LargeImage_FitsTheArea()
    {
        Assert.Equal((1350u, 900u), DisplaySizing.ExpectedDisplayPixels(ImageScaleMode.FitNoUpscale, 1700, 900, 6000, 4000));
    }
}

[Trait("Category", "QuickView")]
public sealed class QuickViewSettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        QuickViewSettings settings = QuickViewSettings.Load(Path.Combine(_dir, "none.json"));
        Assert.Equal(ImageScaleMode.FitNoUpscale, settings.ImageScale);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        string path = Path.Combine(_dir, "nested", "settings.json");
        await new QuickViewSettings { ImageScale = ImageScaleMode.ActualSize }.SaveAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ImageScaleMode.ActualSize, QuickViewSettings.Load(path).ImageScale);
        Assert.Contains("\"imageScale\": \"ActualSize\"", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"imageScale\": \"Zoom9000\"}")]
    [InlineData("{\"imageScale\": 42}")]
    [InlineData("")]
    public async Task InvalidContent_GivesDefaults(string content)
    {
        string path = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        Assert.Equal(ImageScaleMode.FitNoUpscale, QuickViewSettings.Load(path).ImageScale);
    }

    [Fact]
    public async Task UnknownProperties_AreIgnored()
    {
        string path = Path.Combine(_dir, "future.json");
        await File.WriteAllTextAsync(path, "{\"version\": 2, \"imageScale\": \"ActualSize\", \"somethingNew\": true}", TestContext.Current.CancellationToken);

        Assert.Equal(ImageScaleMode.ActualSize, QuickViewSettings.Load(path).ImageScale);
    }
}
