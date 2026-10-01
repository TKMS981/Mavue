using System.Text;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.QuickView.Harness;

/// <summary>A test file and the case name used in reports.</summary>
internal sealed record TestAsset(string Case, string Path, string Description);

/// <summary>
/// Generates synthetic test files (gradient + noise, so JPEG/PNG compression behaves like photos).
/// Files are cached between runs. Nothing here comes from third-party content.
/// </summary>
internal static class TestAssets
{
    // Unicode folder name on purpose: Quick View must handle Japanese paths.
    public const string FolderName = "Mavue クイックビュー テスト";

    public static async Task<List<TestAsset>> PrepareAsync(string root, bool includeHuge, Action<string> log)
    {
        string dir = Path.Combine(root, FolderName);
        Directory.CreateDirectory(dir);
        var assets = new List<TestAsset>();

        async Task Image(string caseName, string file, Guid encoder, uint w, uint h, string description, double? quality = null)
        {
            string path = Path.Combine(dir, file);
            if (!File.Exists(path))
            {
                log($"generating {file} ({w}x{h}) ...");
                try
                {
                    await EncodeAsync(path, encoder, w, h, quality);
                }
                catch (Exception ex)
                {
                    log($"  could not generate {file}: {ex.GetType().Name} 0x{ex.HResult:X8}");
                    File.Delete(path);
                    return;
                }
            }

            assets.Add(new TestAsset(caseName, path, description));
        }

        await Image("small-jpeg", "01 small.jpg", BitmapEncoder.JpegEncoderId, 800, 600, "JPEG 800x600", 0.9);
        await Image("large-jpeg", "02 large 24MP.jpg", BitmapEncoder.JpegEncoderId, 6000, 4000, "JPEG 6000x4000 (24 MP)", 0.9);
        await Image("png", "03 image.png", BitmapEncoder.PngEncoderId, 4000, 3000, "PNG 4000x3000 (12 MP)");
        Copy("webp", "sample.webp", "04 sample.webp", "WebP 1600x1200 (Pillow-generated, committed)");
        Copy("avif", "sample.avif", "05 sample.avif", "AVIF 1600x1200 (Pillow-generated, committed)");
        await Image("heif", "06 image.heic", BitmapEncoder.HeifEncoderId, 4000, 3000, "HEIF/HEVC 4000x3000 (WIC HEIF encoder)", 0.9);
        string pdf = Path.Combine(dir, "07 document.pdf");
        if (!File.Exists(pdf))
        {
            File.WriteAllBytes(pdf, MinimalPdf());
        }

        assets.Add(new TestAsset("pdf", pdf, "PDF 1 page A4 (hand-written minimal PDF)"));
        if (includeHuge)
        {
            await Image("huge-jpeg", "08 huge 192MP.jpg", BitmapEncoder.JpegEncoderId, 16000, 12000, "JPEG 16000x12000 (192 MP)", 0.85);
            await Image("huge-png", "09 huge 100MP.png", BitmapEncoder.PngEncoderId, 10000, 10000, "PNG 10000x10000 (100 MP)");
        }

        return assets;

        void Copy(string caseName, string source, string file, string description)
        {
            string from = Path.Combine(AppContext.BaseDirectory, "assets", source);
            string path = Path.Combine(dir, file);
            if (!File.Exists(from))
            {
                log($"missing committed asset {source}; case {caseName} skipped");
                return;
            }

            File.Copy(from, path, overwrite: true);
            assets.Add(new TestAsset(caseName, path, description));
        }
    }

    private static async Task EncodeAsync(string path, Guid encoderId, uint width, uint height, double? quality)
    {
        byte[] pixels = Pattern(width, height);
        using IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite, StorageOpenOptions.None, FileOpenDisposition.CreateAlways);
        BitmapEncoder encoder;
        if (quality is { } q)
        {
            var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(q, Windows.Foundation.PropertyType.Single) };
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream, options);
        }
        else
        {
            encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
        }

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    /// <summary>BGRA: R = x gradient, G = y gradient, B = deterministic noise.</summary>
    private static byte[] Pattern(uint width, uint height)
    {
        byte[] pixels = new byte[checked((long)width * height * 4)];
        Parallel.For(0, (int)height, y =>
        {
            long row = (long)y * width * 4;
            byte g = (byte)(y * 255L / Math.Max(1, height - 1));
            uint seed = (uint)y * 2654435761u;
            for (uint x = 0; x < width; x++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                long i = row + (x * 4);
                pixels[i] = (byte)(96 + (seed & 63));
                pixels[i + 1] = g;
                pixels[i + 2] = (byte)(x * 255L / Math.Max(1, width - 1));
                pixels[i + 3] = 255;
            }
        });
        return pixels;
    }

    /// <summary>A valid single-page A4 PDF with a large colored rectangle and ASCII text.</summary>
    private static byte[] MinimalPdf()
    {
        string content = "0.20 0.50 0.80 rg 40 40 515 762 re f\n1 1 1 rg BT /F1 28 Tf 70 760 Td (Mavue Quick View PDF test) Tj ET\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
