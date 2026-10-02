using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Mavue.Core.Ipc;

namespace Mavue.QuickView.Ipc;

/// <summary>
/// Named pipe between <c>Mavue.QuickView.Host.exe --quickview</c> (client, started by Explorer for the
/// context-menu command) and the resident Quick View process (server). docs/ARCHITECTURE.md §9.
/// <list type="bullet">
/// <item>The name contains the session id and a hash of the user SID, so sessions and users never meet.</item>
/// <item>The pipe's DACL allows only the current user and denies network logons (remote clients).</item>
/// <item>The client connects with <see cref="PipeOptions.CurrentUserOnly"/>, which checks that the pipe is
/// owned by the current user (another user cannot impersonate the server).</item>
/// <item>Messages are length-prefixed and size-limited (<see cref="IpcFraming"/>); paths are never logged.</item>
/// </list>
/// </summary>
public static partial class QuickViewPipe
{
    /// <summary>Pipe name for the current user and session.</summary>
    public static string NameForCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return NameFor(identity.User!.Value, System.Diagnostics.Process.GetCurrentProcess().SessionId);
    }

    /// <summary>Pipe name for a user SID and session id (the SID is hashed, not exposed).</summary>
    public static string NameFor(string userSid, int sessionId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userSid));
        return $"Mavue.QuickView.{sessionId}.{Convert.ToHexString(hash, 0, 8)}";
    }

    /// <summary>
    /// Sends a request to the resident process. Before sending, the server process is allowed to take the
    /// foreground: the client was started by Explorer from the user's click and holds that right, the
    /// resident process does not (docs/QUICKVIEW-POC.md §3.2).
    /// </summary>
    /// <returns>The reply, or null when no server answered within <paramref name="connectTimeout"/>.</returns>
    public static async Task<QuickViewResponse?> SendAsync(string pipeName, QuickViewRequest request, TimeSpan connectTimeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await client.ConnectAsync(connectTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }

        if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out uint serverProcess))
        {
            AllowSetForegroundWindow(serverProcess);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await IpcFraming.WriteRequestAsync(client, request, timeout.Token).ConfigureAwait(false);
        return await IpcFraming.ReadResponseAsync(client, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Creates one server instance with the restricted DACL.</summary>
    internal static NamedPipeServerStream CreateServerInstance(string pipeName)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            QuickViewPipeServer.MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint serverProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}

/// <summary>
/// Accepts Quick View requests. <see cref="Received"/> runs on a thread-pool thread and must only queue
/// work (the reply is sent after it returns). Invalid requests are answered with an error and dropped.
/// </summary>
public sealed class QuickViewPipeServer : IAsyncDisposable
{
    /// <summary>Concurrent connections (Explorer may start several clients at once for a multi-selection).</summary>
    public const int MaxInstances = 8;

    private readonly string _pipeName;
    private readonly Func<QuickViewRequest, QuickViewResponse> _received;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public QuickViewPipeServer(string pipeName, Func<QuickViewRequest, QuickViewResponse> received)
    {
        _pipeName = pipeName;
        _received = received;
    }

    /// <summary>Raised (diagnostics only) when a connection fails; the argument is an error kind, never content.</summary>
    public event Action<string>? Faulted;

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = QuickViewPipe.CreateServerInstance(_pipeName);
            }
            catch (IOException)
            {
                // All instances busy (or a stale instance being torn down): try again shortly.
                await Task.Delay(50, _stop.Token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _ = HandleAsync(server);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        await using (server.ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                QuickViewRequest request = await IpcFraming.ReadRequestAsync(server, timeout.Token).ConfigureAwait(false);
                QuickViewResponse response = request.Validate() is { } error
                    ? new QuickViewResponse(QuickViewRequest.CurrentVersion, false, error)
                    : _received(request);
                await IpcFraming.WriteResponseAsync(server, response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            {
                Faulted?.Invoke(ex.GetType().Name);
            }
        }
    }
}
