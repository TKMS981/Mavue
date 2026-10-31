namespace Mavue.Core.Archives;

/// <summary>CRC-32 (IEEE 802.3, as in ZIP), slice-by-8. Used to tell a damaged entry from a valid one.</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFF;

    /// <summary>The CRC of everything appended so far.</summary>
    public uint Value => ~_crc;

    /// <summary>The CRC of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = new Crc32();
        crc.Append(data);
        return crc.Value;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        uint crc = _crc;
        uint[] t = Table;
        while (data.Length >= 8)
        {
            uint one = (uint)(data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24)) ^ crc;
            uint two = (uint)(data[4] | (data[5] << 8) | (data[6] << 16) | (data[7] << 24));
            crc = t[(7 * 256) + (one & 0xFF)] ^ t[(6 * 256) + ((one >> 8) & 0xFF)] ^ t[(5 * 256) + ((one >> 16) & 0xFF)] ^ t[(4 * 256) + (one >> 24)]
                ^ t[(3 * 256) + (two & 0xFF)] ^ t[(2 * 256) + ((two >> 8) & 0xFF)] ^ t[256 + ((two >> 16) & 0xFF)] ^ t[two >> 24];
            data = data[8..];
        }

        foreach (byte b in data)
        {
            crc = t[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        _crc = crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        for (int i = 0; i < 256; i++)
        {
            for (int slice = 1; slice < 8; slice++)
            {
                uint previous = table[((slice - 1) * 256) + i];
                table[(slice * 256) + i] = (previous >> 8) ^ table[previous & 0xFF];
            }
        }

        return table;
    }
}
