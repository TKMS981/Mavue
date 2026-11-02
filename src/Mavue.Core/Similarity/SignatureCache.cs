using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Mavue.Core.Similarity;

/// <summary>
/// Signatures of files already examined by the similar-image search (ARCHITECTURE §4.7.14), so a second search in the
/// same folder decodes only new or changed files. Keyed by a hash of the full path (the file is not named in the
/// cache) plus size and modification time; bounded to <see cref="MaxEntries"/> (least recently used dropped). Stored
/// in one file written atomically. Thread-safe. A damaged or foreign file is ignored (the cache starts empty).
/// </summary>
public sealed class SignatureCache
{
    private static readonly byte[] Magic = "MVSIG1\0\0"u8.ToArray();
    private const int CoarseRecord = 8 + 8 + 8 + 8 + 8 + 8 + 4 + 4 + 1;
    private const int FineRecord = 8 + 48;

    private readonly Dictionary<ulong, Entry> _entries = [];
    private readonly Lock _gate = new();

    public SignatureCache(int maxEntries = 100_000) => MaxEntries = Math.Max(16, maxEntries);

    public int MaxEntries { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Changes since loading (nothing to save otherwise).</summary>
    public bool Dirty { get; private set; }

    public static ulong KeyFor(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()), hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    public bool TryGet(string path, long size, DateTime modifiedUtc, out CoarseSignature coarse, out FineSignature? fine)
    {
        ulong key = KeyFor(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? entry) && entry.Size == size && entry.Modified == modifiedUtc.Ticks)
            {
                entry.Used = DateTime.UtcNow.Ticks;
                coarse = entry.Coarse;
                fine = entry.Fine;
                return true;
            }
        }

        coarse = default;
        fine = null;
        return false;
    }

    public void Set(string path, long size, DateTime modifiedUtc, CoarseSignature coarse, FineSignature? fine)
    {
        ulong key = KeyFor(path);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out Entry? existing) && existing.Size == size && existing.Modified == modifiedUtc.Ticks)
            {
                fine ??= existing.Fine;
            }

            _entries[key] = new Entry(size, modifiedUtc.Ticks, coarse, fine) { Used = DateTime.UtcNow.Ticks };
            Dirty = true;
            if (_entries.Count > MaxEntries + (MaxEntries / 8))
            {
                Trim();
            }
        }
    }

    public static SignatureCache Load(string path, int maxEntries = 100_000)
    {
        var cache = new SignatureCache(maxEntries);
        try
        {
            if (!File.Exists(path))
            {
                return cache;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L * 1024 * 1024)
            {
                return cache; // not ours
            }

            byte[] data = new byte[stream.Length];
            stream.ReadExactly(data);
            cache.Parse(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            cache._entries.Clear();
        }

        cache.Dirty = false;
        return cache;
    }

    /// <summary>Writes the cache atomically (temporary file, then replace). Returns false when it could not be written.</summary>
    public bool Save(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        byte[] data;
        lock (_gate)
        {
            Trim();
            data = Serialize();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, path, overwrite: true);
            Dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void Trim()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (ulong key in _entries.OrderBy(e => e.Value.Used).Take(_entries.Count - MaxEntries).Select(e => e.Key).ToArray())
        {
            _entries.Remove(key);
        }
    }

    private byte[] Serialize()
    {
        int fine = _entries.Values.Count(e => e.Fine is not null);
        byte[] data = new byte[Magic.Length + 4 + (_entries.Count * CoarseRecord) + (fine * FineRecord)];
        Span<byte> o = data;
        Magic.CopyTo(o);
        BinaryPrimitives.WriteInt32LittleEndian(o[8..], _entries.Count);
        int at = 12;
        foreach ((ulong key, Entry e) in _entries)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(o[at..], key);
            BinaryPrimitives.WriteInt64LittleEndian(o[(at + 8)..], e.Size);
            BinaryPrimitives.WriteInt64LittleEndian(o[(at + 16)..], e.Modified);
            BinaryPrimitives.WriteInt64LittleEndian(o[(at + 24)..], e.Used);
            BinaryPrimitives.WriteUInt64LittleEndian(o[(at + 32)..], e.Coarse.DifferenceHash);
            BinaryPrimitives.WriteUInt64LittleEndian(o[(at + 40)..], e.Coarse.AverageHash);
            BinaryPrimitives.WriteUInt32LittleEndian(o[(at + 48)..], e.Coarse.MeanColor);
            BinaryPrimitives.WriteSingleLittleEndian(o[(at + 52)..], e.Coarse.Aspect);
            o[at + 56] = e.Fine is null ? (byte)0 : (byte)1;
            at += CoarseRecord;
            if (e.Fine is { } f)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(o[at..], f.PerceptualHash);
                f.ColorLayout.AsSpan(0, Math.Min(48, f.ColorLayout.Length)).CopyTo(o[(at + 8)..]);
                at += FineRecord;
            }
        }

        return data;
    }

    private void Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || !data.StartsWith(Magic))
        {
            throw new InvalidDataException("Not a signature cache.");
        }

        int count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (count < 0 || count > MaxEntries * 2)
        {
            throw new InvalidDataException("Bad entry count.");
        }

        int at = 12;
        for (int i = 0; i < count; i++)
        {
            if (at + CoarseRecord > data.Length)
            {
                throw new InvalidDataException("Truncated.");
            }

            ReadOnlySpan<byte> r = data[at..];
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(r);
            var coarse = new CoarseSignature(
                BinaryPrimitives.ReadUInt64LittleEndian(r[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(r[40..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(r[52..]));
            bool hasFine = r[56] == 1;
            at += CoarseRecord;
            FineSignature? fine = null;
            if (hasFine)
            {
                if (at + FineRecord > data.Length)
                {
                    throw new InvalidDataException("Truncated.");
                }

                fine = new FineSignature(BinaryPrimitives.ReadUInt64LittleEndian(data[at..]), data.Slice(at + 8, 48).ToArray());
                at += FineRecord;
            }

            _entries[key] = new Entry(BinaryPrimitives.ReadInt64LittleEndian(r[8..]), BinaryPrimitives.ReadInt64LittleEndian(r[16..]), coarse, fine)
            {
                Used = BinaryPrimitives.ReadInt64LittleEndian(r[24..]),
            };
        }
    }

    private sealed record Entry(long Size, long Modified, CoarseSignature Coarse, FineSignature? Fine)
    {
        public long Used { get; set; }
    }
}
