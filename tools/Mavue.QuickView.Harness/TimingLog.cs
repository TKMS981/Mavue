using System.Text;
using System.Text.Json;

namespace Mavue.QuickView.Harness;

/// <summary>One latency mark written by Mavue.QuickView.Host (QuickViewTimeline JSON Lines).</summary>
internal sealed record Mark(long Request, string Name, long Qpc, Dictionary<string, JsonElement> Properties)
{
    public string? Text(string key) => Properties.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public bool? Bool(string key) => Properties.TryGetValue(key, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    public double? Number(string key) => Properties.TryGetValue(key, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>Tails the host's timing log.</summary>
internal sealed class TimingLog(string path)
{
    private readonly List<Mark> _marks = [];
    private readonly StringBuilder _partial = new();

    // Keeps a UTF-8 sequence split across two reads.
    private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
    private long _position;

    public IReadOnlyList<Mark> Marks => _marks;

    public void Poll()
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= _position)
        {
            return;
        }

        // Advance only by the bytes actually read. Setting the position to the file length after reading lost the
        // lines the host appended in between (seen as a missing media-command mark in media-video).
        stream.Position = _position;
        byte[] buffer = new byte[64 * 1024];
        char[] chars = new char[buffer.Length + 4];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            _position += read;
            int count = _decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            _partial.Append(chars, 0, count);
        }

        string text = _partial.ToString();
        int lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return;
        }

        foreach (string line in text[..lastNewline].Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            var props = new Dictionary<string, JsonElement>();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                props[p.Name] = p.Value.Clone();
            }

            _marks.Add(new Mark(props["request"].GetInt64(), props["mark"].GetString()!, props["qpc"].GetInt64(), props));
        }

        _partial.Clear().Append(text[(lastNewline + 1)..]);
    }

    public Mark? Find(long request, string name) => _marks.LastOrDefault(m => m.Request == request && m.Name == name);

    public Mark? FindGlobal(string name) => _marks.LastOrDefault(m => m.Request == 0 && m.Name == name);

    /// <summary>First request whose "hook" mark is at or after <paramref name="qpc"/>.</summary>
    public long? RequestAfter(long qpc) => _marks.FirstOrDefault(m => m.Name == "hook" && m.Qpc >= qpc)?.Request;

    /// <summary>Latest "hidden" mark at or after <paramref name="qpc"/>, whichever request produced it.</summary>
    public Mark? HiddenSince(long qpc) => _marks.LastOrDefault(m => m.Name == "hidden" && m.Qpc >= qpc);

    public IEnumerable<Mark> ForRequest(long request) => _marks.Where(m => m.Request == request);

    public IEnumerable<Mark> GlobalSince(long qpc) => _marks.Where(m => m.Request == 0 && m.Qpc >= qpc);
}
