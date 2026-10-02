using System.Buffers.Binary;
using System.Text;
using Mavue.Core.Ipc;

namespace Mavue.Core.Tests;

[Trait("Category", "Unit")]
public sealed class IpcFramingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Request_RoundTrips()
    {
        var request = new QuickViewRequest(QuickViewRequest.CurrentVersion, [@"C:\写真\a b.jpg", @"\\server\share\c.pdf"], 0x1234);
        using var stream = new MemoryStream();

        await IpcFraming.WriteRequestAsync(stream, request, Ct);
        stream.Position = 0;
        QuickViewRequest read = await IpcFraming.ReadRequestAsync(stream, Ct);

        Assert.Equal(request.Version, read.Version);
        Assert.Equal(request.Paths, read.Paths);
        Assert.Equal(0x1234, read.ForegroundWindow);
    }

    [Fact]
    public async Task Response_RoundTrips()
    {
        using var stream = new MemoryStream();

        await IpcFraming.WriteResponseAsync(stream, new QuickViewResponse(1, false, "no-paths"), Ct);
        stream.Position = 0;
        QuickViewResponse read = await IpcFraming.ReadResponseAsync(stream, Ct);

        Assert.False(read.Accepted);
        Assert.Equal("no-paths", read.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcFraming.MaxMessageBytes + 1)]
    public async Task Read_RejectsBadLength_BeforeAllocating(int length)
    {
        byte[] frame = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcFraming.ReadRequestAsync(new MemoryStream(frame), Ct));
    }

    [Fact]
    public async Task Read_RejectsMalformedJson()
    {
        byte[] body = Encoding.UTF8.GetBytes("{not json");
        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);

        await Assert.ThrowsAsync<InvalidDataException>(() => IpcFraming.ReadRequestAsync(new MemoryStream(frame), Ct));
    }

    [Fact]
    public async Task Read_TruncatedFrame_Throws()
    {
        byte[] frame = new byte[4 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 100);

        await Assert.ThrowsAsync<EndOfStreamException>(() => IpcFraming.ReadRequestAsync(new MemoryStream(frame), Ct));
    }

    [Theory]
    [InlineData(1, @"C:\a.jpg", null)]
    [InlineData(2, @"C:\a.jpg", "unsupported-version")]
    [InlineData(1, "a.jpg", "invalid-path")]
    [InlineData(1, @"\a.jpg", "invalid-path")]
    [InlineData(1, "C:a.jpg", "invalid-path")]
    [InlineData(1, "", "invalid-path")]
    [InlineData(1, "C:\\a\0.jpg", "invalid-path")]
    [InlineData(1, @"\\server\share\a.jpg", null)]
    public void Validate(int version, string path, string? expected) =>
        Assert.Equal(expected, new QuickViewRequest(version, [path], 0).Validate());

    [Fact]
    public void Validate_RejectsEmptyAndTooMany()
    {
        Assert.Equal("no-paths", new QuickViewRequest(1, [], 0).Validate());
        string[] many = Enumerable.Range(0, QuickViewRequest.MaxPaths + 1).Select(i => $@"C:\{i}.jpg").ToArray();
        Assert.Equal("too-many-paths", new QuickViewRequest(1, many, 0).Validate());
    }
}
