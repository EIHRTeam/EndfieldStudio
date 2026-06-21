using System.Buffers.Binary;

namespace AnimeStudio.Endfield;

/// <summary>
/// VFS block loader: reads encrypted .blc and .chk files from the
/// StreamingAssets/VFS folder. Faithful port of fluffy-dumper/vfs/src/loader.rs.
/// </summary>
public sealed class VfsLoader
{
    private const string VfsDir = "VFS";

    private readonly string _vfsPath;
    private readonly byte[] _chachaKey;

    public VfsLoader(string streamingAssetsPath, byte[] chacha20Key)
    {
        if (streamingAssetsPath is null) throw new ArgumentNullException(nameof(streamingAssetsPath));
        if (chacha20Key is null) throw new ArgumentNullException(nameof(chacha20Key));
        if (chacha20Key.Length != 32)
            throw new ArgumentException("ChaCha20 key must be 32 bytes", nameof(chacha20Key));

        _vfsPath = Path.Combine(streamingAssetsPath, VfsDir);
        _chachaKey = (byte[])chacha20Key.Clone();
    }

    public string VfsPath => _vfsPath;

    public BlockMainInfo LoadBlockInfo(BlockType bt)
    {
        string dirName = BlockHashes.GetDirName(bt);
        string blockDir = Path.Combine(_vfsPath, dirName);

        if (!Directory.Exists(blockDir))
        {
            throw new DirectoryNotFoundException($"block directory not found: {dirName}");
        }

        string blockFilePath = Path.Combine(blockDir, dirName + ".blc");
        byte[] blockData = File.ReadAllBytes(blockFilePath);

        if (blockData.Length < Keys.BlockHeadLen)
        {
            throw new InvalidDataException("block file too short");
        }

        Span<byte> nonce = stackalloc byte[Keys.BlockHeadLen];
        blockData.AsSpan(0, Keys.BlockHeadLen).CopyTo(nonce);

        int payloadLen = blockData.Length - Keys.BlockHeadLen;
        var decrypted = new byte[payloadLen];
        Buffer.BlockCopy(blockData, Keys.BlockHeadLen, decrypted, 0, payloadLen);

        var cipher = new ChaCha20(_chachaKey, nonce, 1);
        cipher.ApplyKeystream(decrypted);

        return VfsParser.Parse(decrypted, verifyCrc: true);
    }

    public long ExtractFile(BlockType bt, ChunkInfo chunk, FileInfo file, Stream writer)
    {
        if (chunk is null) throw new ArgumentNullException(nameof(chunk));
        if (file is null) throw new ArgumentNullException(nameof(file));
        if (writer is null) throw new ArgumentNullException(nameof(writer));

        string dirName = BlockHashes.GetDirName(bt);
        string chunkPath = Path.Combine(_vfsPath, dirName, chunk.FileName());

        if (!File.Exists(chunkPath))
        {
            throw new FileNotFoundException($"chunk file not found: {chunk.FileName()}", chunkPath);
        }

        using var stream = new FileStream(
            chunkPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: false);

        stream.Seek(file.Offset, SeekOrigin.Begin);

        long bytesWritten = 0;
        long fileLen = file.Length;
        var buffer = new byte[64 * 1024];

        if (file.UseEncrypt)
        {
            Span<byte> nonce = stackalloc byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(nonce.Slice(0, 4), Keys.VfsProtoVersion);
            BinaryPrimitives.WriteInt64LittleEndian(nonce.Slice(4, 8), file.IvSeed);

            var cipher = new ChaCha20(_chachaKey, nonce, 1);

            long remaining = fileLen;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = stream.Read(buffer, 0, toRead);
                if (read == 0) break;

                cipher.ApplyKeystream(buffer.AsSpan(0, read));
                writer.Write(buffer, 0, read);
                bytesWritten += read;
                remaining -= read;
            }
        }
        else
        {
            long remaining = fileLen;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = stream.Read(buffer, 0, toRead);
                if (read == 0) break;

                writer.Write(buffer, 0, read);
                bytesWritten += read;
                remaining -= read;
            }
        }

        return bytesWritten;
    }

    public byte[] ExtractFileToBytes(BlockType bt, ChunkInfo chunk, FileInfo file)
    {
        using var ms = new MemoryStream(checked((int)Math.Max(0, file.Length)));
        ExtractFile(bt, chunk, file, ms);
        return ms.ToArray();
    }
}
