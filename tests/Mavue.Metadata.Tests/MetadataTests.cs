using System.Globalization;
using Mavue.Metadata.Tiff;

namespace Mavue.Metadata.Tests;

/// <summary>EXIF reading, the privacy check and copies without metadata (ARCHITECTURE §4.7.13).</summary>
public sealed class MetadataTests
{
    private static ImageMetadata Read(byte[] file, bool findTrailer = true) => ImageMetadataReader.Read(new MemoryStream(file), findTrailer);

    private static (byte[] Output, StripResult Result) Strip(byte[] file, StripOptions? options = null)
    {
        var output = new MemoryStream();
        StripResult result = MetadataStripper.Strip(new MemoryStream(file), output, options ?? StripOptions.Default);
        return (output.ToArray(), result);
    }

    // ------------------------------------------------------------------ reading

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exif_ValuesAreRead_InBothByteOrders(bool bigEndian)
    {
        ImageMetadata m = Read(TestFiles.Jpeg(TestFiles.RichExif(bigEndian)));
        ExifInfo exif = Assert.IsType<ExifInfo>(m.Exif);
        Assert.Equal(MetadataContainer.Jpeg, m.Container);
        Assert.Equal("Canon EOS R5", exif.Camera); // the maker is not repeated
        Assert.Equal("RF24-70mm F2.8 L IS USM", exif.LensModel);
        Assert.Equal("1/250 s", ExifText.Exposure(exif.ExposureTime));
        Assert.Equal("f/2.8", ExifText.FNumber(exif.FNumber));
        Assert.Equal("ISO 400", ExifText.Iso(exif.Iso));
        Assert.Equal("35 mm (52 mm)", ExifText.FocalLength(exif.FocalLength, exif.FocalLength35mm));
        Assert.Equal(new DateTime(2026, 10, 1, 14, 3, 22), exif.DateTimeOriginal);
        Assert.Equal("+09:00", exif.OffsetTimeOriginal);
        Assert.Equal(6, exif.Orientation);
        Assert.Equal("012345678", exif.BodySerialNumber);
        Assert.Equal("LENS9876", exif.LensSerialNumber);
        Assert.Equal("Taro Yamada", exif.OwnerName);
        Assert.Equal(2000, exif.MakerNoteBytes);
        Assert.Equal(1500, exif.ThumbnailBytes);
        GpsPosition gps = Assert.IsType<GpsPosition>(exif.Gps);
        Assert.Equal(35 + (40 / 60.0) + (52 / 3600.0), gps.Latitude!.Value, 6);
        Assert.Equal(139 + (45 / 60.0) + (36 / 3600.0), gps.Longitude!.Value, 6);
        Assert.Equal(40, gps.Altitude);
    }

    [Fact]
    public void Gps_SouthAndWest_AreNegative()
    {
        byte[] exif = TestFiles.Tiff(false, [TestFiles.Text(0x010F, "X")], gps:
        [
            TestFiles.Text(1, "S"), TestFiles.Rationals(2, false, (33, 1), (52, 1), (0, 1)),
            TestFiles.Text(3, "W"), TestFiles.Rationals(4, false, (70, 1), (30, 1), (0, 1)),
        ]);
        GpsPosition gps = Read(TestFiles.Jpeg(exif)).Exif!.Gps!;
        Assert.True(gps.Latitude < 0 && gps.Longitude < 0);
    }

    [Fact]
    public void OtherBlocks_AreFound()
    {
        ImageMetadata m = Read(TestFiles.Jpeg(TestFiles.RichExif(), trailer: new byte[5000]));
        Assert.True(m.Has(MetadataBlockKind.Xmp));
        Assert.True(m.Has(MetadataBlockKind.Iptc));
        Assert.True(m.Has(MetadataBlockKind.IccProfile));
        Assert.True(m.Has(MetadataBlockKind.Comment));
        Assert.Contains(m.Blocks, b => b.Kind == MetadataBlockKind.EmbeddedImage && b.Label == "MPF");
        Assert.Equal(5000, Assert.Single(m.Blocks, b => b.Kind == MetadataBlockKind.Trailer).Length);
        Assert.Equal("Adobe Photoshop 27.0", m.Xmp!.CreatorTool);
        Assert.True(m.Xmp.HasHistory && m.Xmp.HasLocation && m.Xmp.HasCreator);
        Assert.True(m.Iptc!.HasCreator && m.Iptc.HasLocation && m.Iptc.HasCaption);
        Assert.Equal("secret note", Assert.Single(m.Comments));
    }

    [Fact]
    public void Trailer_IsLookedForOnlyWhenAsked()
    {
        ImageMetadata quick = Read(TestFiles.Jpeg(null, trailer: new byte[100]), findTrailer: false);
        Assert.False(quick.Has(MetadataBlockKind.Trailer));
    }

