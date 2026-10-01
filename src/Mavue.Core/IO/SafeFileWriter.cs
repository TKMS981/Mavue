namespace Mavue.Core.IO;

/// <summary>
/// Writes files without ever leaving the destination in a partially written state
/// (CLAUDE.md §11, docs/ARCHITECTURE.md §6):
/// <list type="number">
/// <item>content is written to a temporary file in the destination directory (same volume),</item>
/// <item>the temporary file is flushed to disk and optionally validated,</item>
/// <item>the destination is atomically replaced (<see cref="File.Replace(string, string, string?)"/>),
/// which on Windows preserves the original's attributes, ACLs and creation time.</item>
/// </list>
/// If any step fails, the original file is left untouched and the temporary file is removed.
/// </summary>
public static class SafeFileWriter
{
    private const string TempPrefix = ".~mavue-";
    private const string TempSuffix = ".tmp";

    /// <summary>Writes <paramref name="destinationPath"/> through a temporary file.</summary>
    /// <param name="destinationPath">Final path. May or may not exist.</param>
    /// <param name="writeContent">Writes the new content to the provided stream.</param>
    /// <param name="validate">
    /// Optional check run against the completed temporary file before it replaces the destination.
    /// Return <c>false</c> to abort; the destination is then left unchanged.
    /// </param>
    /// <param name="backupPath">Optional path that receives the previous version of the destination.</param>
    /// <param name="cancellationToken">Cancels the operation before the destination is replaced.</param>
    /// <exception cref="SafeWriteValidationException">The validator rejected the written content.</exception>
    public static async Task WriteAsync(
        string destinationPath,
        Func<Stream, CancellationToken, Task> writeContent,
        Func<string, CancellationToken, Task<bool>>? validate = null,
        string? backupPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        ArgumentNullException.ThrowIfNull(writeContent);

        string fullPath = ResolveFinalTarget(Path.GetFullPath(destinationPath));
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("Destination must include a directory.", nameof(destinationPath));
        string tempPath = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                BufferSize = 81920,
            };

            var stream = new FileStream(tempPath, options);
            await using (stream.ConfigureAwait(false))
            {
                await writeContent(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (validate is not null && !await validate(tempPath, cancellationToken).ConfigureAwait(false))
            {
                throw new SafeWriteValidationException(fullPath);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, backupPath, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(tempPath, fullPath, overwrite: false);
            }
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    /// <summary>Returns true if <paramref name="path"/> is a temporary file left behind by an interrupted write.</summary>
    public static bool IsTemporaryFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(TempPrefix, StringComparison.Ordinal) && name.EndsWith(TempSuffix, StringComparison.Ordinal);
    }

    /// <summary>
    /// If <paramref name="path"/> is a symbolic link, returns the file it ultimately points to, so saving
    /// updates the target instead of replacing the link with a regular file (which would silently break it).
    /// </summary>
    private static string ResolveFinalTarget(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
        {
            return Path.GetFullPath(target.FullName);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is recognizable via IsTemporaryFile and cleaned up later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Thrown when the validator passed to <see cref="SafeFileWriter"/> rejects the written content.</summary>
public sealed class SafeWriteValidationException : IOException
{
    public SafeWriteValidationException()
    {
    }

    public SafeWriteValidationException(string destinationPath)
        : base($"Validation of the new content failed; '{Path.GetFileName(destinationPath)}' was not modified.")
    {
    }

    public SafeWriteValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
