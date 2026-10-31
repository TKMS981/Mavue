using System.Text;

namespace Mavue.QuickView.Resident;

/// <summary>
/// The command line Windows uses to start the resident process again after a crash or hang
/// (<c>RegisterApplicationRestart</c>): the original options, never the files to show — <c>--quickview</c> and every
/// argument after it (all paths) are dropped, so a file that brought the process down is not opened again.
/// </summary>
public static class RestartArguments
{
    /// <summary>Longest command line Windows accepts for a restart (RESTART_MAX_CMD_LINE, 1024, with the terminator).</summary>
    public const int MaxLength = 1023;

    private const string QuickViewSwitch = "--quickview";

    /// <summary>
    /// The arguments for the restart, quoted for <c>CommandLineToArgvW</c>; null when there are none or when they do
    /// not fit (the process then restarts with its defaults).
    /// </summary>
    public static string? Build(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var builder = new StringBuilder();
        foreach (string argument in arguments)
        {
            if (argument.Equals(QuickViewSwitch, StringComparison.OrdinalIgnoreCase))
            {
                break; // the rest are paths
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            AppendQuoted(builder, argument);
        }

        return builder.Length is 0 or > MaxLength ? null : builder.ToString();
    }

    /// <summary>One argument quoted so that <c>CommandLineToArgvW</c> reads it back unchanged.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var builder = new StringBuilder();
        AppendQuoted(builder, argument);
        return builder.ToString();
    }

    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes are literal unless they precede a quote: then each is doubled and the quote escaped.
            builder.Append('\\', c == '"' ? (backslashes * 2) + 1 : backslashes);
            builder.Append(c);
            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2); // before the closing quote
        builder.Append('"');
    }
}
