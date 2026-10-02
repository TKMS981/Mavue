using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

[Trait("Category", "Viewer")]
public sealed class ViewerFileListTests
{
    [Fact]
    public void FromFiles_KeepsOrder_RemovesDuplicates_StartsAtFirst()
    {
        ViewerFileList list = ViewerFileList.FromFiles([@"C:\b.jpg", @"C:\a.pdf", @"C:\B.JPG"]);

        Assert.Equal([@"C:\b.jpg", @"C:\a.pdf"], list.Items);
        Assert.Equal(0, list.Index);
        Assert.Equal(@"C:\b.jpg", list.Current);
    }

    [Fact]
    public void Step_StopsAtBothEnds()
    {
        ViewerFileList list = ViewerFileList.FromFiles([@"C:\1.jpg", @"C:\2.jpg"]);

        Assert.False(list.TryStep(-1));
        Assert.True(list.TryStep(+1));
        Assert.False(list.TryStep(+1));
        Assert.Equal(1, list.Index);
        Assert.False(list.CanStep(0));
    }

    [Fact]
    public void Empty_HasNoCurrent()
    {
        ViewerFileList list = ViewerFileList.FromFiles([]);

        Assert.Null(list.Current);
        Assert.Equal(-1, list.Index);
        Assert.False(list.TryStep(+1));
    }

    [Fact]
    public void FromFolder_FiltersAndSortsLikeExplorer_AndSelectsTheFile()
    {
        string[] folder = [@"D:\x\img10.jpg", @"D:\x\notes.txt", @"D:\x\img2.jpg", @"D:\x\Img1.png", @"D:\x\clip.mp4"];

        ViewerFileList list = ViewerFileList.FromFolder(@"D:\x\img2.jpg", folder, ViewerFormats.HasViewableExtension);

        Assert.Equal([@"D:\x\clip.mp4", @"D:\x\Img1.png", @"D:\x\img2.jpg", @"D:\x\img10.jpg"], list.Items);
        Assert.Equal(2, list.Index);
    }

    [Fact]
    public void FromFolder_KeepsTheOpenedFileEvenWithAnUnknownExtension()
    {
        string[] folder = [@"D:\x\a.jpg", @"D:\x\b.xyz"];

        ViewerFileList list = ViewerFileList.FromFolder(@"D:\x\b.xyz", folder, ViewerFormats.HasViewableExtension);

        Assert.Equal(@"D:\x\b.xyz", list.Current);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void FromFolder_FileMissingFromListing_IsAddedFirst()
    {
        ViewerFileList list = ViewerFileList.FromFolder(@"D:\x\new.jpg", [@"D:\x\a.jpg"], ViewerFormats.HasViewableExtension);

        Assert.Equal(@"D:\x\new.jpg", list.Current);
        Assert.Equal(0, list.Index);
    }

    [Theory]
    [InlineData("a2", "a10", -1)]
    [InlineData("a10", "a2", 1)]
    [InlineData("A1", "a1", -1)]  // equal ignoring case: ordinal tie-break keeps the order stable
    [InlineData("a01", "a1", -1)] // same value: the ordinal tie-break decides ("0" before "1")
    [InlineData("b", "a", 1)]
    [InlineData("file", "file 2", -1)]
    [InlineData("x99999999999999999999", "x100000000000000000000", -1)] // beyond long: compared as digit strings
    public void NaturalOrder(string x, string y, int sign) =>
        Assert.Equal(sign, Math.Sign(NaturalStringComparer.Instance.Compare(x, y)));

    [Fact]
    public void NaturalOrder_HandlesNulls()
    {
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
    }

    [Fact]
    public void ViewerFormats_CoverImagesPdfAndMedia()
    {
        Assert.Contains(FileFormat.Pdf, ViewerFormats.All);
        Assert.Contains(FileFormat.Mp4, ViewerFormats.All);
        Assert.Contains(FileFormat.Flac, ViewerFormats.All);
        Assert.Contains(FileFormat.Heif, ViewerFormats.All);
        Assert.Contains(".jpg", ViewerFormats.Extensions);
        Assert.Contains(".mkv", ViewerFormats.Extensions);
        Assert.DoesNotContain(".svg", ViewerFormats.Extensions); // no decoder yet
        Assert.True(ViewerFormats.HasViewableExtension(@"C:\a\B.JPG"));
        Assert.False(ViewerFormats.HasViewableExtension(@"C:\a\b.txt"));
        Assert.Equal(ViewerFormats.Extensions.Count, ViewerFormats.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5 * 1024 * 1024, "5 MB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
    public void ByteSize(long bytes, string expected)
    {
        using var culture = new CultureScope("en-US");
        Assert.Equal(expected, ByteSizeText.Format(bytes));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly System.Globalization.CultureInfo _previous = System.Globalization.CultureInfo.CurrentCulture;

        public CultureScope(string name) => System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(name);

        public void Dispose() => System.Globalization.CultureInfo.CurrentCulture = _previous;
    }
}
