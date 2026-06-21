using System.Buffers.Binary;

namespace AnimeStudio.Endfield;

public sealed class FileInfo
{
    public string FileName { get; init; } = "";
    public long FileNameHash { get; init; }
    public UInt128 FileChunkMd5 { get; init; }
    public UInt128 FileDataMd5 { get; init; }
    public long Offset { get; init; }
    public long Length { get; init; }
    public BlockType BlockType { get; init; }
    public bool UseEncrypt { get; init; }
    public long IvSeed { get; init; }
    public FileTag FileTag { get; init; }
}

public sealed class ChunkInfo
{
    public UInt128 Md5Name { get; init; }
    public UInt128 ContentMd5 { get; init; }
    public long Length { get; init; }
    public BlockType BlockType { get; init; }
    public FileTag MainTag { get; init; }
    public List<FileInfo> Files { get; init; } = new();

    public string FileName()
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(bytes, Md5Name);
        return Convert.ToHexString(bytes) + ".chk";
    }
}

public sealed class BlockMainInfo
{
    public int Version { get; init; }
    public string GroupCfgName { get; init; } = "";
    public long GroupCfgHashName { get; init; }
    public int GroupFileInfoNum { get; init; }
    public long GroupChunksLength { get; init; }
    public BlockType BlockType { get; init; }
    public List<ChunkInfo> Chunks { get; init; } = new();
    public int CodeVersion { get; init; }
}
