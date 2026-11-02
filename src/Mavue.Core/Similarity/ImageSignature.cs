using System.Numerics;

namespace Mavue.Core.Similarity;

/// <summary>
/// The coarse signature of an image (stage 1 of the similar-image search, ARCHITECTURE §4.7.14): computed from a
/// decode of at most 32×32 pixels. 64-bit difference hash (gradients of a 9×8 grayscale), 64-bit average hash
/// (8×8 against the mean), the mean color and the aspect ratio. 24 bytes; cached per file.
/// </summary>
public readonly record struct CoarseSignature(ulong DifferenceHash, ulong AverageHash, uint MeanColor, float Aspect);

/// <summary>
/// The fine signature (stage 2, only for candidates that passed stage 1): computed from a decode of at most 128×128
/// pixels. 64-bit perceptual hash (DCT of a 32×32 grayscale) and a 4×4 color layout in YCbCr (48 bytes).
/// </summary>
public readonly record struct FineSignature(ulong PerceptualHash, byte[] ColorLayout);

/// <summary>
/// Image signatures and their distances (pure; unit-tested). Inputs are BGRA pixels (premultiplied or straight —
/// only opaque structure matters) of any size; they are resampled by area averaging, which ignores the aspect ratio
/// so a resized copy has the same signature.
/// </summary>
public static class ImageSignatures
{
    /// <summary>Stage 1 passes images whose difference hash differs in at most this many of 64 bits…</summary>
    public const int CoarseDifferenceLimit = 22;

    /// <summary>…or whose average hash differs in at most this many bits.</summary>
    public const int CoarseAverageLimit = 12;

