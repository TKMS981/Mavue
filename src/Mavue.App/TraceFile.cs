using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mavue.App;

/// <summary>
/// Diagnostics for automated tests (<c>--trace-file</c>): viewer events as JSON Lines, flushed per line so a test can
/// follow them while the app runs. Off unless the switch is given; written only to the given local file.
/// </summary>
internal sealed class TraceFile : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _gate = new();

    public TraceFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };
    }

    public void Write(string name, IReadOnlyDictionary<string, object?> detail)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteString("event", name);
            json.WriteNumber("qpc", Stopwatch.GetTimestamp());
            foreach ((string key, object? value) in detail)
            {
                switch (value)
                {
                    case null:
                        json.WriteNull(key);
                        break;
                    case bool b:
                        json.WriteBoolean(key, b);
                        break;
                    case int or long or uint or double or float:
                        json.WriteNumber(key, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    default:
                        json.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                }
            }

            json.WriteEndObject();
        }

        string line = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        lock (_gate)
        {
            _writer.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }
}
