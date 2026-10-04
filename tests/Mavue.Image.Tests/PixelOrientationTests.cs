using Mavue.Core.Viewing;
using Mavue.Image;

namespace Mavue.Image.Tests;

[Trait("Category", "Image")]
public sealed class PixelOrientationTests
{
    // A 3 × 2 image whose pixels are numbered row by row: 1 2 3 / 4 5 6.
    private static readonly uint[] Source = [1, 2, 3, 4, 5, 6];

    private static uint[] Apply(ViewOrientation orientation, int sourceStride = 12)
    {
        byte[] source = new byte[sourceStride * 2];
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                BitConverter.GetBytes(Source[(y * 3) + x]).CopyTo(source, (y * sourceStride) + (x * 4));
            }
        }

        (int w, int h) = orientation.Oriented(3, 2);
        byte[] destination = new byte[w * h * 4];
        PixelOrientation.Apply(source, 3, 2, sourceStride, destination, w * 4, orientation);
        return Enumerable.Range(0, w * h).Select(i => BitConverter.ToUInt32(destination, i * 4)).ToArray();
    }

    [Fact]
    public void Identity_Copies_EvenWithPaddedRows() => Assert.Equal(Source, Apply(ViewOrientation.Identity, sourceStride: 16));

    [Fact]
    public void Clockwise() => Assert.Equal([4u, 1, 5, 2, 6, 3], Apply(new ViewOrientation(1, false)));

    [Fact]
    public void HalfTurn() => Assert.Equal([6u, 5, 4, 3, 2, 1], Apply(new ViewOrientation(2, false)));

    [Fact]
    public void CounterClockwise() => Assert.Equal([3u, 6, 2, 5, 1, 4], Apply(new ViewOrientation(3, false)));

    [Fact]
    public void FlipHorizontal() => Assert.Equal([3u, 2, 1, 6, 5, 4], Apply(ViewOrientation.Identity.FlipHorizontal()));

    [Fact]
    public void FlipVertical() => Assert.Equal([4u, 5, 6, 1, 2, 3], Apply(ViewOrientation.Identity.FlipVertical()));

    [Fact]
    public void TooSmallBuffers_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => PixelOrientation.Apply(new byte[20], 3, 2, 12, new byte[24], 8, new ViewOrientation(1, false)));
        Assert.Throws<ArgumentException>(() => PixelOrientation.Apply(new byte[24], 3, 2, 12, new byte[20], 8, new ViewOrientation(1, false)));
    }
}
