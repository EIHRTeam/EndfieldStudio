using System.Collections.Generic;
using System.IO;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// AKPK 音频包解析器。端口 fluffy-dumper/audio/akpk.rs。
/// 流程：解密 AKPK 头 → 解析 languages/banks/sounds/externals 段 →
/// banks 段内 BNK 有 DIDX/Data 子段定位 WEM；sounds/externals 段直接是 WEM。
/// </summary>
public sealed class AkpkPackage
{
    private static readonly byte[] EncryptedMagic = { (byte)':', (byte)')', (byte)'x', (byte)'D' };
    private static readonly byte[] AkpkMagic = { (byte)'A', (byte)'K', (byte)'P', (byte)'K' };
    private static readonly byte[] BkhdMagic = { (byte)'B', (byte)'K', (byte)'H', (byte)'D' };
    private static readonly byte[] DidxMagic = { (byte)'D', (byte)'I', (byte)'D', (byte)'X' };
    private static readonly byte[] DataMagic = { (byte)'D', (byte)'A', (byte)'T', (byte)'A' };
    private static readonly byte[] RiffMagic = { (byte)'R', (byte)'I', (byte)'F', (byte)'F' };
    private static readonly byte[] RifxMagic = { (byte)'R', (byte)'I', (byte)'F', (byte)'X' };

    public sealed class WemEntry
    {
        public required ulong Id;
        public required ulong Offset;
        public required ulong Size;
        public string? Language;
    }

    public List<WemEntry> Entries { get; } = new();
    public Dictionary<uint, string> Languages { get; } = new();

    private readonly byte[] _data;

    private AkpkPackage(byte[] data)
    {
        _data = data;
    }

    /// <summary>
    /// 解析 AKPK 字节流。若 magic 是 ":)xD" 则先解密头部。
    /// </summary>
    public static AkpkPackage Parse(byte[] data)
    {
        // 解密头部
        if (BytesEqual(data, 0, EncryptedMagic, 0, 4))
        {
            uint headerSize = ReadU32LE(data, 4);
            AkpkCrypto.DecryptVfs(data, 12, (int)headerSize - 4, headerSize, 0);
            Array.Copy(AkpkMagic, 0, data, 0, 4);
            WriteU32LE(data, 8, 1);
        }

        if (!BytesEqual(data, 0, AkpkMagic, 0, 4))
            throw new InvalidDataException("invalid AKPK magic");

        var pkg = new AkpkPackage(data);
        pkg.ParseInternal();
        return pkg;
    }

    private void ParseInternal()
    {
        // 偏移 4: header_size, 8: endian_check(这里忽略，按 LE 读)
        int pos = 4;
        uint headerSize = ReadU32LE(_data, pos); pos += 4;
        uint flag = ReadU32LE(_data, pos); pos += 4;  // endian check

        uint languagesSectorSize = ReadU32LE(_data, pos); pos += 4;
        uint banksSectorSize = ReadU32LE(_data, pos); pos += 4;
        uint soundsSectorSize = ReadU32LE(_data, pos); pos += 4;

        uint externalsSectorSize = 0;
        if (languagesSectorSize + banksSectorSize + soundsSectorSize + 0x10 < headerSize)
        {
            externalsSectorSize = ReadU32LE(_data, pos); pos += 4;
        }

        // languages 段
        ParseLanguages(pos, languagesSectorSize);
        pos += (int)languagesSectorSize;

        // banks 段（内含 BNK → DIDX → WEM）
        ParseSector(pos, banksSectorSize, isSounds: false, isExternals: false);
        pos += (int)banksSectorSize;

        // sounds 段（直接 WEM 条目）
        ParseSector(pos, soundsSectorSize, isSounds: true, isExternals: false);
        pos += (int)soundsSectorSize;

        // externals 段
        ParseSector(pos, externalsSectorSize, isSounds: true, isExternals: true);
    }

    private void ParseLanguages(int sectorStart, uint sectorSize)
    {
        if (sectorSize == 0) return;

        int pos = sectorStart;
        uint langCount = ReadU32LE(_data, pos); pos += 4;

        for (int i = 0; i < langCount; i++)
        {
            uint langOffset = ReadU32LE(_data, pos); pos += 4;
            uint langId = ReadU32LE(_data, pos); pos += 4;

            int stringPos = sectorStart + (int)langOffset;
            // 检测 UTF-8 vs UTF-16
            string langName;
            if (stringPos + 2 <= _data.Length &&
                (_data[stringPos] == 0 || _data[stringPos + 1] == 0))
            {
                // UTF-16 LE
                var chars = new List<char>();
                int p = stringPos;
                while (p + 1 < _data.Length)
                {
                    char c = (char)(_data[p] | (_data[p + 1] << 8));
                    if (c == '\0') break;
                    chars.Add(c);
                    p += 2;
                }
                langName = new string(chars.ToArray());
            }
            else
            {
                // UTF-8 / ASCII，截断到 null
                int end = stringPos;
                int maxEnd = Math.Min(stringPos + 16, _data.Length);
                while (end < maxEnd && _data[end] != 0) end++;
                langName = System.Text.Encoding.UTF8.GetString(_data, stringPos, end - stringPos);
            }

            Languages[langId] = langName;
        }
    }

