using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Mavue.Core.Archives;

namespace Mavue.Core.Tests;

/// <summary>
/// ZIP preview without extraction (ARCHITECTURE §4.7.10): the listing from the central directory, names in every
/// encoding Windows users meet, and reading one entry with every size, ratio and CRC check.
/// </summary>
[Trait("Category", "Archive")]
public class ZipArchiveTests
{
    private const long MiB = 1024L * 1024;

    static ZipArchiveTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static byte[] Zip(Action<ZipArchive> fill, Encoding? names = null)
    {
        var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true, names))
        {
            fill(zip);
        }

        return memory.ToArray();
    }

    private static void Add(ZipArchive zip, string name, byte[] content, CompressionLevel level = CompressionLevel.Optimal)
    {
        using Stream stream = zip.CreateEntry(name, level).Open();
        stream.Write(content);
    }

    private static ZipDirectory Read(byte[] zip, ArchiveLimits? limits = null, int? codePage = null) =>
        ZipDirectory.Read(new MemoryStream(zip), limits, codePage);

    private static byte[] ReadEntry(byte[] zip, ZipEntry entry, ArchiveLimits? limits = null, DecompressionTotals? totals = null) =>
        ZipEntryReader.ReadAll(new MemoryStream(zip), entry, limits ?? ArchiveLimits.Default, totals ?? new DecompressionTotals()).ToArray();

    /// <summary>Offsets of the central directory headers (signature PK\1\2).</summary>
    private static List<int> CentralHeaders(byte[] zip)
    {
        var offsets = new List<int>();
        for (int i = 0; i + 4 <= zip.Length; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 1 && zip[i + 3] == 2)
            {
                offsets.Add(i);
            }
        }

        return offsets;
    }

    private static int EndRecord(byte[] zip)
    {
        for (int i = zip.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50)
            {
                return i;
            }
        }

        throw new InvalidOperationException("no end record");
    }

    [Fact]
    public void Crc32_MatchesTheStandardCheckValue()
    {
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        var crc = new Crc32();
        crc.Append("1234"u8);
        crc.Append("56789"u8);
        Assert.Equal(0xCBF43926u, crc.Value);
        Assert.Equal(0u, Crc32.Compute([]));
    }

    [Fact]
    public void Listing_ComesFromTheCentralDirectory()
    {
        byte[] zip = Zip(z =>
        {
            Add(z, "photos/b.jpg", new byte[1000]);
            Add(z, "readme.txt", Encoding.UTF8.GetBytes("hello"), CompressionLevel.NoCompression);
            z.CreateEntry("empty/");
            Add(z, "写真/夏.png", [1, 2, 3]);
        });

        ZipDirectory directory = Read(zip);

        Assert.Equal(4, directory.Entries.Count);
        ZipEntry photo = directory.Entries[0];
        Assert.Equal("photos/b.jpg", photo.Path);
        Assert.Equal(1000, photo.Size);
        Assert.Equal(ZipEntry.Deflate, photo.Method);
        Assert.True(photo.IsReadable);
        Assert.Equal(ZipEntry.Stored, directory.Entries[1].Method);
        Assert.True(directory.Entries[2].IsDirectory);
        Assert.Equal("写真/夏.png", directory.Entries[3].Path);
        Assert.All(directory.Entries, e => Assert.Equal(ArchiveNameCheck.Safe, e.NameCheck));
        Assert.Null(directory.LegacyNameEncoding); // flagged UTF-8 or ASCII
    }

    [Fact]
    public void LegacyNames_UseTheOemCodePage_AsExplorerDoes()
    {
        byte[] zip = Zip(z => Add(z, "日本語/写真.png", [1]), Encoding.GetEncoding(932));

        Assert.Equal("日本語/写真.png", Read(zip, codePage: 932).Entries[0].Path);
        Assert.Equal("shift_jis", Read(zip, codePage: 932).LegacyNameEncoding);

        // On an English system (CP437) the same bytes are not Japanese text, but still a name.
        Assert.NotEqual("日本語/写真.png", Read(zip, codePage: 437).Entries[0].Path);
    }

    [Fact]
    public void Utf8NamesWithoutTheFlag_AreRecognized()
    {
        byte[] zip = Zip(z => Add(z, "Café/ü.txt", [1]));
        foreach (int header in CentralHeaders(zip))
        {
            zip[header + 9] &= 0xF7; // clear bit 11 (UTF-8) of the general purpose flags
        }

        ZipDirectory directory = Read(zip, codePage: 932);

        Assert.Equal("Café/ü.txt", directory.Entries[0].Path);
        Assert.Equal("utf-8", directory.LegacyNameEncoding);
    }

    [Fact]
    public void PrefixData_LikeASelfExtractor_ShiftsEveryOffset()
    {
        byte[] content = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("data ", 500)));
        byte[] zip = Zip(z => Add(z, "a.txt", content));
        byte[] prefixed = [.. new byte[4096].Select((_, i) => (byte)(i * 7)), .. zip];

        ZipDirectory directory = Read(prefixed);

        Assert.Equal(content, ReadEntry(prefixed, directory.Entries[0]));
    }

    [Fact]
    public void Zip64_RecordsAndExtraFields_AreRead()
    {
        byte[] zip = Zip64Archive("big/a.txt", "hello zip64"u8.ToArray());

        ZipDirectory directory = Read(zip);

        ZipEntry entry = Assert.Single(directory.Entries);
        Assert.Equal("big/a.txt", entry.Path);
        Assert.Equal(11, entry.Size);
        Assert.Equal("hello zip64"u8.ToArray(), ReadEntry(zip, entry));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(5000)]
    public void NotAZip_IsReportedAsSuch(int length)
    {
        byte[] bytes = new byte[length];
        new Random(length).NextBytes(bytes);

        var error = Assert.Throws<ArchiveFormatException>(() => Read(bytes));
        Assert.Equal(ArchiveProblem.NotAnArchive, error.Problem);
    }

    [Fact]
    public void CentralDirectoryOutsideTheFile_IsDamaged()
    {
        byte[] zip = Zip(z => Add(z, "a.txt", [1, 2, 3]));
        int end = EndRecord(zip);
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(end + 12), 1_000_000); // directory size

        Assert.Equal(ArchiveProblem.Damaged, Assert.Throws<ArchiveFormatException>(() => Read(zip)).Problem);
    }

    [Fact]
    public void SplitArchives_AreRecognized()
    {
        byte[] zip = Zip(z => Add(z, "a.txt", [1]));
        int end = EndRecord(zip);
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(end + 4), 2); // this disk

        Assert.Equal(ArchiveProblem.MultiVolume, Assert.Throws<ArchiveFormatException>(() => Read(zip)).Problem);
    }

    [Fact]
    public void TooManyEntries_AreRefusedBeforeTheDirectoryIsRead()
    {
        byte[] zip = Zip(z =>
        {
            for (int i = 0; i < 5; i++)
            {
                Add(z, $"{i}.txt", [1]);
            }
        });

        var error = Assert.Throws<ArchiveLimitExceededException>(() => Read(zip, new ArchiveLimits { MaxEntries = 4 }));
        Assert.Equal(nameof(ArchiveLimits.MaxEntries), error.Limit);
        var size = Assert.Throws<ArchiveLimitExceededException>(() => Read(zip, new ArchiveLimits { MaxCentralDirectoryBytes = 64 }));
        Assert.Equal(nameof(ArchiveLimits.MaxCentralDirectoryBytes), size.Limit);
    }

    [Fact]
    public void UnsafeNames_AreListedWithTheirCheck()
    {
        byte[] zip = Zip(z =>
        {
            Add(z, "../evil.txt", [1]);
            Add(z, "C:/Windows/x.dll", [1]);
            Add(z, "photo\u202Egpj.exe", [1]);
            Add(z, "ok.txt", [1]);
        });

        ArchiveNameCheck[] checks = Read(zip).Entries.Select(e => e.NameCheck).ToArray();

        Assert.Equal([ArchiveNameCheck.Traversal, ArchiveNameCheck.Absolute, ArchiveNameCheck.BidiControl, ArchiveNameCheck.Safe], checks);
    }

    [Fact]
    public void Entries_RoundTrip_StoredAndDeflated()
    {
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Mavue ", 10_000)));
        byte[] random = new byte[70_000];
        new Random(3).NextBytes(random);
        byte[] zip = Zip(z =>
        {
            Add(z, "text.txt", text);
            Add(z, "random.bin", random, CompressionLevel.NoCompression);
        });
        ZipDirectory directory = Read(zip);

        Assert.Equal(text, ReadEntry(zip, directory.Entries[0]));
        Assert.Equal(random, ReadEntry(zip, directory.Entries[1]));
    }

    [Fact]
    public void ReadingAFolder_Encrypted_OrAnotherMethod_SaysWhy()
    {
        byte[] zip = Zip(z =>
        {
            z.CreateEntry("folder/");
            Add(z, "secret.txt", [1, 2, 3]);
            Add(z, "lzma.bin", [1, 2, 3]);
        });
        List<int> headers = CentralHeaders(zip);
        zip[headers[1] + 8] |= 1; // encrypted flag
        BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(headers[2] + 10), 14); // LZMA
        ZipDirectory directory = Read(zip);

        Assert.Equal(ArchiveEntryProblem.Folder, Assert.Throws<ArchiveEntryException>(() => ReadEntry(zip, directory.Entries[0])).Problem);
        Assert.True(directory.Entries[1].IsEncrypted);
        Assert.Equal(ArchiveEntryProblem.Encrypted, Assert.Throws<ArchiveEntryException>(() => ReadEntry(zip, directory.Entries[1])).Problem);
        Assert.False(directory.Entries[2].IsSupportedMethod);
        Assert.Equal(ArchiveEntryProblem.UnsupportedMethod, Assert.Throws<ArchiveEntryException>(() => ReadEntry(zip, directory.Entries[2])).Problem);
    }

    [Fact]
    public void WrongCrc_OrDataBeyondTheEnd_IsDamaged()
    {
        byte[] zip = Zip(z => Add(z, "a.txt", Encoding.UTF8.GetBytes("checked content")));
        int header = CentralHeaders(zip)[0];
        byte[] badCrc = (byte[])zip.Clone();
        badCrc[header + 16] ^= 0xFF;
        byte[] badSize = (byte[])zip.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badSize.AsSpan(header + 20), 10_000_000); // compressed size

        Assert.Equal(ArchiveEntryProblem.Damaged, Assert.Throws<ArchiveEntryException>(() => ReadEntry(badCrc, Read(badCrc).Entries[0])).Problem);
        Assert.Equal(ArchiveEntryProblem.Damaged, Assert.Throws<ArchiveEntryException>(() => ReadEntry(badSize, Read(badSize).Entries[0])).Problem);
    }

    [Fact]
    public void DeclaredBomb_IsRefusedBeforeDecompressing()
    {
        byte[] zip = Zip(z => Add(z, "bomb.png", new byte[50 * MiB]));
        ZipEntry entry = Read(zip).Entries[0];

        Assert.True(entry.HasSuspiciousRatio(ArchiveLimits.Default));
        var totals = new DecompressionTotals();
        var error = Assert.Throws<ArchiveLimitExceededException>(() => ReadEntry(zip, entry, totals: totals));
        Assert.Equal(nameof(ArchiveLimits.MaxCompressionRatio), error.Limit);
        Assert.Equal(0, totals.Bytes);
    }

    [Fact]
    public void LyingDeclaredSize_IsCaughtByCountingWhatIsDecompressed()
    {
        byte[] zip = Zip(z => Add(z, "liar.png", new byte[20 * MiB]));
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(CentralHeaders(zip)[0] + 24), 1000); // claims 1,000 bytes
        ZipEntry entry = Read(zip).Entries[0];

        Assert.False(entry.HasSuspiciousRatio(ArchiveLimits.Default));
        var error = Assert.Throws<ArchiveLimitExceededException>(() => ReadEntry(zip, entry));
        Assert.Equal(nameof(ArchiveLimits.MaxCompressionRatio), error.Limit);
    }

    [Fact]
    public void EntryAndArchiveTotals_AreEnforced()
    {
        byte[] random = new byte[5000];
        new Random(5).NextBytes(random);
        byte[] zip = Zip(z =>
        {
            Add(z, "a.bin", random, CompressionLevel.NoCompression);
            Add(z, "b.bin", random, CompressionLevel.NoCompression);
        });
        ZipDirectory directory = Read(zip);

        var entryLimit = Assert.Throws<ArchiveLimitExceededException>(() => ReadEntry(zip, directory.Entries[0], new ArchiveLimits { MaxEntryBytes = 4000 }));
        Assert.Equal(nameof(ArchiveLimits.MaxEntryBytes), entryLimit.Limit);

        var limits = new ArchiveLimits { MaxTotalBytes = 8000 };
        var totals = new DecompressionTotals();
        ReadEntry(zip, directory.Entries[0], limits, totals);
        var total = Assert.Throws<ArchiveLimitExceededException>(() => ReadEntry(zip, directory.Entries[1], limits, totals));
        Assert.Equal(nameof(ArchiveLimits.MaxTotalBytes), total.Limit);
    }

    [Fact]
    public void Reading_StopsWhenCancelled()
    {
        byte[] zip = Zip(z => Add(z, "a.txt", new byte[100_000]));
        ZipEntry entry = Read(zip).Entries[0];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ZipEntryReader.ReadAll(new MemoryStream(zip), entry, ArchiveLimits.Default, new DecompressionTotals(), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ZipDirectory.Read(new MemoryStream(zip), cancellation: cancelled.Token));
    }

    [Fact]
    public void Tree_ShowsFoldersFirst_InNaturalOrder_WithImpliedFolders()
    {
        byte[] zip = Zip(z =>
        {
            Add(z, "b/page10.jpg", [1]);
            Add(z, "b/page2.jpg", [1]);
            Add(z, "a.txt", [1]);
            Add(z, "z/deep/x.png", [1]);
            Add(z, "nested.zip", [1]);
            Add(z, "../evil.png", [1]);
        });

        ArchiveTree tree = ArchiveTree.Build(Read(zip).Entries, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["b", "z", "../evil.png", "a.txt", "nested.zip"], tree.Children(string.Empty).Select(n => n.Name));
        Assert.True(tree.Children(string.Empty)[0].IsFolder);
        Assert.Equal(2, tree.Children(string.Empty)[0].ChildCount);
        Assert.True(tree.Children(string.Empty)[2].IsUnsafe);
        Assert.Equal(["page2.jpg", "page10.jpg"], tree.Children("b").Select(n => n.Name));
        Assert.Equal(["deep"], tree.Children("z").Select(n => n.Name));
        Assert.Equal(["b/page2.jpg", "b/page10.jpg", "z/deep/x.png"], tree.Images.Select(e => e.Path)); // the unsafe one is not shown
        Assert.True(tree.HasFolder("z/deep"));
        Assert.Equal("z", ArchiveTree.ParentOf("z/deep"));
        Assert.Equal(string.Empty, ArchiveTree.ParentOf("a.txt"));

        ArchiveSummary summary = tree.Summary;
        Assert.Equal(6, summary.Files);
        Assert.Equal(3, summary.Folders);
        Assert.Equal(3, summary.Images);
        Assert.Equal(1, summary.UnsafeNames);
        Assert.Equal(1, summary.NestedArchives);
    }

    [Fact]
    public void Tree_KeepsEncryptedAndUnreadableImagesOutOfTheSequence()
    {
        byte[] zip = Zip(z =>
        {
            Add(z, "1.jpg", [1]);
            Add(z, "2.jpg", [1]);
            Add(z, "3.svg", [1]); // SVG is drawn from files only
            Add(z, "bomb.png", new byte[20 * MiB]);
        });
        zip[CentralHeaders(zip)[1] + 8] |= 1;

        ArchiveTree tree = ArchiveTree.Build(Read(zip).Entries, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(["1.jpg", "bomb.png"], tree.Images.Select(e => e.Path));
        Assert.Equal(1, tree.Summary.Encrypted);
        Assert.Equal(1, tree.Summary.SuspiciousRatio);
    }

    [Theory]
    [InlineData("photo.JPG", true)]
    [InlineData("a/b.webp", true)]
    [InlineData("raw.nef", true)]
    [InlineData("vector.svg", false)]
    [InlineData("notes.txt", false)]
    [InlineData("noext", false)]
    public void ImageNames_AreWhatWicDecodes(string name, bool image) => Assert.Equal(image, ArchiveTree.IsImageName(name));

    /// <summary>One stored entry with ZIP64 sizes and offset in its extra fields, a ZIP64 end record and locator.</summary>
    private static byte[] Zip64Archive(string name, byte[] content)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        uint crc = Crc32.Compute(content);
        var out_ = new MemoryStream();
        var w = new BinaryWriter(out_);

        // Local header with ZIP64 extra (sizes).
        w.Write(0x04034b50u);
        w.Write((ushort)45);
        w.Write((ushort)0);
        w.Write((ushort)0); // stored
        w.Write((ushort)0);
        w.Write((ushort)0x21);
        w.Write(crc);
        w.Write(uint.MaxValue);
        w.Write(uint.MaxValue);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)20);
        w.Write(nameBytes);
        w.Write((ushort)1);
        w.Write((ushort)16);
        w.Write((ulong)content.Length);
        w.Write((ulong)content.Length);
        w.Write(content);

        // Central directory with ZIP64 extra (sizes and offset).
        long directoryStart = out_.Position;
        w.Write(0x02014b50u);
        w.Write((ushort)45);
        w.Write((ushort)45);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0x21);
        w.Write(crc);
        w.Write(uint.MaxValue);
        w.Write(uint.MaxValue);
        w.Write((ushort)nameBytes.Length);
        w.Write((ushort)28);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(0u);
        w.Write(uint.MaxValue);
        w.Write(nameBytes);
        w.Write((ushort)1);
        w.Write((ushort)24);
        w.Write((ulong)content.Length);
        w.Write((ulong)content.Length);
        w.Write(0ul);
        long directorySize = out_.Position - directoryStart;

        // ZIP64 end record, locator, end record.
        long zip64End = out_.Position;
        w.Write(0x06064b50u);
        w.Write(44ul);
        w.Write((ushort)45);
        w.Write((ushort)45);
        w.Write(0u);
        w.Write(0u);
        w.Write(1ul);
        w.Write(1ul);
        w.Write((ulong)directorySize);
        w.Write((ulong)directoryStart);
        w.Write(0x07064b50u);
        w.Write(0u);
        w.Write((ulong)zip64End);
        w.Write(1u);
        w.Write(0x06054b50u);
        w.Write((ushort)0);
        w.Write((ushort)0);
        w.Write(ushort.MaxValue);
        w.Write(ushort.MaxValue);
        w.Write(uint.MaxValue);
        w.Write(uint.MaxValue);
        w.Write((ushort)0);
        w.Flush();
        return out_.ToArray();
    }
}