    [Fact]
    public void Png_WebP_Gif_AreRead()
    {
        ImageMetadata png = Read(TestFiles.Png(TestFiles.RichExif(), trailer: true));
        Assert.Equal(MetadataContainer.Png, png.Container);
        Assert.Equal("Canon EOS R5", png.Exif!.Camera);
        Assert.Contains("Author", png.TextKeys);
        Assert.True(png.Has(MetadataBlockKind.Xmp) && png.Has(MetadataBlockKind.Time) && png.Has(MetadataBlockKind.Trailer));
        Assert.Contains(png.Blocks, b => b.Kind == MetadataBlockKind.Other && b.Label == "zzZz");

        ImageMetadata webp = Read(TestFiles.WebP(TestFiles.RichExif()));
        Assert.Equal(MetadataContainer.WebP, webp.Container);
        Assert.Equal(400, webp.Exif!.Iso);
        Assert.True(webp.Has(MetadataBlockKind.Xmp) && webp.Has(MetadataBlockKind.IccProfile));

        ImageMetadata gif = Read(TestFiles.Gif());
        Assert.Equal(MetadataContainer.Gif, gif.Container);
        Assert.Equal("secret", Assert.Single(gif.Comments));
        Assert.True(gif.Has(MetadataBlockKind.Xmp) && gif.Has(MetadataBlockKind.Trailer));
    }

    [Fact]
    public void TiffBasedFiles_AreRead()
    {
        ImageMetadata tiff = Read(TestFiles.RichExif());
        Assert.Equal(MetadataContainer.Tiff, tiff.Container);
        Assert.Equal("Canon EOS R5", tiff.Exif!.Camera);
        Assert.False(MetadataStripper.CanStrip(tiff.Container));
    }

    [Fact]
    public void OtherFormats_AreUnsupported()
    {
        // HEIF is read since the Phase 5 follow-up (HeifAndExportTests); an MP4 video is still not an image container.
        Assert.Equal(MetadataContainer.Unsupported, Read([0, 0, 0, 0x18, .. "ftypisom"u8, .. new byte[40]]).Container);
        Assert.Throws<NotSupportedException>(() => Strip(TestFiles.RichExif()));
    }

    // ------------------------------------------------------------------ hostile input

    [Fact]
    public void TiffLoops_HugeCounts_AndBadOffsets_AreHarmless()
    {
        // IFD0 whose "next" points back to itself; an entry with a huge count pointing outside.
        byte[] tiff = [.. "II*\0"u8, 8, 0, 0, 0, 1, 0, 0x0F, 0x01, 2, 0, 0xFF, 0xFF, 0xFF, 0x0F, 0xFF, 0xFF, 0, 0, 8, 0, 0, 0];
        TiffStructure? s = TiffReader.Read(new MemoryStream(tiff), 0, tiff.Length);
        Assert.NotNull(s);
        Assert.Null(s.Image.Ascii(0x010F)); // too large / outside: not read
        Assert.Null(s.Thumbnail); // the loop is detected
    }

