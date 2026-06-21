using System.Buffers.Binary;

namespace AnimeStudio.Endfield;

/// <summary>
/// XXTEA decryption. Faithful port of fluffy-dumper/xxtea/src/lib.rs.
/// </summary>
public static class Xxtea
{
    private const uint Delta = 0x9E3779B9;

    public static byte[] Decrypt(byte[] data, byte[] key)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (key.Length != 16)
            throw new ArgumentException($"key must be exactly 16 bytes, got {key.Length}", nameof(key));
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0) return Array.Empty<byte>();

        var v = BytesToU32Le(data);

        var k = new uint[4];
        k[0] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(0, 4));
        k[1] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(4, 4));
        k[2] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(8, 4));
        k[3] = BinaryPrimitives.ReadUInt32LittleEndian(key.AsSpan(12, 4));

        int n = v.Length;
        if (n < 2)
        {
            // Match Rust: return data clone.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            return copy;
        }

        int rounds = 6 + 52 / n;
        uint sum = unchecked((uint)rounds * Delta);

        uint y = v[0];

        unchecked
        {
            while (sum != 0)
            {
                uint e = (sum >> 2) & 3;

                for (int p = n - 1; p >= 1; p--)
                {
                    uint z = v[p - 1];
                    v[p] = v[p] - Mx(sum, y, z, p, e, k);
                    y = v[p];
                }

                {
                    uint z = v[n - 1];
                    v[0] = v[0] - Mx(sum, y, z, 0, e, k);
                    y = v[0];
                }

                sum = sum - Delta;
            }
        }

        var result = U32ToBytesLe(v);

        // Truncate based on length suffix if valid.
        long original = v[n - 1];
        long maxLen = (long)n * 4;
        long minLen = Math.Max(0, maxLen - 7);
        if (original >= minLen && original <= maxLen)
        {
            if (result.Length > original)
            {
                Array.Resize(ref result, (int)original);
            }
        }
        return result;
    }

    private static uint Mx(uint sum, uint y, uint z, int p, uint e, uint[] k)
    {
        unchecked
        {
            uint left = ((z >> 5) ^ (y << 2)) + ((y >> 3) ^ (z << 4));
            uint right = (sum ^ y) + (k[(p & 3) ^ (int)e] ^ z);
            return left ^ right;
        }
    }

    private static uint[] BytesToU32Le(byte[] data)
    {
        int paddedLen = ((data.Length + 3) / 4) * 4;
        var padded = new byte[paddedLen];
        Buffer.BlockCopy(data, 0, padded, 0, data.Length);

        var result = new uint[paddedLen / 4];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(padded.AsSpan(i * 4, 4));
        }
        return result;
    }

    private static byte[] U32ToBytesLe(uint[] data)
    {
        var result = new byte[data.Length * 4];
        for (int i = 0; i < data.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4, 4), data[i]);
        }
        return result;
    }
}
