namespace Mavue.Core.Archives;

/// <summary>Result of checking an archive entry name.</summary>
public enum ArchiveNameCheck
{
    Safe = 0,

    /// <summary>Empty, or only separators and "." segments.</summary>
    Empty,

    /// <summary>Rooted: "/x", "\x", "C:\x", "C:x", "\\server\share".</summary>
    Absolute,

    /// <summary>A ".." segment, or one of only dots and spaces that Windows would turn into ".." (path traversal, Zip Slip).</summary>
    Traversal,

    /// <summary>Control characters, ":" (alternate data streams, drives) or characters invalid in Windows names.</summary>
    InvalidCharacters,

    /// <summary>A reserved device name such as CON, NUL, COM1 (with or without an extension).</summary>
    DeviceName,

    /// <summary>Longer than <see cref="ArchiveLimits.MaxNameLength"/>.</summary>
    TooLong,

    /// <summary>More nested than <see cref="ArchiveLimits.MaxPathDepth"/>.</summary>
    TooDeep,

    /// <summary>Bidirectional control characters, which can make a name look like another (e.g. "photo\u202Egpj.exe").</summary>
    BidiControl,
}

/// <summary>
/// Validates and normalizes entry names of archives (ZIP and later formats). An unsafe name is listed with a warning
/// and is never used as a file-system path.
/// </summary>
public static class ArchiveEntryName
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Checks <paramref name="name"/> as stored in the archive (either separator).</summary>
    public static ArchiveNameCheck Check(string name, ArchiveLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        limits ??= ArchiveLimits.Default;
        if (name.Length > limits.MaxNameLength)
        {
            return ArchiveNameCheck.TooLong;
        }

        if (name.StartsWith('/') || name.StartsWith('\\') || (name.Length >= 2 && char.IsAsciiLetter(name[0]) && name[1] == ':'))
        {
            return ArchiveNameCheck.Absolute;
        }

        // "." segments are dropped (as by Normalize): "./" names nothing.
        string[] segments = name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        if (segments.Length == 0)
        {
            return ArchiveNameCheck.Empty;
        }

        if (segments.Length > limits.MaxPathDepth)
        {
            return ArchiveNameCheck.TooDeep;
        }

        foreach (string segment in segments)
        {
            // ".." and names of only dots and spaces ("...", ".. "): Windows drops trailing dots and spaces from
            // path segments, so they can resolve to the parent folder (or to nothing).
            if (segment.AsSpan().TrimEnd(". ").IsEmpty)
            {
                return ArchiveNameCheck.Traversal;
            }

            foreach (char c in segment)
            {
                if (c < 0x20 || c == 0x7F || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                {
                    return ArchiveNameCheck.InvalidCharacters;
                }

                if (IsBidiControl(c))
                {
                    return ArchiveNameCheck.BidiControl;
                }
            }

            string stem = segment.Split('.')[0].TrimEnd(' ');
            if (DeviceNames.Contains(stem))
            {
                return ArchiveNameCheck.DeviceName;
            }
        }

        return ArchiveNameCheck.Safe;
    }

    /// <summary>The name with "/" separators, without empty and "." segments (for display and sorting).</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return string.Join('/', name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Where(s => s != "."));
    }

    /// <summary>
    /// The name as it may be shown: control and bidirectional control characters become U+FFFD, so a name can neither
    /// break the layout nor disguise its extension.
    /// </summary>
    public static string ForDisplay(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!name.Any(c => c < 0x20 || c == 0x7F || IsBidiControl(c)))
        {
            return name;
        }

        return string.Create(name.Length, name, (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                span[i] = c < 0x20 || c == 0x7F || IsBidiControl(c) ? '\uFFFD' : c;
            }
        });
    }

    private static bool IsBidiControl(char c) => c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');
}
