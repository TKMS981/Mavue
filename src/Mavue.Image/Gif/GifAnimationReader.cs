using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Mavue.Image.Gif;

/// <summary>
/// Reads an animated GIF one frame at a time through Windows' GIF decoder (WIC via WinRT): logical screen size,
/// loop count and, per frame, its pixels, rectangle, delay and disposal. Frames are decoded on demand, so only
/// the frame being prepared is in memory (never the whole animation).
/// </summary>
public sealed class GifAnimationReader : IDisposable
{
    private readonly IRandomAccessStream _stream;
    private readonly BitmapDecoder _decoder;

    private GifAnimationReader(IRandomAccessStream stream, BitmapDecoder decoder, int width, int height, int? loopCount)
    {
        _stream = stream;
        _decoder = decoder;
        Width = width;
        Height = height;
        LoopCount = loopCount;
    }

    /// <summary>Logical screen width (the canvas).</summary>
    public int Width { get; }

    public int Height { get; }

    public int FrameCount => (int)_decoder.FrameCount;

    /// <summary>0 = forever; N = repeat N more times after the first play; null = play once (no NETSCAPE2.0 block).</summary>
    public int? LoopCount { get; }

    /// <summary>Total plays implied by <see cref="LoopCount"/>; null for "forever".</summary>
    public int? TotalPlays => LoopCount switch
    {
        null => 1,
        0 => null,
        int n => n + 1,
    };

    /// <summary>Opens a GIF. Returns null when the file is not a GIF Windows can decode.</summary>
    public static async Task<GifAnimationReader?> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        IRandomAccessStream stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read, StorageOpenOptions.AllowReadersAndWriters, FileOpenDisposition.OpenExisting);
        try
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.GifDecoderId, stream).AsTask(cancellationToken);
            int width = await ReadIntAsync(decoder.BitmapContainerProperties, "/logscrdesc/Width") ?? (int)decoder.PixelWidth;
            int height = await ReadIntAsync(decoder.BitmapContainerProperties, "/logscrdesc/Height") ?? (int)decoder.PixelHeight;
            int? loops = await ReadLoopCountAsync(decoder.BitmapContainerProperties);
            if (width <= 0 || height <= 0)
            {
                stream.Dispose();
                return null;
            }

            return new GifAnimationReader(stream, decoder, width, height, loops);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stream.Dispose();
            return null;
        }
    }

    /// <summary>Decodes frame <paramref name="index"/> (its own rectangle, not composed).</summary>
    public async Task<GifFrame> ReadFrameAsync(int index, CancellationToken cancellationToken = default)
    {
        BitmapFrame frame = await _decoder.GetFrameAsync((uint)index).AsTask(cancellationToken);
        PixelDataProvider data = await frame.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        int left = await ReadIntAsync(frame.BitmapProperties, "/imgdesc/Left") ?? 0;
        int top = await ReadIntAsync(frame.BitmapProperties, "/imgdesc/Top") ?? 0;
        int delay = await ReadIntAsync(frame.BitmapProperties, "/grctlext/Delay") ?? 0;
        int disposal = await ReadIntAsync(frame.BitmapProperties, "/grctlext/Disposal") ?? 0;
        return new GifFrame(
            data.DetachPixelData(),
            left,
            top,
            (int)frame.PixelWidth,
            (int)frame.PixelHeight,
            GifComposer.NormalizeDelay(delay),
            disposal is >= 0 and <= 3 ? (GifDisposal)disposal : GifDisposal.Unspecified);
    }

    public void Dispose() => _stream.Dispose();

    private static async Task<int?> ReadIntAsync(BitmapPropertiesView properties, string name)
    {
        try
        {
            BitmapPropertySet values = await properties.GetPropertiesAsync([name]);
            return values.TryGetValue(name, out BitmapTypedValue? value) ? Convert.ToInt32(value.Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or FormatException or OverflowException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            return null; // property absent in this file
        }
    }

    private static async Task<int?> ReadLoopCountAsync(BitmapPropertiesView container)
    {
        try
        {
            BitmapPropertySet values = await container.GetPropertiesAsync(["/appext/Application", "/appext/Data"]);
            if (values.TryGetValue("/appext/Application", out BitmapTypedValue? application) && application.Value is byte[] name &&
                System.Text.Encoding.ASCII.GetString(name).StartsWith("NETSCAPE2.0", StringComparison.Ordinal) &&
                values.TryGetValue("/appext/Data", out BitmapTypedValue? data) && data.Value is byte[] bytes)
            {
                return GifComposer.ParseLoopCount(bytes);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or NotSupportedException)
        {
            // No application extension: the animation plays once.
        }

        return null;
    }
}
