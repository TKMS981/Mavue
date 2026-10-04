namespace Mavue.Pdf.Pdfium;

/// <summary>
/// Process-wide PDFium state. PDFium is not thread-safe, so every call into it — from any document, any thread —
/// runs while holding <see cref="Gate"/>. The library is initialized on first use and stays loaded for the life of
/// the process (FPDF_DestroyLibrary would invalidate documents still open elsewhere).
/// </summary>
public static class PdfiumLibrary
{
    private static readonly Lock InitGate = new();
    private static bool? s_available;

    /// <summary>Serializes all PDFium calls.</summary>
    internal static Lock Gate { get; } = new();

    /// <summary>
    /// True when pdfium.dll is present and initialized. False (never throws) when the native library is missing or
    /// cannot be loaded; callers then fall back to Windows.Data.Pdf for display.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            if (s_available is { } known)
            {
                return known;
            }

            lock (InitGate)
            {
                s_available ??= TryInitialize();
                return s_available.Value;
            }
        }
    }

    /// <summary>Throws when PDFium cannot be used.</summary>
    internal static void EnsureInitialized()
    {
        if (!IsAvailable)
        {
            throw new DllNotFoundException("pdfium.dll could not be loaded.");
        }
    }

    private static unsafe bool TryInitialize()
    {
        try
        {
            lock (Gate)
            {
                // Version 2: no user font paths, no V8 (the binaries are built without V8/XFA).
                var config = new PdfiumNative.LibraryConfig { Version = 2 };
                PdfiumNative.FPDF_InitLibraryWithConfig(&config);
            }

            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }
}
