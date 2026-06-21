using System.Runtime.CompilerServices;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// AKPK/WEM XOR 流密码。端口 fluffy-dumper/audio/vfs.rs。
/// 两个魔常量：0x9C5A0B29（初始 XOR）、81861667（乘法常量）。
/// </summary>
public static class AkpkCrypto
{
    private const uint MulConst = 81861667u;
    private const uint XorConst = 0x9C5A0B29u;

    /// <summary>
    /// 从 seed 派生 32 位密钥：4 轮 wrapping_mul + 逐字节 XOR 混入。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint DeriveKey(uint seed)
    {
        uint k = unchecked(((seed & 0xFF) ^ XorConst) * MulConst);
        k = unchecked((k ^ ((seed >> 8) & 0xFF)) * MulConst);
        k = unchecked((k ^ ((seed >> 16) & 0xFF)) * MulConst);
        k = unchecked((k ^ ((seed >> 24) & 0xFF)) * MulConst);
        return k;
    }

    /// <summary>
    /// 就地解密/加密（对称）。按 4 字节块对齐 XOR，key_index = seed + (data_offset >> 2)。
    /// </summary>
    public static void DecryptVfs(byte[] data, int start, int length, uint seed, uint dataOffset = 0)
    {
        uint keyIndex = unchecked(seed + (dataOffset >> 2));
        int pos = start;
        int remaining = length;
        int alignment = (int)(dataOffset & 3);

        // 头部未对齐部分：逐字节 XOR
        if (alignment != 0)
        {
            uint key = DeriveKey(keyIndex);
            int toAlign = Math.Min(4 - alignment, remaining);
            for (int i = 0; i < toAlign; i++)
            {
                if (pos >= start + length) break;
                int bytePos = alignment + i;
                data[pos] ^= (byte)((key >> (bytePos * 8)) & 0xFF);
                pos++;
            }
            remaining -= toAlign;
            keyIndex = unchecked(keyIndex + 1);
        }

        // 主体 4 字节块：整块 little-endian XOR
        int blockCount = remaining / 4;
        for (int i = 0; i < blockCount; i++)
        {
            uint key = DeriveKey(keyIndex);
            uint value = unchecked(
                (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24)) ^ key);

            data[pos] = (byte)(value & 0xFF);
            data[pos + 1] = (byte)((value >> 8) & 0xFF);
            data[pos + 2] = (byte)((value >> 16) & 0xFF);
            data[pos + 3] = (byte)((value >> 24) & 0xFF);

            pos += 4;
            keyIndex = unchecked(keyIndex + 1);
        }

        // 尾部 trailing（&lt;4 字节）：逐字节 XOR
        int trailing = remaining & 3;
        if (trailing > 0)
        {
            uint key = DeriveKey(keyIndex);
            for (int i = 0; i < trailing; i++)
            {
                data[pos] ^= (byte)((key >> (i * 8)) & 0xFF);
                pos++;
            }
        }
    }

    /// <summary>
    /// 解密单个 WEM 文件（seed = wem_id）。
    /// </summary>
    public static void DecryptWem(byte[] data, uint wemId)
    {
        DecryptVfs(data, 0, data.Length, wemId, 0);
    }
}
