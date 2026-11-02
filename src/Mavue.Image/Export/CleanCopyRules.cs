using System.Globalization;
using Mavue.Core.IO;
using Mavue.Core.Viewing;
using Mavue.Image.Wic;

namespace Mavue.Image.Export;

/// <summary>Why a copy without metadata was not made (the same messages in Quick View and the main app).</summary>
public enum CleanCopyFailure
{
    /// <summary>The written copy did not pass the verification; nothing was saved.</summary>
    NotVerified,

    /// <summary>Windows cannot decode the image (a codec extension such as HEIF or Raw Image is missing).</summary>
    NoCodec,

    /// <summary>The image is too large to convert within the memory limit.</summary>
    TooLarge,

    /// <summary>The destination cannot be written (read-only folder, no permission).</summary>
    NotWritable,

    /// <summary>Anything else (the file changed or disappeared, the worker failed…).</summary>
    Failed,
}

/// <summary>
/// The rules both apps follow for copies without metadata (ARCHITECTURE §4.7.13, §4.7.15): the name next to the
/// original, the message for a failure, the export worker's memory limit and the clean-up of temporary copies.
/// </summary>
public static class CleanCopyRules
{
    /// <summary>Temporary copies (clipboard, worker output) older than this are deleted when the next one is made.</summary>
    public static readonly TimeSpan TemporaryLifetime = TimeSpan.FromHours(24);

    /// <summary>The worker's output folder inside an app's temporary folder.</summary>
    public const string WorkFolder = "work";

    /// <summary>"photo (no metadata).jpg", "photo (no metadata) (2).jpg"… in the original's folder (an unused name).</summary>
    /// <param name="nameFormat">The localized pattern, "{0} (no metadata)".</param>
    public static string NameNextTo(string original, string nameFormat, string extension)
    {
        ArgumentException.ThrowIfNullOrEmpty(original);
        ArgumentException.ThrowIfNullOrEmpty(nameFormat);
        string folder = Path.GetDirectoryName(original)!;
        string stem = string.Format(CultureInfo.CurrentCulture, nameFormat, Path.GetFileNameWithoutExtension(original));
        string candidate = Path.Combine(folder, stem + extension);
        for (int n = 2; File.Exists(candidate) && n < 1000; n++)
        {
            candidate = Path.Combine(folder, stem + $" ({n})" + extension);
        }

        return candidate;
    }

    /// <summary>True when <paramref name="destination"/> is the original itself (never allowed).</summary>
    public static bool IsOriginal(string original, string destination) =>
        string.Equals(Path.GetFullPath(original), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase);

    /// <summary>The message for a failed copy; null for an exception that is not a copy failure.</summary>
    public static CleanCopyFailure? Classify(Exception exception) => exception switch
    {
        SafeWriteValidationException => CleanCopyFailure.NotVerified,
        CleanExportException { Message: "unsupported" } or NotSupportedException => CleanCopyFailure.NoCodec,
        CleanExportException { Message: "too-large" } or ImageTooLargeException => CleanCopyFailure.TooLarge,
        UnauthorizedAccessException => CleanCopyFailure.NotWritable,
        CleanExportException or IOException or InvalidDataException or ArgumentException or System.Runtime.InteropServices.COMException => CleanCopyFailure.Failed,
        _ => null,
    };

    /// <summary>The worker's memory limit for a machine of <paramref name="tier"/>.</summary>
    public static long WorkerMemoryLimit(PerformanceTier tier) => tier switch
    {
        PerformanceTier.Low => 1024L << 20,
        PerformanceTier.Balanced => 1536L << 20,
        _ => 2048L << 20,
    };

    /// <summary>
    /// Deletes clipboard copies (one folder each) older than <see cref="TemporaryLifetime"/>, and old files left in the
    /// worker's folder by a crash (file by file: a conversion may be running). Errors are ignored (retried next time).
    /// </summary>
    public static void DeleteOldTemporaryCopies(string temporaryRoot, DateTime utcNow)
    {
        try
        {
            if (!Directory.Exists(temporaryRoot))
            {
                return;
            }

            foreach (string folder in Directory.EnumerateDirectories(temporaryRoot))
            {
                if (string.Equals(Path.GetFileName(folder), WorkFolder, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        if (utcNow - File.GetLastWriteTimeUtc(file) > TemporaryLifetime)
                        {
                            File.Delete(file);
                        }
                    }
                }
                else if (utcNow - Directory.GetCreationTimeUtc(folder) > TemporaryLifetime)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retried next time.
        }
    }
}

/// <summary>A converted export failed in the worker; <see cref="Exception.Message"/> is its code ("unsupported", "too-large"…).</summary>
public sealed class CleanExportException : Exception
{
    public CleanExportException()
    {
    }

    public CleanExportException(string code)
        : base(code)
    {
    }

    public CleanExportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
