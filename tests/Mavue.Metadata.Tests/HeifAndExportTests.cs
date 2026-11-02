using System.Buffers.Binary;
using System.Text;

namespace Mavue.Metadata.Tests;

/// <summary>HEIF/AVIF metadata items and EXIF written from another source (ARCHITECTURE §4.7.15).</summary>
public sealed class HeifAndExportTests
{
    /// <summary>A minimal HEIC: ftyp, meta (hdlr, iinf with an Exif and an XMP item, iloc), mdat with both payloads.</summary>
    private static byte[] Heic(byte[] exifTiff, string brand = "heic", bool ilocVersion1 = false)
    {
        byte[] exifItem = [0, 0, 0, 6, .. "Exif\0\0"u8, .. exifTiff];
        byte[] xmpItem = Encoding.UTF8.GetBytes(TestFiles.Xmp);
        byte[] ftyp = Box("ftyp", [.. Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0, .. "mif1heic"u8]);
        byte[] hdlr = FullBox("hdlr", 0, [0, 0, 0, 0, .. "pict"u8, .. new byte[12], 0]);
        byte[] infe1 = FullBox("infe", 2, [0, 1, 0, 0, .. "Exif"u8, 0]);
        byte[] infe2 = FullBox("infe", 2, [0, 2, 0, 0, .. "mime"u8, 0, .. "application/rdf+xml"u8, 0]);
        byte[] iinf = FullBox("iinf", 0, [0, 2, .. infe1, .. infe2]);

        // iloc: offset_size 4, length_size 4, base_offset_size 0; offsets filled once the layout is known.
        byte[] ilocBody(int exifAt, int xmpAt)
        {
            var b = new List<byte> { 0x44, 0x00, 0, 2 };
            foreach ((int id, int at, int length) in new[] { (1, exifAt, exifItem.Length), (2, xmpAt, xmpItem.Length) })
            {
                b.AddRange([(byte)(id >> 8), (byte)id]);
                if (ilocVersion1)
                {
                    b.AddRange([0, 0]); // construction method 0
                }

                b.AddRange([0, 0, 0, 1]); // data reference index, extent count
                b.AddRange(BigEndian(at));
                b.AddRange(BigEndian(length));
            }

            return [.. b];
        }

        int ilocSize = FullBox("iloc", (byte)(ilocVersion1 ? 1 : 0), ilocBody(0, 0)).Length;
        int metaSize = 12 + hdlr.Length + iinf.Length + ilocSize;
        int mdatStart = ftyp.Length + metaSize + 8;
        byte[] iloc = FullBox("iloc", (byte)(ilocVersion1 ? 1 : 0), ilocBody(mdatStart, mdatStart + exifItem.Length));
        byte[] meta = FullBox("meta", 0, [.. hdlr, .. iinf, .. iloc]);
        byte[] mdat = Box("mdat", [.. exifItem, .. xmpItem]);
        return [.. ftyp, .. meta, .. mdat];
    }

    private static byte[] Box(string type, byte[] body) => [.. BigEndian(body.Length + 8), .. Encoding.ASCII.GetBytes(type), .. body];

    private static byte[] FullBox(string type, byte version, byte[] body) => Box(type, [version, 0, 0, 0, .. body]);

    private static byte[] BigEndian(int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        return b;
    }

    [Theory]
    [InlineData("heic", false)]
    [InlineData("avif", true)]
    public void HeifExifAndXmp_AreRead(string brand, bool ilocVersion1)
    {
        ImageMetadata m = ImageMetadataReader.Read(new MemoryStream(Heic(TestFiles.RichExif(), brand, ilocVersion1)));
        Assert.Equal(MetadataContainer.Heif, m.Container);
        Assert.Equal("Canon EOS R5", m.Exif!.Camera);
        Assert.NotNull(m.Exif.Gps);
        Assert.Equal("Adobe Photoshop 27.0", m.Xmp!.CreatorTool);
        Assert.False(MetadataStripper.CanStrip(m.Container)); // a clean copy is converted
        Assert.Contains(PrivacyCheck.Review(m), f => f.Kind == PrivacyKind.Location);
    }

    [Fact]
    public void BrokenHeif_IsHarmless()
    {
        byte[] heic = Heic(TestFiles.RichExif());
        var random = new Random(9);
        for (int round = 0; round < 300; round++)
        {
            byte[] file = (byte[])heic.Clone();
            for (int c = 0; c < random.Next(1, 8); c++)
            {
                file[random.Next(16, file.Length)] = (byte)random.Next(256);
            }

            _ = ImageMetadataReader.Read(new MemoryStream(file));
        }

        // A video (ftyp isom) and a Canon CR3 (crx) are not read as HEIF images.
        Assert.NotEqual(MetadataContainer.Heif, ImageMetadataReader.Read(new MemoryStream([0, 0, 0, 16, .. "ftypisom"u8, 0, 0, 0, 0])).Container);
        Assert.NotEqual(MetadataContainer.Heif, ImageMetadataReader.Read(new MemoryStream([0, 0, 0, 16, .. "ftypcrx "u8, 0, 0, 0, 0])).Container);
    }

    [Fact]
    public void ExifFromAnotherSource_KeepsChosenValues_WithoutOrientation()
    {
        // A converted image (encoded without metadata) gets the original's kept values.
        Tiff.TiffStructure source = ImageMetadataReader.Read(new MemoryStream(TestFiles.Jpeg(TestFiles.RichExif(orientation: 6)))).ExifStructure!;
        byte[] encoded = TestFiles.Jpeg(null, xmp: false, iptc: false, icc: false, mpf: false, comment: false);
        var output = new MemoryStream();
        var keep = new StripOptions(RemoveCameraDetails: false, KeepOrientation: false);
        StripResult result = MetadataStripper.Strip(new MemoryStream(encoded), output, keep, source);
        ImageMetadata after = ImageMetadataReader.Read(new MemoryStream(output.ToArray()));
        Assert.True(result.WroteExif);
        Assert.Equal("Canon EOS R5", after.Exif!.Camera);
        Assert.Null(after.Exif.Orientation); // the pixels are already upright
        Assert.Null(after.Exif.Gps);
        Assert.Empty(PrivacyCheck.Leftovers(after, keep));
    }
}
