using System.Collections.Concurrent;
using System.IO.Pipes;
using Mavue.Core.Ipc;
using Mavue.QuickView.Ipc;

namespace Mavue.QuickView.Tests;

[Trait("Category", "Integration")]
public sealed class QuickViewPipeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueName() => "Mavue.QuickView.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Name_DependsOnUserAndSession_AndHidesSid()
    {
        string a = QuickViewPipe.NameFor("S-1-5-21-1-2-3-1001", 1);
        Assert.Equal(a, QuickViewPipe.NameFor("S-1-5-21-1-2-3-1001", 1));
        Assert.NotEqual(a, QuickViewPipe.NameFor("S-1-5-21-1-2-3-1002", 1));
        Assert.NotEqual(a, QuickViewPipe.NameFor("S-1-5-21-1-2-3-1001", 2));
        Assert.DoesNotContain("1001", a, StringComparison.Ordinal);
        Assert.StartsWith("Mavue.QuickView.1.", a, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_DeliversRequest_AndReturnsReply()
    {
        string name = UniqueName();
        var received = new ConcurrentQueue<QuickViewRequest>();
        await using var server = new QuickViewPipeServer(name, r =>
        {
            received.Enqueue(r);
            return new QuickViewResponse(1, true, null);
        });
        server.Start();

        QuickViewResponse? reply = await QuickViewPipe.SendAsync(name, new QuickViewRequest(1, [@"C:\x\写真.jpg"], 42), TimeSpan.FromSeconds(5), Ct);

        Assert.NotNull(reply);
        Assert.True(reply.Accepted);
        QuickViewRequest only = Assert.Single(received);
        Assert.Equal(@"C:\x\写真.jpg", Assert.Single(only.Paths));
        Assert.Equal(42, only.ForegroundWindow);
    }

    [Fact]
    public async Task Send_ManyClientsAtOnce_AllDelivered()
    {
        // Explorer starts one client per selected item for a command-line verb.
        string name = UniqueName();
        var received = new ConcurrentQueue<string>();
        await using var server = new QuickViewPipeServer(name, r =>
        {
            received.Enqueue(r.Paths[0]);
            return new QuickViewResponse(1, true, null);
        });
        server.Start();

        QuickViewResponse?[] replies = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            QuickViewPipe.SendAsync(name, new QuickViewRequest(1, [$@"C:\{i}.jpg"], 0), TimeSpan.FromSeconds(10), Ct)));

        Assert.All(replies, r => Assert.True(r?.Accepted));
        Assert.Equal(20, received.Distinct().Count());
    }

    [Fact]
    public async Task Server_RejectsInvalidRequest_WithoutCallingHandler()
    {
        string name = UniqueName();
        int calls = 0;
        await using var server = new QuickViewPipeServer(name, _ =>
        {
            Interlocked.Increment(ref calls);
            return new QuickViewResponse(1, true, null);
        });
        server.Start();

        QuickViewResponse? reply = await QuickViewPipe.SendAsync(name, new QuickViewRequest(1, ["relative.jpg"], 0), TimeSpan.FromSeconds(5), Ct);

        Assert.False(reply?.Accepted);
        Assert.Equal("invalid-path", reply?.Error);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Server_SurvivesGarbage_AndKeepsServing()
    {
        string name = UniqueName();
        await using var server = new QuickViewPipeServer(name, _ => new QuickViewResponse(1, true, null));
        server.Start();

        using (var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000, Ct);
            try
            {
                await raw.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F, 1, 2, 3 }, Ct); // absurd length
                await raw.FlushAsync(Ct);
            }
            catch (IOException)
            {
                // The server may already have dropped the connection after reading the bad header.
            }
        }

        QuickViewResponse? reply = await QuickViewPipe.SendAsync(name, new QuickViewRequest(1, [@"C:\a.jpg"], 0), TimeSpan.FromSeconds(5), Ct);
        Assert.True(reply?.Accepted);
    }

    [Fact]
    public async Task Send_NoServer_ReturnsNull()
    {
        QuickViewResponse? reply = await QuickViewPipe.SendAsync(UniqueName(), new QuickViewRequest(1, [@"C:\a.jpg"], 0), TimeSpan.FromMilliseconds(200), Ct);
        Assert.Null(reply);
    }
}
