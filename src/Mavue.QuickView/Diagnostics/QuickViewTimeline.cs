using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;

namespace Mavue.QuickView.Diagnostics;

/// <summary>
/// Quick View latency marks (docs/TESTING.md §6). Every mark carries a QPC timestamp
/// (<see cref="Stopwatch.GetTimestamp"/>), which is comparable across processes on the same machine,
/// so an external harness can measure end to end from the moment it injected the key.
/// <para>
/// Marks go to ETW (EventSource "Mavue-QuickView", capture with WPR/PerfView/dotnet-trace) and,
/// when a log path is given, to a JSON Lines file written on a background thread.
/// Marks never contain file paths or document content; only format, sizes and timings.
/// </para>
/// </summary>
public sealed class QuickViewTimeline : IAsyncDisposable
{
    private readonly Channel<string>? _lines;
    private readonly Task? _writer;

    /// <param name="jsonLinesPath">Optional JSONL output path; null disables file output.</param>
    public QuickViewTimeline(string? jsonLinesPath)
    {
        if (string.IsNullOrEmpty(jsonLinesPath))
        {
            return;
        }

        _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _writer = Task.Run(() => WriteLinesAsync(jsonLinesPath, _lines.Reader));
    }

    /// <summary>Current QPC timestamp.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Records a mark. Thread-safe and non-blocking.</summary>
    /// <param name="requestId">Quick View request (one Space press); 0 for process-level marks.</param>
    /// <param name="name">Stage name, e.g. "hook", "selection", "shown", "first-frame", "thumb-visible", "full-visible".</param>
    /// <param name="timestamp">QPC timestamp of the stage.</param>
    /// <param name="detail">Optional non-sensitive key/value details.</param>
    public void Mark(long requestId, string name, long timestamp, IReadOnlyDictionary<string, object?>? detail = null)
    {
        QuickViewEventSource.Log.Mark(requestId, name, timestamp);
        if (_lines is null)
        {
            return;
        }

        _lines.Writer.TryWrite(Serialize(requestId, name, timestamp, detail));
    }

    public async ValueTask DisposeAsync()
    {
        if (_lines is not null && _writer is not null)
        {
            _lines.Writer.TryComplete();
            await _writer.ConfigureAwait(false);
        }
    }

    private static string Serialize(long requestId, string name, long timestamp, IReadOnlyDictionary<string, object?>? detail)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("request", requestId);
            json.WriteString("mark", name);
            json.WriteNumber("qpc", timestamp);
            json.WriteNumber("freq", Stopwatch.Frequency);
            if (detail is not null)
            {
                foreach ((string key, object? value) in detail)
                {
                    switch (value)
                    {
                        case null: json.WriteNull(key); break;
                        case bool b: json.WriteBoolean(key, b); break;
                        case int i: json.WriteNumber(key, i); break;
                        case long l: json.WriteNumber(key, l); break;
                        case uint u: json.WriteNumber(key, u); break;
                        case double d: json.WriteNumber(key, d); break;
                        default: json.WriteString(key, value.ToString()); break;
                    }
                }
            }

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task WriteLinesAsync(string path, ChannelReader<string> reader)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await using (stream.ConfigureAwait(false))
        {
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            await using (writer.ConfigureAwait(false))
            {
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out string? line))
                    {
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                    }

                    // Flush per batch so external readers (the E2E harness) see marks promptly.
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
        }
    }
}

/// <summary>ETW provider for Quick View latency marks.</summary>
[EventSource(Name = "Mavue-QuickView")]
internal sealed class QuickViewEventSource : EventSource
{
    public static readonly QuickViewEventSource Log = new();

    [Event(1, Level = EventLevel.Informational)]
    public void Mark(long requestId, string name, long qpc)
    {
        if (IsEnabled())
        {
            WriteEvent(1, requestId, name, qpc);
        }
    }
}
