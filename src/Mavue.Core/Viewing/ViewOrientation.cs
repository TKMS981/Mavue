namespace Mavue.Core.Viewing;

/// <summary>
/// How the viewer turns content on screen (the file is never changed): a horizontal mirror applied first, then a
/// clockwise rotation in quarter turns. The operations are expressed as the user sees them — "flip horizontally"
/// mirrors what is on screen left to right, whatever the current rotation.
/// </summary>
/// <param name="QuarterTurns">Clockwise quarter turns, 0–3.</param>
/// <param name="Mirrored">Mirrored left to right before rotating.</param>
public readonly record struct ViewOrientation(int QuarterTurns, bool Mirrored)
{
    public static ViewOrientation Identity => default;

    public bool IsIdentity => QuarterTurns == 0 && !Mirrored;

    /// <summary>True for 90° and 270°: width and height trade places on screen.</summary>
    public bool SwapsAxes => (QuarterTurns & 1) == 1;

    /// <summary>Rotation in degrees (0, 90, 180, 270).</summary>
    public int Degrees => QuarterTurns * 90;

    public ViewOrientation RotateClockwise() => this with { QuarterTurns = Normalize(QuarterTurns + 1) };

    public ViewOrientation RotateCounterClockwise() => this with { QuarterTurns = Normalize(QuarterTurns - 1) };

    /// <summary>Mirrors the screen left to right: M ∘ R(q) ∘ F = R(−q) ∘ M ∘ F.</summary>
    public ViewOrientation FlipHorizontal() => new(Normalize(-QuarterTurns), !Mirrored);

    /// <summary>Mirrors the screen top to bottom (a horizontal mirror followed by a half turn).</summary>
    public ViewOrientation FlipVertical() => new(Normalize(2 - QuarterTurns), !Mirrored);

    /// <summary>Size on screen of content that is <paramref name="width"/> × <paramref name="height"/> before turning.</summary>
    public (T Width, T Height) Oriented<T>(T width, T height) => SwapsAxes ? (height, width) : (width, height);

    /// <summary>
    /// Where the source pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="width"/> ×
    /// <paramref name="height"/> image lands on screen.
    /// </summary>
    public (int X, int Y) MapPixel(int x, int y, int width, int height)
    {
        if (Mirrored)
        {
            x = width - 1 - x;
        }

        return QuarterTurns switch
        {
            1 => (height - 1 - y, x),
            2 => (width - 1 - x, height - 1 - y),
            3 => (y, width - 1 - x),
            _ => (x, y),
        };
    }

    private static int Normalize(int quarterTurns) => ((quarterTurns % 4) + 4) % 4;
}
