using System.Text;
using Mavue.Core.Formats;
using Mavue.Core.Settings;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Unit")]
public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "Mavue.Tests", "設定 " + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_Or_Broken_GivesDefaults()
    {
        Assert.Equal(AppTheme.System, AppSettings.Load(SettingsPath).Theme);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ not json");
        AppSettings loaded = AppSettings.Load(SettingsPath);
        Assert.Equal(ImageScaleMode.FitNoUpscale, loaded.DefaultScale);
        Assert.Empty(loaded.RecentFiles);
        Assert.True(loaded.ShowPageThumbnails);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var settings = new AppSettings
        {
            Theme = AppTheme.Dark,
            DefaultScale = ImageScaleMode.ActualSize,
            ShowInfoPane = true,
            RecentFiles = [@"C:\写真\a.jpg"],
            Window = new WindowPlacement(10, 20, 800, 600, Maximized: true),
        };
        await settings.SaveAsync(SettingsPath, TestContext.Current.CancellationToken);

        AppSettings loaded = AppSettings.Load(SettingsPath);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.Equal(ImageScaleMode.ActualSize, loaded.DefaultScale);
        Assert.True(loaded.ShowInfoPane);
        Assert.Equal([@"C:\写真\a.jpg"], loaded.RecentFiles);
        Assert.Equal(new WindowPlacement(10, 20, 800, 600, true), loaded.Window);
        Assert.Contains("\"theme\": \"Dark\"", await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void RecentFiles_MostRecentFirst_NoDuplicates_Limited()
    {
        var settings = new AppSettings();
        for (int i = 0; i < 20; i++)
        {
            settings = settings.WithRecentFile($@"C:\f{i}.jpg");
        }

        settings = settings.WithRecentFile(@"C:\F5.JPG");
        Assert.Equal(AppSettings.MaxRecentFiles, settings.RecentFiles.Count);
        Assert.Equal(@"C:\F5.JPG", settings.RecentFiles[0]);
        Assert.Single(settings.RecentFiles, f => f.Equals(@"C:\f5.jpg", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Update_ReReadsTheFile()
    {
        await new AppSettings { Theme = AppTheme.Light }.SaveAsync(SettingsPath, TestContext.Current.CancellationToken);

        // Another window changed the theme; this window adds a recent file without losing that change.
        AppSettings updated = await AppSettings.Update(SettingsPath, s => s.WithRecentFile(@"D:\x.pdf"), TestContext.Current.CancellationToken);
        Assert.Equal(AppTheme.Light, updated.Theme);
        Assert.Equal(AppTheme.Light, AppSettings.Load(SettingsPath).Theme);
        Assert.Equal([@"D:\x.pdf"], AppSettings.Load(SettingsPath).RecentFiles);
    }

    [Fact]
    public void Sanitized_DropsInvalidValues()
    {
        AppSettings sanitized = new AppSettings
        {
            Theme = (AppTheme)42,
            DefaultScale = (ImageScaleMode)9,
            RecentFiles = ["relative.jpg", "", @"C:\ok.jpg", @"c:\OK.jpg"],
            Window = new WindowPlacement(0, 0, 0, 100, false),
        }.Sanitized();
        Assert.Equal(AppTheme.System, sanitized.Theme);
        Assert.Equal(ImageScaleMode.FitNoUpscale, sanitized.DefaultScale);
        Assert.Equal([@"C:\ok.jpg"], sanitized.RecentFiles);
        Assert.Null(sanitized.Window);
    }

    [Fact]
    public async Task MissingProperties_KeepTheirDefaults()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, "{\"version\": 1, \"theme\": \"Dark\"}", TestContext.Current.CancellationToken);

        AppSettings loaded = AppSettings.Load(SettingsPath);
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.True(loaded.ShowPageThumbnails);
        Assert.Equal(PdfLayoutMode.Continuous, loaded.PdfLayout);
        Assert.Empty(loaded.RecentFiles);
    }
}

[Trait("Category", "Unit")]
public sealed class SvgDimensionsTests
{
    private static (double Width, double Height)? Read(string svg) => SvgDimensions.TryRead(new MemoryStream(Encoding.UTF8.GetBytes(svg)));

    [Fact]
    public void WidthAndHeight_InPixelsAndAbsoluteUnits()
    {
        Assert.Equal((400, 300), Read("<svg xmlns='http://www.w3.org/2000/svg' width='400' height='300px'/>"));
        Assert.Equal((96, 48), Read("<?xml version='1.0'?><!-- c --><svg width='1in' height='0.5in'/>"));
        (double w, double h) = Read("<svg width='72pt' height='25.4mm'/>")!.Value;
        Assert.Equal(96, w, 6);
        Assert.Equal(96, h, 6);
    }

    [Fact]
    public void ViewBox_GivesTheSize_OrTheAspectRatio()
    {
        Assert.Equal((200, 100), Read("<svg viewBox='0 0 200 100'/>"));
        Assert.Equal((400, 200), Read("<svg width='400' viewBox='0,0,200,100'/>"));
        Assert.Equal((200, 100), Read("<svg width='100%' height='100%' viewBox='0 0 200 100'/>"));
        Assert.Equal((1000, 500), Read("<svg height='500' viewBox='0 0 20 10'/>"));
    }

    [Fact]
    public void NoSize_UsesTheCssDefault() => Assert.Equal(SvgDimensions.Default, Read("<svg/>"));

    [Theory]
    [InlineData("<html/>")]
    [InlineData("not xml")]
    [InlineData("")]
    public void NotSvg_IsNull(string text) => Assert.Null(Read(text));

    [Fact]
    public void Dtd_IsNotProcessed()
    {
        // An external entity must never be resolved; the document still gives its size (or nothing), without reading files.
        const string hostile = "<?xml version='1.0'?><!DOCTYPE svg [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><svg width='10' height='20'>&x;</svg>";
        Assert.Equal((10, 20), Read(hostile));
    }
}

[Trait("Category", "Unit")]
public sealed class CameraRawDetectionTests
{
    [Theory]
    [InlineData(".nef")]
    [InlineData(".CR2")]
    [InlineData(".dng")]
    [InlineData(".arw")]
    public void TiffBasedRaw_IsRecognizedByExtension(string extension)
    {
        byte[] tiff = [(byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0];
        Assert.Equal(FileFormat.CameraRaw, FileFormatDetector.Detect(tiff, extension));
        Assert.Equal(FileFormat.Tiff, FileFormatDetector.Detect(tiff, ".tif"));
    }

    [Fact]
    public void Cr3_And_Raf_AreRecognizedByContent()
    {
        byte[] cr3 = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'c', (byte)'r', (byte)'x', (byte)' ', 0, 0, 0, 1, (byte)'c', (byte)'r', (byte)'x', (byte)' '];
        Assert.Equal(FileFormat.CameraRaw, FileFormatDetector.Detect(cr3, ".cr3"));
        Assert.Equal(FileFormat.CameraRaw, FileFormatDetector.Detect(Encoding.ASCII.GetBytes("FUJIFILMCCD-RAW 0201"), ".raf"));
    }

    [Fact]
    public void RawAndSvg_AreViewableImages()
    {
        Assert.True(FileFormatKinds.IsImage(FileFormat.CameraRaw));
        Assert.True(FileFormatKinds.IsImage(FileFormat.Svg));
        Assert.False(FileFormatKinds.IsImage(FileFormat.Pdf));
        Assert.True(ViewerFormats.HasViewableExtension(@"C:\a\b.ORF"));
    }
}
