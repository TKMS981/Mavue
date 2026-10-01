namespace Mavue.Ocr;

/// <summary>A recognized line with its bounding box in source-image pixels.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height);

/// <summary>Swappable OCR engine. Runs locally; never sends images to external services (SPEC §27).</summary>
public interface IOcrEngine
{
    /// <summary>Engine identifier for settings and diagnostics.</summary>
    string Name { get; }

    /// <summary>BCP-47 language tags this engine can recognize on this machine.</summary>
    IReadOnlyList<string> AvailableLanguages { get; }
}