    private void ParseSector(int sectorStart, uint sectorSize, bool isSounds, bool isExternals)
    {
        if (sectorSize == 0) return;

        int pos = sectorStart;
        uint fileCount = ReadU32LE(_data, pos); pos += 4;
        if (fileCount == 0) return;

        uint entrySize = (sectorSize - 4) / fileCount;
        bool altMode = entrySize == 0x18;

        for (int i = 0; i < fileCount; i++)
        {
            ulong fileIdLow = ReadU32LE(_data, pos);
            ulong? fileIdHigh = null;

            int p = pos + 4;
            if (altMode && isExternals)
            {
                fileIdHigh = ReadU32LE(_data, p);
                p += 4;
            }

            uint blockSize = ReadU32LE(_data, p); p += 4;

            ulong size;
            if (altMode && isExternals)
            {
                size = ReadU32LE(_data, p); p += 4;
            }
            else if (altMode)
            {
                size = ReadU64LE(_data, p); p += 8;
            }
            else
            {
                size = ReadU32LE(_data, p); p += 4;
            }

            ulong offset = ReadU32LE(_data, p); p += 4;
            uint langId = ReadU32LE(_data, p); p += 4;

            if (blockSize != 0)
                offset *= blockSize;

            string? language = Languages.TryGetValue(langId, out var lang) ? lang : null;

            ulong finalId = fileIdHigh.HasValue
                ? ((fileIdHigh.Value << 32) | fileIdLow)
                : fileIdLow;

            if (!isSounds)
            {
                // banks 段：解析 BNK 内的 DIDX/Data 子段，提取 WEM
                int bnkStart = (int)offset;
                int bnkEnd = bnkStart + (int)size;
                foreach (var (wemId, wemOffset, wemSize) in ParseBnk(bnkStart, bnkEnd))
                {
                    Entries.Add(new WemEntry
                    {
                        Id = wemId,
                        Offset = offset + wemOffset,
                        Size = wemSize,
                        Language = language,
                    });
                }
            }
            else
            {
                // sounds/externals 段：直接是 WEM
                Entries.Add(new WemEntry
                {
                    Id = finalId,
                    Offset = offset,
                    Size = size,
                    Language = language,
                });
            }

            pos += (int)entrySize;
        }
    }

    /// <summary>
    /// 解析 BNK 的 DIDX 段，返回 (wemId, wemDataOffset, wemSize) 三元组列表。
    /// wemDataOffset 已加上 DATA 段头偏移。
    /// </summary>
    private List<(uint Id, uint Offset, uint Size)> ParseBnk(int start, int end)
    {
        var result = new List<(uint, uint, uint)>();
        if (end - start < 8) return result;
        if (!BytesEqual(_data, start, BkhdMagic, 0, 4)) return result;

        uint bkhdSize = ReadU32LE(_data, start + 4);
        int pos = start + 8 + (int)bkhdSize;
        if (pos >= end) return result;

        // 找 DIDX 段
        if (pos + 8 > end || !BytesEqual(_data, pos, DidxMagic, 0, 4)) return result;

        uint didxSize = ReadU32LE(_data, pos + 4);
        int nWems = (int)(didxSize / 12);
        int didxEntriesStart = pos + 8;

        // 找 DATA 段（紧跟 DIDX 之后）
        int dataSectionStart = didxEntriesStart + (int)didxSize;
        if (dataSectionStart + 8 > end || !BytesEqual(_data, dataSectionStart, DataMagic, 0, 4))
            return result;

        int dataOffset = dataSectionStart + 8;

        for (int i = 0; i < nWems; i++)
        {
            int p = didxEntriesStart + i * 12;
            uint wemId = ReadU32LE(_data, p);
            uint wemOffset = ReadU32LE(_data, p + 4);
            uint wemSize = ReadU32LE(_data, p + 8);
            result.Add((wemId, (uint)dataOffset + wemOffset, wemSize));
        }

        return result;
    }

    /// <summary>
    /// 提取单个 WEM 的字节数据。若不是 RIFF/RIFX 头则用 wem_id 解密。
    /// </summary>
    public byte[] GetWemData(WemEntry entry)
    {
        int start = (int)entry.Offset;
        int end = start + (int)entry.Size;
        if (start < 0 || end > _data.Length)
            throw new InvalidDataException($"WEM 越界: offset={start}, size={entry.Size}, data 长度={_data.Length}");
        var data = new byte[entry.Size];
        Array.Copy(_data, start, data, 0, data.Length);

        if (data.Length >= 4 &&
            !BytesEqual(data, 0, RiffMagic, 0, 4) &&
            !BytesEqual(data, 0, RifxMagic, 0, 4))
        {
            AkpkCrypto.DecryptWem(data, (uint)entry.Id);
        }

        return data;
    }

    private static uint ReadU32LE(byte[] data, int offset)
    {
        if ((uint)offset + 4 > (uint)data.Length)
            throw new InvalidDataException($"读取越界: offset={offset}, 需要 4 字节, 剩余 {data.Length - offset}");
        return (uint)(data[offset] | (data[offset + 1] << 8) |
                      (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static ulong ReadU64LE(byte[] data, int offset)
    {
        if ((uint)offset + 8 > (uint)data.Length)
            throw new InvalidDataException($"读取越界: offset={offset}, 需要 8 字节, 剩余 {data.Length - offset}");
        return (ulong)ReadU32LE(data, offset) | ((ulong)ReadU32LE(data, offset + 4) << 32);
    }

    private static void WriteU32LE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value & 0xFF);
        data[offset + 1] = (byte)((value >> 8) & 0xFF);
        data[offset + 2] = (byte)((value >> 16) & 0xFF);
        data[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static bool BytesEqual(byte[] a, int aOffset, byte[] b, int bOffset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (aOffset + i >= a.Length) return false;
            if (a[aOffset + i] != b[bOffset + i]) return false;
        }
        return true;
    }
}