    public static CoarseSignature Coarse(ReadOnlySpan<byte> bgra, int width, int height, double aspect)
    {
        Validate(bgra, width, height);
        Span<float> gray9x8 = stackalloc float[72];
        Resample(bgra, width, height, gray9x8, 9, 8, gray: true);
        ulong difference = 0;
        int bit = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++, bit++)
            {
                if (gray9x8[(y * 9) + x] < gray9x8[(y * 9) + x + 1])
                {
                    difference |= 1UL << bit;
                }
            }
        }

        Span<float> gray8x8 = stackalloc float[64];
        Resample(bgra, width, height, gray8x8, 8, 8, gray: true);
        float mean = 0;
        foreach (float v in gray8x8)
        {
            mean += v;
        }

        mean /= 64;
        ulong average = 0;
        for (int i = 0; i < 64; i++)
        {
            if (gray8x8[i] > mean)
            {
                average |= 1UL << i;
            }
        }

        return new CoarseSignature(difference, average, MeanColor(bgra, width, height), (float)Math.Clamp(aspect, 0.01, 100));
    }

    public static FineSignature Fine(ReadOnlySpan<byte> bgra, int width, int height)
    {
        Validate(bgra, width, height);
        const int N = 32;
        float[] gray = new float[N * N];
        Resample(bgra, width, height, gray, N, N, gray: true);

        // 2-D DCT-II of the 32×32 grayscale; only the 8×8 lowest frequencies are needed.
        float[] dct = new float[64];
        Span<float> rowTemp = stackalloc float[N * 8];
        for (int y = 0; y < N; y++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int x = 0; x < N; x++)
                {
                    sum += gray[(y * N) + x] * Cos[(u * N) + x];
                }

                rowTemp[(y * 8) + u] = sum;
            }
        }

        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                float sum = 0;
                for (int y = 0; y < N; y++)
                {
                    sum += rowTemp[(y * 8) + u] * Cos[(v * N) + y];
                }

                dct[(v * 8) + u] = sum;
            }
        }

        // Compare each coefficient (except DC) with the median of the 63 others.
        float[] ac = dct[1..];
        Array.Sort(ac);
        float median = ac[31];
        ulong hash = 0;
        for (int i = 1; i < 64; i++)
        {
            if (dct[i] > median)
            {
                hash |= 1UL << i;
            }
        }

        // 4×4 color layout in YCbCr (perceptually separates brightness from color).
        float[] rgb = new float[4 * 4 * 3];
        Resample(bgra, width, height, rgb, 4, 4, gray: false);
        byte[] layout = new byte[48];
        for (int i = 0; i < 16; i++)
        {
            float r = rgb[(i * 3) + 2], g = rgb[(i * 3) + 1], b = rgb[i * 3];
            float luma = (0.299f * r) + (0.587f * g) + (0.114f * b);
            layout[i * 3] = Byte(luma);
            layout[(i * 3) + 1] = Byte(128 + (-0.1687f * r) - (0.3313f * g) + (0.5f * b));
            layout[(i * 3) + 2] = Byte(128 + (0.5f * r) - (0.4187f * g) - (0.0813f * b));
        }

        return new FineSignature(hash, layout);
    }

    /// <summary>Stage 1: worth a closer look.</summary>
    public static bool MayBeSimilar(CoarseSignature a, CoarseSignature b) =>
        BitOperations.PopCount(a.DifferenceHash ^ b.DifferenceHash) <= CoarseDifferenceLimit ||
        BitOperations.PopCount(a.AverageHash ^ b.AverageHash) <= CoarseAverageLimit;

    /// <summary>
    /// Stage 2: similarity in percent (100 = the same picture, also when resized or recompressed). Structure comes from
    /// both the perceptual (DCT) and the difference hash: on smooth pictures (sky, plain backgrounds) the DCT
    /// coefficients sit near their median and flip easily, so one unstable hash must not sink an obvious match, while
    /// unrelated pictures disagree in both.
    /// </summary>
    public static int Similarity(CoarseSignature coarseA, FineSignature fineA, CoarseSignature coarseB, FineSignature fineB)
    {
        double perceptual = Math.Min(1, BitOperations.PopCount(fineA.PerceptualHash ^ fineB.PerceptualHash) / 26.0);
        double difference = Math.Min(1, BitOperations.PopCount(coarseA.DifferenceHash ^ coarseB.DifferenceHash) / 26.0);
        double average = Math.Min(1, BitOperations.PopCount(coarseA.AverageHash ^ coarseB.AverageHash) / 26.0);
        double structure = (0.5 * Math.Min(perceptual, difference)) + (0.25 * (perceptual + difference));
        double color = 0;
        int n = Math.Min(fineA.ColorLayout.Length, fineB.ColorLayout.Length);
        for (int i = 0; i < n; i++)
        {
            color += Math.Abs(fineA.ColorLayout[i] - fineB.ColorLayout[i]);
        }

        color = n == 0 ? 0 : Math.Min(1, color / n / 48.0);
        double aspect = Math.Min(1, Math.Abs(Math.Log(Math.Max(0.01f, coarseA.Aspect) / Math.Max(0.01f, coarseB.Aspect))) / 0.7);
        double distance = (0.62 * structure) + (0.1 * average) + (0.2 * color) + (0.08 * aspect);
        return (int)Math.Round(100 * (1 - Math.Clamp(distance, 0, 1)));
    }

    private static readonly float[] Cos = BuildCos();

    private static float[] BuildCos()
    {
        const int N = 32;
        float[] table = new float[8 * N];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < N; x++)
            {
                table[(u * N) + x] = (float)Math.Cos(((2 * x) + 1) * u * Math.PI / (2 * N));
            }
        }

        return table;
    }

    private static void Validate(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
        {
            throw new ArgumentException("The pixels do not match the size.", nameof(bgra));
        }
    }

    /// <summary>Area-average resampling to <paramref name="outWidth"/>×<paramref name="outHeight"/> (gray: one value per cell; otherwise B, G, R).</summary>
    private static void Resample(ReadOnlySpan<byte> bgra, int width, int height, Span<float> output, int outWidth, int outHeight, bool gray)
    {
        int channels = gray ? 1 : 3;
        output.Clear();
        Span<float> counts = stackalloc float[outWidth * outHeight];
        counts.Clear();
        for (int y = 0; y < height; y++)
        {
            int oy = Math.Min(outHeight - 1, y * outHeight / height);
            for (int x = 0; x < width; x++)
            {
                int ox = Math.Min(outWidth - 1, x * outWidth / width);
                int p = ((y * width) + x) * 4;
                int cell = (oy * outWidth) + ox;
                if (gray)
                {
                    output[cell] += (0.114f * bgra[p]) + (0.587f * bgra[p + 1]) + (0.299f * bgra[p + 2]);
                }
                else
                {
                    output[(cell * 3) + 0] += bgra[p];
                    output[(cell * 3) + 1] += bgra[p + 1];
                    output[(cell * 3) + 2] += bgra[p + 2];
                }

                counts[cell]++;
            }
        }

        // Upsampling small images: cells no pixel fell into take the nearest pixel.
        for (int oy = 0; oy < outHeight; oy++)
        {
            for (int ox = 0; ox < outWidth; ox++)
            {
                int cell = (oy * outWidth) + ox;
                if (counts[cell] > 0)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        output[(cell * channels) + c] /= counts[cell];
                    }

                    continue;
                }

                int sx = Math.Min(width - 1, ox * width / outWidth);
                int sy = Math.Min(height - 1, oy * height / outHeight);
                int p = ((sy * width) + sx) * 4;
                if (gray)
                {
                    output[cell] = (0.114f * bgra[p]) + (0.587f * bgra[p + 1]) + (0.299f * bgra[p + 2]);
                }
                else
                {
                    output[cell * 3] = bgra[p];
                    output[(cell * 3) + 1] = bgra[p + 1];
                    output[(cell * 3) + 2] = bgra[p + 2];
                }
            }
        }
    }

    private static uint MeanColor(ReadOnlySpan<byte> bgra, int width, int height)
    {
        long b = 0, g = 0, r = 0;
        int pixels = width * height;
        for (int i = 0; i < pixels; i++)
        {
            b += bgra[i * 4];
            g += bgra[(i * 4) + 1];
            r += bgra[(i * 4) + 2];
        }

        return (uint)((r / pixels) << 16 | (g / pixels) << 8 | (b / pixels));
    }

    private static byte Byte(float value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
