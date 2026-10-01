namespace Mavue.Print;

/// <summary>Scaling modes offered in the print dialog (SPEC §18).</summary>
public enum PrintScaling
{
    FitToPaper,
    ActualSize,
    Custom,
}

/// <summary>User-selected print options, independent of the print API used to submit the job.</summary>
public sealed record PrintSettings
{
    /// <summary>Page ranges such as "1-3,5"; null prints all pages.</summary>
    public string? PageRanges { get; init; }

    public int Copies { get; init; } = 1;

    /// <summary>Document pages per sheet (N-up): 1, 2, 4, 6, 9 or 16.</summary>
    public int PagesPerSheet { get; init; } = 1;

    public PrintScaling Scaling { get; init; } = PrintScaling.FitToPaper;

    /// <summary>Percentage used when <see cref="Scaling"/> is <see cref="PrintScaling.Custom"/>.</summary>
    public int CustomScalePercent { get; init; } = 100;

    public bool Monochrome { get; init; }
}
