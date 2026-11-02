namespace Mavue.Core.Similarity;

/// <summary>How alike images must be to be listed (Quick View's similar-image search; ARCHITECTURE §4.7.14).</summary>
public enum SimilarSensitivity
{
    /// <summary>Also loosely similar pictures (same scene, different framing): 64 % and more.</summary>
    Loose,

    /// <summary>Resized, recompressed and lightly edited copies: 72 % and more (the default).</summary>
    Normal,

    /// <summary>Near-identical copies only: 84 % and more.</summary>
    Strict,
}

/// <summary>Thresholds of the sensitivities, and the signature cache file.</summary>
public static class SimilarSearchSettings
{
    public static int Threshold(SimilarSensitivity sensitivity) => sensitivity switch
    {
        SimilarSensitivity.Loose => 64,
        SimilarSensitivity.Strict => 84,
        _ => 72,
    };

    /// <summary>The signature cache of the current user (the only file the search keeps).</summary>
    public static string CachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mavue", "QuickView", "SimilarCache", "signatures.bin");

    /// <summary>Size of the cache file in bytes (0 when there is none).</summary>
    public static long CacheBytes(string? path = null)
    {
        try
        {
            var file = new FileInfo(path ?? CachePath);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Deletes the cache (and temporary files of an interrupted save). Returns false when it is in use (a search is
    /// saving it); nothing is lost by trying again later.
    /// </summary>
    public static bool DeleteCache(string? path = null)
    {
        string file = path ?? CachePath;
        try
        {
            File.Delete(file);
            string? folder = Path.GetDirectoryName(file);
            if (folder is not null && Directory.Exists(folder))
            {
                foreach (string temporary in Directory.EnumerateFiles(folder, Path.GetFileName(file) + ".*.tmp"))
                {
                    File.Delete(temporary);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
