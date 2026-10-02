namespace Mavue.Image.Gif;

/// <summary>How a GIF frame is removed before the next one is drawn (Graphic Control Extension, GIF89a §23).</summary>
public enum GifDisposal
{
    /// <summary>0: not specified (treated like <see cref="Keep"/>).</summary>
    Unspecified = 0,

    /// <summary>1: leave the frame in place.</summary>
    Keep = 1,

    /// <summary>2: clear the frame's rectangle (to transparent, as browsers do).</summary>
    RestoreBackground = 2,

    /// <summary>3: restore the rectangle to what it was before the frame was drawn.</summary>
    RestorePrevious = 3,
}

/// <summary>One decoded GIF frame: BGRA pixels of its own rectangle on the logical screen.</summary>
/// <param name="Pixels">Premultiplied BGRA, <paramref name="Width"/> × <paramref name="Height"/>; alpha is 0 or 255.</param>
public sealed record GifFrame(byte[] Pixels, int Left, int Top, int Width, int Height, TimeSpan Delay, GifDisposal Disposal);

/// <summary>
/// Composes GIF frames onto the logical screen ("canvas"), applying frame offsets, transparency and the
/// disposal of the previous frame. Only the canvas (and, for <see cref="GifDisposal.RestorePrevious"/>, one
/// saved copy) is kept, so memory does not grow with the number of frames.
/// </summary>
public sealed class GifComposer
{
    /// <summary>Delays below this are replaced by <see cref="DefaultDelay"/> (what browsers do for 0/10 ms GIFs).</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private byte[]? _saved; // canvas before a RestorePrevious frame
    private GifFrame? _previous;

    public GifComposer(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        Canvas = new byte[checked(width * height * 4)];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The composed image (premultiplied BGRA, Width × Height). Valid after <see cref="Apply"/>.</summary>
    public byte[] Canvas { get; }

    /// <summary>GIF delays are in 1/100 s; very short ones are shown as 100 ms (browser behavior).</summary>
    public static TimeSpan NormalizeDelay(int centiseconds) =>
        centiseconds * 10 < MinimumDelay.TotalMilliseconds ? DefaultDelay : TimeSpan.FromMilliseconds(centiseconds * 10.0);

    /// <summary>
    /// Loop count from the NETSCAPE2.0 application extension sub-block (<c>01 lo hi</c>, optionally prefixed by its
    /// length byte 3): 0 means forever. Null when there is no such block (the animation plays once).
    /// </summary>
    public static int? ParseLoopCount(ReadOnlySpan<byte> applicationData)
    {
        if (applicationData.Length >= 4 && applicationData[0] == 3 && applicationData[1] == 1)
        {
            return applicationData[2] | (applicationData[3] << 8);
        }

        if (applicationData.Length >= 3 && applicationData[0] == 1)
        {
            return applicationData[1] | (applicationData[2] << 8);
        }

        return null;
    }

    /// <summary>Starts over (first frame of a new loop): clears the canvas.</summary>
    public void Reset()
    {
        Array.Clear(Canvas);
        _saved = null;
        _previous = null;
    }

    /// <summary>Disposes the previous frame as it requested, then draws <paramref name="frame"/>.</summary>
    public void Apply(GifFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_previous is { } previous)
        {
            if (previous.Disposal == GifDisposal.RestoreBackground)
            {
                ClearRect(previous.Left, previous.Top, previous.Width, previous.Height);
            }
            else if (previous.Disposal == GifDisposal.RestorePrevious && _saved is not null)
            {
                _saved.CopyTo(Canvas, 0);
            }
        }

        if (frame.Disposal == GifDisposal.RestorePrevious)
        {
            _saved ??= new byte[Canvas.Length];
            Canvas.CopyTo(_saved, 0);
        }

        Draw(frame);
        _previous = frame;
    }

    private void Draw(GifFrame frame)
    {
        // Clip the frame's rectangle to the canvas (malformed files may place frames outside it).
        int x0 = Math.Max(0, frame.Left), y0 = Math.Max(0, frame.Top);
        int x1 = Math.Min(Width, frame.Left + frame.Width), y1 = Math.Min(Height, frame.Top + frame.Height);
        if (frame.Pixels.Length < frame.Width * frame.Height * 4)
        {
            return;
        }

        for (int y = y0; y < y1; y++)
        {
            int source = (((y - frame.Top) * frame.Width) + (x0 - frame.Left)) * 4;
            int target = ((y * Width) + x0) * 4;
            for (int x = x0; x < x1; x++, source += 4, target += 4)
            {
                if (frame.Pixels[source + 3] != 0) // GIF transparency is all-or-nothing
                {
                    Canvas[target] = frame.Pixels[source];
                    Canvas[target + 1] = frame.Pixels[source + 1];
                    Canvas[target + 2] = frame.Pixels[source + 2];
                    Canvas[target + 3] = frame.Pixels[source + 3];
                }
            }
        }
    }

    private void ClearRect(int left, int top, int width, int height)
    {
        int x0 = Math.Max(0, left), y0 = Math.Max(0, top);
        int x1 = Math.Min(Width, left + width), y1 = Math.Min(Height, top + height);
        for (int y = y0; y < y1; y++)
        {
            Array.Clear(Canvas, ((y * Width) + x0) * 4, (x1 - x0) * 4);
        }
    }
}
