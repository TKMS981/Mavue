using System.Text;
using Mavue.Core.IO;

namespace Mavue.Core.Tests;

[Trait("Category", "Unit")]
public sealed class SafeFileWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mavue-safewrite-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Func<Stream, CancellationToken, Task> Content(string text) =>
        (stream, ct) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    private string[] LeftoverTempFiles() =>
        Directory.GetFiles(_dir).Where(SafeFileWriter.IsTemporaryFile).ToArray();

    [Fact]
    public async Task WriteAsync_CreatesNewFile()
    {
        string path = Path.Combine(_dir, "new.txt");

        await SafeFileWriter.WriteAsync(path, Content("hello"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("hello", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task WriteAsync_ReplacesExistingFile_AndWritesBackup()
    {
        string path = Path.Combine(_dir, "doc.txt");
        string backup = Path.Combine(_dir, "doc.bak");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);

        await SafeFileWriter.WriteAsync(path, Content("new"), backupPath: backup, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("old", await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task WriteAsync_LeavesOriginalUntouched_WhenWriterThrows()
    {
        string path = Path.Combine(_dir, "doc.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

        async Task FailingWriter(Stream stream, CancellationToken ct)
        {
            await stream.WriteAsync("partial"u8.ToArray(), ct);
            throw new InvalidOperationException("encoder failed");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SafeFileWriter.WriteAsync(path, FailingWriter, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task WriteAsync_LeavesOriginalUntouched_WhenValidationFails()
    {
        string path = Path.Combine(_dir, "doc.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<SafeWriteValidationException>(() =>
            SafeFileWriter.WriteAsync(path, Content("corrupt"), validate: (_, _) => Task.FromResult(false), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task WriteAsync_ValidatorSeesCompleteContentBeforeReplace()
    {
        string path = Path.Combine(_dir, "doc.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        string? seen = null;

        await SafeFileWriter.WriteAsync(
            path,
            Content("complete"),
            validate: async (temp, ct) =>
            {
                seen = await File.ReadAllTextAsync(temp, ct);
                Assert.Equal("original", await File.ReadAllTextAsync(path, ct));
                return true;
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("complete", seen);
        Assert.Equal("complete", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_Cancelled_LeavesOriginalUntouched()
    {
        string path = Path.Combine(_dir, "doc.txt");
        await File.WriteAllTextAsync(path, "original", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SafeFileWriter.WriteAsync(
                path,
                Content("new"),
                validate: (_, _) =>
                {
                    cts.Cancel();
                    return Task.FromResult(true);
                },
                cancellationToken: cts.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task WriteAsync_PreservesHiddenAttribute()
    {
        string path = Path.Combine(_dir, "hidden.txt");
        await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        File.SetAttributes(path, FileAttributes.Hidden);

        await SafeFileWriter.WriteAsync(path, Content("new"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal("new", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_SupportsJapaneseFileNames()
    {
        string path = Path.Combine(_dir, "書類 – 契約書（最終版）.pdf");

        await SafeFileWriter.WriteAsync(path, Content("%PDF-1.7"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("%PDF-1.7", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_ThroughSymbolicLink_UpdatesTargetAndKeepsLink()
    {
        string target = Path.Combine(_dir, "target.txt");
        string link = Path.Combine(_dir, "link.txt");
        await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating symlinks needs Developer Mode or elevation on Windows.
            Assert.Skip($"Cannot create symbolic links here: {ex.GetType().Name}");
        }

        await SafeFileWriter.WriteAsync(link, Content("new"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Empty(LeftoverTempFiles());
    }

    [Theory]
    [InlineData(".~mavue-0123456789abcdef.tmp", true)]
    [InlineData("photo.jpg", false)]
    [InlineData(".~mavue-x.txt", false)]
    public void IsTemporaryFile_RecognizesOwnTempFiles(string name, bool expected)
    {
        Assert.Equal(expected, SafeFileWriter.IsTemporaryFile(Path.Combine(_dir, name)));
    }
}
