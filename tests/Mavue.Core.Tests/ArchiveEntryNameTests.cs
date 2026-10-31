using Mavue.Core.Archives;

namespace Mavue.Core.Tests;

/// <summary>Entry names of archives (ZIP) are checked before they are shown or ever used as a path (ARCHITECTURE §4.7.5).</summary>
[Trait("Category", "Archive")]
public class ArchiveEntryNameTests
{
    [Theory]
    [InlineData("a.txt")]
    [InlineData("folder/b.png")]
    [InlineData(@"folder\b.png")]
    [InlineData("folder/")]                 // directory entry
    [InlineData("日本語/写真 01.jpg")]
    [InlineData("./a.txt")]                 // "." is dropped by Normalize
    [InlineData("a..b/c.txt")]              // dots inside a name
    [InlineData(".gitignore")]
    [InlineData("file.txt.")]               // trailing dot: a different name, not a parent folder
    [InlineData("CONSOLE.txt")]             // not a device name
    [InlineData("COM10")]
    [InlineData("nul_file.txt")]
    [InlineData("LPT")]
    public void SafeNames(string name)
    {
        Assert.Equal(ArchiveNameCheck.Safe, ArchiveEntryName.Check(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("./")]
    public void EmptyNames(string name)
    {
        Assert.NotEqual(ArchiveNameCheck.Safe, ArchiveEntryName.Check(name));
    }

    [Fact]
    public void Empty_IsReportedAsEmpty()
    {
        Assert.Equal(ArchiveNameCheck.Empty, ArchiveEntryName.Check(string.Empty));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\System32\x.dll")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("C:x.dll")]                 // drive-relative
    [InlineData(@"\\server\share\x")]       // UNC
    [InlineData("//server/share/x")]
    [InlineData(@"\\?\C:\x")]               // device path
    [InlineData("/")]
    public void AbsolutePaths(string name)
    {
        Assert.Equal(ArchiveNameCheck.Absolute, ArchiveEntryName.Check(name));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData(@"..\evil.exe")]
    [InlineData("a/../../evil.exe")]
    [InlineData(@"a\..\..\evil.exe")]
    [InlineData("a/..")]
    [InlineData("..")]
    [InlineData(".. /evil.exe")]           // Windows drops the trailing space: ".."
    [InlineData("../ evil.exe")]
    [InlineData("a/.../b")]                // only dots
    [InlineData("a/. ./b")]
    [InlineData("a/ /b")]                  // only a space
    public void Traversal(string name)
    {
        Assert.Equal(ArchiveNameCheck.Traversal, ArchiveEntryName.Check(name));
    }

    [Theory]
    [InlineData("a.txt:hidden")]            // alternate data stream
    [InlineData("dir/b:c")]
    [InlineData("a\u0000b")]
    [InlineData("a\u0001b")]
    [InlineData("line\nbreak")]
    [InlineData("tab\there")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void InvalidCharacters(string name)
    {
        Assert.Equal(ArchiveNameCheck.InvalidCharacters, ArchiveEntryName.Check(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("dir/PRN")]
    [InlineData("AUX.tar.gz")]
    [InlineData("COM1")]
    [InlineData("com9.log")]
    [InlineData("LPT1")]
    [InlineData("COM¹")]
    [InlineData("LPT³.txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$")]
    [InlineData("CON .txt")]               // trailing space before the extension
    public void DeviceNames(string name)
    {
        Assert.Equal(ArchiveNameCheck.DeviceName, ArchiveEntryName.Check(name));
    }

    [Fact]
    public void LongestAcceptedName_IsTheLimit()
    {
        int max = ArchiveLimits.Default.MaxNameLength;
        Assert.Equal(ArchiveNameCheck.Safe, ArchiveEntryName.Check(new string('a', max)));
        Assert.Equal(ArchiveNameCheck.TooLong, ArchiveEntryName.Check(new string('a', max + 1)));
    }

    [Fact]
    public void DeepestAcceptedNesting_IsTheLimit()
    {
        int depth = ArchiveLimits.Default.MaxPathDepth;
        Assert.Equal(ArchiveNameCheck.Safe, ArchiveEntryName.Check(string.Join('/', Enumerable.Repeat("d", depth))));
        Assert.Equal(ArchiveNameCheck.TooDeep, ArchiveEntryName.Check(string.Join('/', Enumerable.Repeat("d", depth + 1))));
    }

    [Fact]
    public void CustomLimits_AreApplied()
    {
        var limits = new ArchiveLimits { MaxNameLength = 8, MaxPathDepth = 2 };
        Assert.Equal(ArchiveNameCheck.TooLong, ArchiveEntryName.Check("123456789", limits));
        Assert.Equal(ArchiveNameCheck.TooDeep, ArchiveEntryName.Check("a/b/c", limits));
        Assert.Equal(ArchiveNameCheck.Safe, ArchiveEntryName.Check("a/b", limits));
    }

    [Fact]
    public void TooLong_IsCheckedBeforeSplitting()
    {
        // A huge name is rejected without allocating its segments.
        Assert.Equal(ArchiveNameCheck.TooLong, ArchiveEntryName.Check(string.Join('/', Enumerable.Repeat("..", 100_000))));
    }

    [Theory]
    [InlineData(@"a\b//c/./d", "a/b/c/d")]
    [InlineData("folder/", "folder")]
    [InlineData("./a.txt", "a.txt")]
    [InlineData("日本語\\写真.jpg", "日本語/写真.jpg")]
    [InlineData("", "")]
    public void Normalize_UsesSlashes_WithoutEmptyOrDotSegments(string name, string expected)
    {
        Assert.Equal(expected, ArchiveEntryName.Normalize(name));
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => ArchiveEntryName.Check(null!));
        Assert.Throws<ArgumentNullException>(() => ArchiveEntryName.Normalize(null!));
    }
}
