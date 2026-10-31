using Mavue.Core.Formats;
using Mavue.Core.Viewing;

namespace Mavue.Core.Tests;

/// <summary>Universal Preview's facts, Mark-of-the-Web and the drag-out policy (docs/ARCHITECTURE.md §4.7.8).</summary>
[Trait("Category", "QuickView")]
public sealed class UniversalFileInfoTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-universal-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void File_FactsWithoutReadingTheContents()
    {
        string path = Path.Combine(_dir, "報告書 2026.docx");
        File.WriteAllBytes(path, new byte[1234]);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            UniversalFileInfo info = UniversalFileInfo.Read(path);

            Assert.True(info.Exists);
            Assert.False(info.IsFolder);
            Assert.Equal("報告書 2026.docx", info.Name);
            Assert.Equal(".docx", info.Extension);
            Assert.Equal(1234, info.Length);
            Assert.Equal(FileKindCategory.Document, info.Category);
            Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", info.MimeType);
            Assert.True(info.Attributes.HasFlag(FileAttributes.ReadOnly));
            Assert.NotNull(info.ModifiedUtc);
            Assert.Null(info.Origin);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void DownloadedFile_HasItsMarkOfTheWeb_HostOnly()
    {
        string path = Path.Combine(_dir, "setup.zip");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x03, 0x04]);
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nReferrerUrl=https://downloads.example.com/page?user=secret\r\nHostUrl=https://cdn.example.net/file.zip\r\n");

        UniversalFileInfo info = UniversalFileInfo.Read(path);

        Assert.Equal(new MarkOfTheWeb(3, "downloads.example.com"), info.Origin);
        Assert.True(info.Origin!.IsFromInternet);
        Assert.Equal(FileKindCategory.Archive, info.Category);
        Assert.True(File.Exists(path + ":Zone.Identifier")); // read, never removed
    }

    [Fact]
    public void Folder_IsAFolder()
    {
        string folder = Path.Combine(_dir, "写真");
        Directory.CreateDirectory(folder);

        UniversalFileInfo info = UniversalFileInfo.Read(folder);

        Assert.True(info.Exists);
        Assert.True(info.IsFolder);
        Assert.Equal("写真", info.Name);
        Assert.Null(info.Length);
        Assert.Equal(FileKindCategory.Folder, info.Category);
        Assert.Equal("写真", UniversalFileInfo.Read(folder + Path.DirectorySeparatorChar).Name);
    }

    [Fact]
    public void MissingFile_DoesNotExist_ButKeepsItsKind()
    {
        UniversalFileInfo info = UniversalFileInfo.Read(Path.Combine(_dir, "gone.pptx"));

        Assert.False(info.Exists);
        Assert.Equal(FileKindCategory.Presentation, info.Category);
        Assert.Null(info.Length);
    }

    [Fact]
    public void DevicePath_IsNeverOpened()
    {
        UniversalFileInfo info = UniversalFileInfo.Read(@"\\.\PhysicalDrive0");
        Assert.False(info.Exists);
        Assert.Equal(FileKindCategory.Other, info.Category);
    }

    [Theory]
    [InlineData("[ZoneTransfer]\nZoneId=3\nHostUrl=https://example.com/a.exe", 3, "example.com")]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\n", 3, null)]
    [InlineData("\uFEFF[ZoneTransfer]\r\nZoneId=4", 4, null)]
    [InlineData("[zonetransfer]\r\nzoneid = 2\r\nhosturl = http://intranet.local/x", 2, "intranet.local")]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=javascript:alert(1)", 3, null)]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=file:///C:/x", 3, null)]
    [InlineData("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=about:internet", 3, null)]
    public void ParseZoneIdentifier_ValidStreams(string text, int zone, string? host)
    {
        Assert.Equal(new MarkOfTheWeb(zone, host), UniversalFileInfo.ParseZoneIdentifier(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZoneId=3")]                                   // outside the section
    [InlineData("[Other]\r\nZoneId=3")]
    [InlineData("[ZoneTransfer]\r\nZoneId=seven")]
    [InlineData("[ZoneTransfer]\r\nZoneId=9")]
    [InlineData("[ZoneTransfer]\r\nHostUrl=https://example.com")]
    public void ParseZoneIdentifier_WithoutAValidZone_IsNull(string text)
    {
        Assert.Null(UniversalFileInfo.ParseZoneIdentifier(text));
    }

    [Fact]
    public void ParseZoneIdentifier_LocalZones_AreNotFromTheInternet()
    {
        Assert.False(UniversalFileInfo.ParseZoneIdentifier("[ZoneTransfer]\r\nZoneId=1")!.IsFromInternet);
        Assert.True(UniversalFileInfo.ParseZoneIdentifier("[ZoneTransfer]\r\nZoneId=4")!.IsFromInternet);
    }

    [Fact]
    public void OversizedZoneIdentifier_IsIgnored()
    {
        string path = Path.Combine(_dir, "big-motw.txt");
        File.WriteAllText(path, "x");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n" + new string(';', UniversalFileInfo.MaxZoneIdentifierBytes));
        Assert.Null(UniversalFileInfo.ReadMarkOfTheWeb(path));
    }

    [Theory]
    [InlineData(".jpg", FileFormat.Unknown, FileKindCategory.Image)]
    [InlineData(".mp4", FileFormat.Unknown, FileKindCategory.Video)]
    [InlineData(".flac", FileFormat.Unknown, FileKindCategory.Audio)]
    [InlineData(".pdf", FileFormat.Unknown, FileKindCategory.Pdf)]
    [InlineData(".xlsx", FileFormat.Unknown, FileKindCategory.Spreadsheet)]
    [InlineData(".PPTX", FileFormat.Unknown, FileKindCategory.Presentation)]
    [InlineData(".txt", FileFormat.Unknown, FileKindCategory.Text)]
    [InlineData(".cs", FileFormat.Unknown, FileKindCategory.Code)]
    [InlineData(".7z", FileFormat.Unknown, FileKindCategory.Archive)]
    [InlineData(".exe", FileFormat.Unknown, FileKindCategory.Application)]
    [InlineData(".lnk", FileFormat.Unknown, FileKindCategory.Shortcut)]
    [InlineData(".otf", FileFormat.Unknown, FileKindCategory.Font)]
    [InlineData(".iso", FileFormat.Unknown, FileKindCategory.DiskImage)]
    [InlineData(".xyz", FileFormat.Unknown, FileKindCategory.Other)]
    [InlineData("", FileFormat.Unknown, FileKindCategory.Other)]
    [InlineData(".txt", FileFormat.Png, FileKindCategory.Image)]   // the content wins over the extension
    [InlineData(".bin", FileFormat.Pdf, FileKindCategory.Pdf)]
    public void CategoryFor_ContentThenExtension(string extension, FileFormat format, FileKindCategory expected)
    {
        Assert.Equal(expected, UniversalFileInfo.CategoryFor(extension, format));
    }

    [Theory]
    [InlineData(".png", FileFormat.Unknown, "image/png")]
    [InlineData(".bin", FileFormat.Jpeg, "image/jpeg")]
    [InlineData(".zip", FileFormat.Unknown, "application/zip")]
    [InlineData(".json", FileFormat.Unknown, "application/json")]
    [InlineData(".mkv", FileFormat.Unknown, "video/x-matroska")]
    [InlineData(".xyz", FileFormat.Unknown, null)]
    public void MimeTypeFor_KnownTypes(string extension, FileFormat format, string? expected)
    {
        Assert.Equal(expected, UniversalFileInfo.MimeTypeFor(extension, format));
    }

    [Fact]
    public void CountEntries_StopsAtTheLimit()
    {
        string folder = Path.Combine(_dir, "many");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < 25; i++)
        {
            File.WriteAllText(Path.Combine(folder, $"{i}.txt"), string.Empty);
        }

        Directory.CreateDirectory(Path.Combine(folder, "sub"));

        Assert.Equal(26, UniversalFileInfo.CountEntries(folder, 1000, TestContext.Current.CancellationToken));
        Assert.Equal(10, UniversalFileInfo.CountEntries(folder, 10, TestContext.Current.CancellationToken));
        Assert.Null(UniversalFileInfo.CountEntries(Path.Combine(_dir, "missing"), 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CountEntries_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => UniversalFileInfo.CountEntries(_dir, 1000, cts.Token));
    }

    [Theory]
    [InlineData(PreviewAccess.Allowed, true)]
    [InlineData(PreviewAccess.CloudPlaceholder, true)]   // the user's own drag may download it; Quick View does not
    [InlineData(PreviewAccess.TooLarge, true)]
    [InlineData(PreviewAccess.NotAFile, false)]
    public void DragOut_OnlyFiles(PreviewAccess access, bool expected)
    {
        Assert.Equal(expected, DragOutPolicy.CanDrag(new FileFacts(0, 1, FileFormat.Unknown, access)));
    }

    [Fact]
    public void DragOut_NeverMoves()
    {
        Assert.False(DragOutPolicy.AllowsMove);
    }
}
