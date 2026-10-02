using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mavue.Core.Ipc;

/// <summary>
/// Message framing for Mavue's local IPC: a 4-byte little-endian length followed by that many bytes of
/// UTF-8 JSON (docs/ARCHITECTURE.md §9). The length is checked before anything is allocated, so a
/// misbehaving peer cannot make the receiver allocate more than <see cref="MaxMessageBytes"/>.
/// </summary>
public static class IpcFraming
{
    /// <summary>Largest message accepted (1 MiB: far above 1000 maximal paths in practice).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    public static Task WriteRequestAsync(Stream stream, QuickViewRequest request, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, request, IpcJson.Default.QuickViewRequest, cancellationToken);

    public static Task<QuickViewRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync(stream, IpcJson.Default.QuickViewRequest, cancellationToken);

    public static Task WriteResponseAsync(Stream stream, QuickViewResponse response, CancellationToken cancellationToken = default) =>
        WriteAsync(stream, response, IpcJson.Default.QuickViewResponse, cancellationToken);

    public static Task<QuickViewResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ReadAsync(stream, IpcJson.Default.QuickViewResponse, cancellationToken);

    private static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, type);
        if (body.Length > MaxMessageBytes)
        {
            throw new InvalidDataException("IPC message too large.");
        }

        byte[] frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            throw new InvalidDataException("Invalid IPC message length.");
        }

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(body, type) ?? throw new InvalidDataException("Empty IPC message.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Malformed IPC message.", ex);
        }
    }
}
