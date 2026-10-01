using Mavue.QuickView.Preview;

namespace Mavue.QuickView.Tests;

[Trait("Category", "QuickView")]
public class PreviewSizingTests
{
    [Theory]
    [InlineData(800u, 600u, 2000u, 1500u, 800u, 600u)]          // smaller than viewport: never upscale
    [InlineData(6000u, 4000u, 2025u, 1519u, 2025u, 1350u)]      // width-bound
    [InlineData(4000u, 6000u, 2025u, 1519u, 1013u, 1519u)]      // height-bound (portrait)
    [InlineData(16000u, 12000u, 2025u, 1519u, 2025u, 1519u)]    // 192 MP decoded to screen size
    [InlineData(100000u, 10u, 2000u, 1000u, 2000u, 1u)]         // extreme aspect: at least 1 px
    public void FitWithin_PreservesAspectAndNeverUpscales(uint w, uint h, uint vw, uint vh, uint ew, uint eh)
    {
        Assert.Equal((ew, eh), PreviewSizing.FitWithin(w, h, vw, vh));
    }

    [Fact]
    public void FitWithin_ZeroViewport_ReturnsSource()
    {
        Assert.Equal((640u, 480u), PreviewSizing.FitWithin(640, 480, 0, 0));
    }

    [Fact]
    public void FitWithin_RejectsEmptyImages()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PreviewSizing.FitWithin(0, 10, 100, 100));
    }
}

[Trait("Category", "QuickView")]
public class PreviewSafetyPolicyTests
{
    [Theory]
    [InlineData(FileAttributes.Normal, PreviewAccess.Allowed)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Archive, PreviewAccess.Allowed)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, PreviewAccess.Allowed)]
    [InlineData(FileAttributes.ReparsePoint, PreviewAccess.Allowed)] // symlinks: read-only preview of the target is fine
    [InlineData(FileAttributes.Directory, PreviewAccess.NotAFile)]
    [InlineData(FileAttributes.Device, PreviewAccess.NotAFile)]
    [InlineData(FileAttributes.Offline, PreviewAccess.CloudPlaceholder)]
    [InlineData((FileAttributes)0x00400000, PreviewAccess.CloudPlaceholder)] // RECALL_ON_DATA_ACCESS (OneDrive online-only)
    [InlineData((FileAttributes)0x00040000, PreviewAccess.CloudPlaceholder)] // RECALL_ON_OPEN
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint | (FileAttributes)0x00400000, PreviewAccess.CloudPlaceholder)]
    public void CheckAttributes(FileAttributes attributes, PreviewAccess expected)
    {
        Assert.Equal(expected, PreviewSafetyPolicy.CheckAttributes(attributes));
    }

    [Theory]
    [InlineData(16000u, 12000u, PreviewAccess.Allowed)]
    [InlineData(40000u, 25000u, PreviewAccess.Allowed)]      // exactly 1 gigapixel
    [InlineData(65535u, 65535u, PreviewAccess.TooLarge)]
    [InlineData(0u, 100u, PreviewAccess.TooLarge)]
    public void CheckDimensions(uint width, uint height, PreviewAccess expected)
    {
        Assert.Equal(expected, PreviewSafetyPolicy.CheckDimensions(width, height));
    }

    [Theory]
    [InlineData(@"\\server\share\photo.jpg", true)]
    [InlineData(@"\\?\UNC\server\share\photo.jpg", true)]
    [InlineData(@"\\?\C:\very\long\path.jpg", false)]
    [InlineData(@"C:\Users\me\写真.jpg", false)]
    [InlineData(@"\\.\PhysicalDrive0", false)]
    public void IsNetworkPath(string path, bool expected)
    {
        Assert.Equal(expected, PreviewSafetyPolicy.IsNetworkPath(path));
    }

    [Theory]
    [InlineData(@"\\.\PhysicalDrive0", true)]
    [InlineData(@"\\.\pipe\something", true)]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\x.jpg", true)]
    [InlineData(@"\\?\C:\photo.jpg", false)]
    [InlineData(@"\\server\share\photo.jpg", false)]
    [InlineData(@"C:\photo.jpg", false)]
    public void IsDevicePath(string path, bool expected)
    {
        Assert.Equal(expected, PreviewSafetyPolicy.IsDevicePath(path));
    }
}
