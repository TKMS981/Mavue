using System.Runtime.InteropServices;
using Mavue.QuickView.Resident;

namespace Mavue.QuickView.Tests;

/// <summary>The command line of an automatic restart after a crash (ARCHITECTURE §4.7.5).</summary>
[Trait("Category", "QuickView")]
public partial class RestartArgumentsTests
{
    [Fact]
    public void NoArguments_RestartsWithDefaults()
    {
        Assert.Null(RestartArguments.Build([]));
    }

    [Fact]
    public void Options_AreKept()
    {
        Assert.Equal("--settings C:\\s.json --no-prewarm", RestartArguments.Build(["--settings", @"C:\s.json", "--no-prewarm"]));
    }

    [Fact]
    public void FilesToShow_AreNeverReopened()
    {
        // The file that crashed the process must not be shown again automatically.
        Assert.Null(RestartArguments.Build(["--quickview", @"C:\bad.jpg"]));
        Assert.Equal("--no-prewarm", RestartArguments.Build(["--no-prewarm", "--quickview", @"C:\bad.jpg", @"C:\two.png"]));
    }

    [Fact]
    public void QuickViewSwitch_IsCaseInsensitive()
    {
        Assert.Equal("--trace-shell", RestartArguments.Build(["--trace-shell", "--QuickView", @"C:\a.jpg"]));
    }

    [Fact]
    public void ALongCommandLine_FallsBackToDefaults()
    {
        string path = @"C:\" + new string('a', RestartArguments.MaxLength);
        Assert.Null(RestartArguments.Build(["--settings", path]));
    }

    [Fact]
    public void TheLongestCommandLine_IsKept()
    {
        string value = new('a', RestartArguments.MaxLength - "--settings ".Length);
        string? line = RestartArguments.Build(["--settings", value]);
        Assert.NotNull(line);
        Assert.Equal(RestartArguments.MaxLength, line.Length);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData(@"C:\Program Files\Mavue\", "\"C:\\Program Files\\Mavue\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\no\spaces\", @"C:\no\spaces\")]
    public void Quote_FollowsTheWindowsRules(string argument, string expected)
    {
        Assert.Equal(expected, RestartArguments.Quote(argument));
    }

    private static readonly string[][] RoundTrips =
    [
        ["--settings", @"C:\Users\テスト\AppData\Local\Mavue\settings.json"],
        ["--settings", @"C:\with space\"],
        ["--timing-log", @"C:\a\\b\\\"],
        ["--verb", "say \"hi\"", "\\\"quoted\\\""],
        ["", "\t tab", "line\nbreak", "a\\\\\"b"],
        ["--activation", "panel", "--interpolation", "fant", "--no-idle-trim"],
    ];

    public static TheoryData<string[]> RoundTripCases() => [.. RoundTrips];

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Build_IsReadBackUnchangedByWindows(string[] arguments)
    {
        string? line = RestartArguments.Build(arguments);
        Assert.NotNull(line);
        Assert.Equal(arguments, ParseLikeWindows(line));
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RestartArguments.Build(null!));
        Assert.Throws<ArgumentNullException>(() => RestartArguments.Quote(null!));
    }

    private static string[] ParseLikeWindows(string arguments)
    {
        // CommandLineToArgvW treats the first token as the program name; give it one.
        nint argv = CommandLineToArgvW("Mavue.QuickView.Host.exe " + arguments, out int count);
        Assert.NotEqual(0, argv);
        try
        {
            var result = new string[count - 1];
            for (int i = 1; i < count; i++)
            {
                result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