    [Fact]
    public void RandomBytes_NeverCrashTheReaderOrStripper()
    {
        var random = new Random(5);
        byte[][] seeds = [TestFiles.Jpeg(TestFiles.RichExif()), TestFiles.Png(TestFiles.RichExif()), TestFiles.WebP(TestFiles.RichExif()), TestFiles.Gif()];
        for (int round = 0; round < 400; round++)
        {
            byte[] file = (byte[])seeds[round % seeds.Length].Clone();
            int changes = random.Next(1, 12);
            for (int c = 0; c < changes; c++)
            {
                file[random.Next(8, file.Length)] = (byte)random.Next(256);
            }

            if (random.Next(4) == 0)
            {
                Array.Resize(ref file, random.Next(10, file.Length));
            }

            _ = Read(file);
            try
            {
                _ = Strip(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
            {
                // Damaged input is refused; anything else would be a bug.
            }
        }
    }

    // ------------------------------------------------------------------ privacy check

    [Fact]
    public void Review_FindsEverythingPrivate()
    {
        IReadOnlyList<PrivacyFinding> findings = PrivacyCheck.Review(Read(TestFiles.Jpeg(TestFiles.RichExif(), trailer: new byte[10])));
        PrivacyKind[] kinds = [.. findings.Select(f => f.Kind)];
        Assert.Contains(PrivacyKind.Location, kinds);
        Assert.Contains(PrivacyKind.CaptureTime, kinds);
        Assert.Contains(PrivacyKind.CameraDetails, kinds);
        Assert.Contains(PrivacyKind.DeviceIdentity, kinds);
        Assert.Contains(PrivacyKind.OwnerName, kinds);
        Assert.Contains(PrivacyKind.AuthorCopyright, kinds);
        Assert.Contains(PrivacyKind.EditingSoftware, kinds);
        Assert.Contains(PrivacyKind.Descriptions, kinds);
        Assert.Contains(PrivacyKind.EmbeddedImages, kinds);
        Assert.Contains(PrivacyKind.MakerNote, kinds);
        Assert.Equal(PrivacyLevel.High, findings.Single(f => f.Kind == PrivacyKind.Location).Level);
        Assert.Equal(PrivacyLevel.Kept, findings.Single(f => f.Kind == PrivacyKind.ColorProfile).Level);
        Assert.StartsWith("35.68", findings.Single(f => f.Kind == PrivacyKind.Location).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Review_OfACleanFile_IsEmpty()
    {
        Assert.Empty(PrivacyCheck.Review(Read(TestFiles.Jpeg(null, xmp: false, iptc: false, icc: false, mpf: false, comment: false))));
    }

    // ------------------------------------------------------------------ copies without metadata

    [Fact]
    public void Jpeg_StripsEverything_KeepsImageDataProfileAndOrientation()
    {
        byte[] original = TestFiles.Jpeg(TestFiles.RichExif(orientation: 6), trailer: new byte[3000]);
        (byte[] clean, StripResult result) = Strip(original);
        ImageMetadata after = Read(clean);
        Assert.Empty(PrivacyCheck.Leftovers(after, StripOptions.Default));
        Assert.True(result.WroteExif);
        Assert.Equal(6, after.Exif!.Orientation); // still upright
        Assert.Null(after.Exif.Camera);
        Assert.Null(after.Exif.Gps);
        Assert.Equal(0, after.Exif.ThumbnailBytes);
        Assert.True(after.Has(MetadataBlockKind.IccProfile));
        Assert.DoesNotContain(after.Blocks, b => b.Kind is MetadataBlockKind.Xmp or MetadataBlockKind.Iptc or MetadataBlockKind.Comment or MetadataBlockKind.EmbeddedImage or MetadataBlockKind.Trailer);
        Assert.True(result.RemovedBytes > 3000);

        // The compressed image (from SOS to EOI) is byte for byte the same; the file still ends with EOI.
        int sos = IndexOf(original, [0xFF, 0xDA]);
        int sosClean = IndexOf(clean, [0xFF, 0xDA]);
        int eoi = IndexOf(original, [0xFF, 0xD9]);
        Assert.Equal(original.AsSpan(sos, eoi + 2 - sos).ToArray(), clean.AsSpan(sosClean).ToArray());
        Assert.True(clean.AsSpan(0, sosClean).IndexOf("Adobe"u8) >= 0); // APP14 Adobe (color transform) kept
    }

    [Fact]
    public void Jpeg_KeepsWhatTheUserChose()
    {
        byte[] original = TestFiles.Jpeg(TestFiles.RichExif(big: true));
        var keep = new StripOptions(RemoveLocation: false, RemoveCaptureTime: false, RemoveCameraDetails: false, RemoveAuthorCopyright: false, KeepColorProfile: false);
        (byte[] clean, _) = Strip(original, keep);
        ImageMetadata after = Read(clean);
        ExifInfo exif = after.Exif!;
        Assert.Equal("Canon EOS R5", exif.Camera);
        Assert.Equal(400, exif.Iso);
        Assert.NotNull(exif.Gps);
        Assert.Equal(new DateTime(2026, 10, 1, 14, 3, 22), exif.DateTimeOriginal);
        Assert.Equal("Taro Yamada", exif.Artist);
        Assert.Null(exif.BodySerialNumber); // serial numbers, owner and maker notes always go
        Assert.Null(exif.OwnerName);
        Assert.Equal(0, exif.MakerNoteBytes);
        Assert.Null(exif.Software);
        Assert.False(after.Has(MetadataBlockKind.IccProfile));
        Assert.True(after.ExifStructure!.BigEndian);
        Assert.Empty(PrivacyCheck.Leftovers(after, keep));
    }

    [Fact]
    public void Jpeg_WithoutJfif_GetsExifRightAfterStartOfImage()
    {
        (byte[] clean, _) = Strip(TestFiles.Jpeg(TestFiles.RichExif(), jfif: false));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE1], clean[..4]);
    }

    [Fact]
    public void Jpeg_UprightWithoutKeptValues_HasNoExifAtAll()
    {
        (byte[] clean, StripResult result) = Strip(TestFiles.Jpeg(TestFiles.RichExif(orientation: 1)));
        Assert.False(result.WroteExif);
        Assert.False(Read(clean).Has(MetadataBlockKind.Exif));
    }

    [Fact]
    public void Png_StripsChunks_AndWritesAValidExifChunk()
    {
        (byte[] clean, StripResult result) = Strip(TestFiles.Png(TestFiles.RichExif(orientation: 8), trailer: true));
        ImageMetadata after = Read(clean);
        Assert.Empty(PrivacyCheck.Leftovers(after, StripOptions.Default));
        Assert.True(result.WroteExif);
        Assert.Equal(8, after.Exif!.Orientation);
        Assert.Empty(after.TextKeys);
        Assert.DoesNotContain(after.Blocks, b => b.Kind is MetadataBlockKind.Xmp or MetadataBlockKind.Time or MetadataBlockKind.Other or MetadataBlockKind.Trailer);
        Assert.True(after.Has(MetadataBlockKind.IccProfile));
        Assert.True(PngCrcsValid(clean));
        Assert.True(IndexOf(clean, "eXIf"u8.ToArray()) < IndexOf(clean, "IDAT"u8.ToArray()));
    }

    [Fact]
    public void WebP_StripsChunks_AndFixesFlagsAndSizes()
    {
        (byte[] clean, _) = Strip(TestFiles.WebP(TestFiles.RichExif(orientation: 3)));
        ImageMetadata after = Read(clean);
        Assert.Empty(PrivacyCheck.Leftovers(after, StripOptions.Default));
        Assert.Equal(3, after.Exif!.Orientation);
        Assert.False(after.Has(MetadataBlockKind.Xmp));
        Assert.DoesNotContain(after.Blocks, b => b.Kind == MetadataBlockKind.Other);
        Assert.Equal(clean.Length - 8, BitConverter.ToInt32(clean, 4)); // RIFF size
        byte flags = clean[20];
        Assert.Equal(0x20 | 0x08, flags & (0x20 | 0x08 | 0x04)); // ICC and EXIF, no XMP
    }

    [Fact]
    public void Gif_KeepsFramesAndLoop_DropsCommentsXmpAndJunk()
    {
        byte[] original = TestFiles.Gif();
        (byte[] clean, _) = Strip(original);
        ImageMetadata after = Read(clean);
        Assert.Empty(after.Comments);
        Assert.DoesNotContain(after.Blocks, b => b.Kind is MetadataBlockKind.Xmp or MetadataBlockKind.Trailer);
        Assert.True(IndexOf(clean, "NETSCAPE2.0"u8.ToArray()) > 0);
        Assert.Equal(0x3B, clean[^1]);
        Assert.Equal(2, CountOf(clean, [0x2C, 0, 0, 0, 0, 2, 0, 2, 0, 0]));
    }

    [Fact]
    public void ExifWriter_CopiesValuesByteForByte()
    {
        TiffStructure source = TiffReader.Read(new MemoryStream(TestFiles.RichExif()), 0, TestFiles.RichExif().Length)!;
        byte[] written = ExifWriter.Write(source, new StripOptions(RemoveCameraDetails: false))!;
        TiffStructure copy = TiffReader.Read(new MemoryStream(written), 0, written.Length)!;
        Assert.Equal(source.Exif![0x829A]!.Value, copy.Exif![0x829A]!.Value);
        Assert.Null(copy.Exif[0x927C]); // no maker note
        Assert.Null(copy.Gps);
        Assert.Null(copy.Thumbnail);
    }

    [Fact]
    public void ExifText_FormatsSettings()
    {
        Assert.Equal("1/4000 s", ExifText.Exposure(new RationalValue(1, 4000)));
        Assert.Equal("2 s", ExifText.Exposure(new RationalValue(2, 1)));
        Assert.Equal("0.3 s", ExifText.Exposure(new RationalValue(3, 10)));
        Assert.Null(ExifText.Exposure(new RationalValue(0, 1)));
        Assert.Equal("+0.3 EV", ExifText.ExposureBias(0.33));
        Assert.Equal("−1 EV", ExifText.ExposureBias(-1));
        Assert.Null(ExifText.ExposureBias(0));
        Assert.Equal("50 mm", ExifText.FocalLength(50, 50));
        Assert.True(ExifText.FlashFired(0x19));
        Assert.Contains("2026", ExifText.Taken(new ExifInfo { DateTimeOriginal = new DateTime(2026, 1, 2, 3, 4, 5) }, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    private static int CountOf(byte[] data, byte[] pattern)
    {
        int count = 0;
        for (int at = data.AsSpan().IndexOf(pattern); at >= 0; at = data.AsSpan(at + 1).IndexOf(pattern) is var next and >= 0 ? at + 1 + next : -1)
        {
            count++;
        }

        return count;
    }

    private static bool PngCrcsValid(byte[] png)
    {
        int at = 8;
        while (at + 12 <= png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            uint crc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length));
            if (Core.Archives.Crc32.Compute(png.AsSpan(at + 4, 4 + length)) != crc)
            {
                return false;
            }

            at += 12 + length;
        }

        return at == png.Length;
    }
}
