namespace Mavue.Image;

/// <summary>
/// Non-destructive image adjustments (SPEC §6). Neutral values leave the image unchanged.
/// Rendering is implemented with Direct2D effects; this record only carries the parameters.
/// </summary>
public sealed record ImageAdjustments
{
    public static ImageAdjustments Neutral { get; } = new();

    /// <summary>Exposure in EV stops, -2..2.</summary>
    public double Exposure { get; init; }

    /// <summary>-1..1.</summary>
    public double Brightness { get; init; }

    /// <summary>-1..1.</summary>
    public double Contrast { get; init; }

    /// <summary>-1..1 (0 = unchanged).</summary>
    public double Saturation { get; init; }

    /// <summary>-1..1 (cool..warm).</summary>
    public double Temperature { get; init; }

    /// <summary>-1..1 (green..magenta).</summary>
    public double Tint { get; init; }

    /// <summary>Gamma, 0.1..10 (1 = unchanged).</summary>
    public double Gamma { get; init; } = 1.0;

    /// <summary>0..1.</summary>
    public double Sharpness { get; init; }

    public bool IsNeutral => this == Neutral;
}
