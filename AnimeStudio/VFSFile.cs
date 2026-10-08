using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AnimeStudio
{
    public class VFSFile
    {
        private List<BundleFile.StorageBlock> m_BlocksInfo;
        private List<BundleFile.Node> m_DirectoryInfo;

        public BundleFile.Header m_Header;
        public List<StreamFile> fileList;
        public long Offset;
        private readonly long allocationBudget;

        public VFSFile(FileReader reader, string path, GameType game, long allocationBudgetBytes = 0)
        {
            allocationBudget = allocationBudgetBytes > 0 ? allocationBudgetBytes : 512L * 1024 * 1024;
            Offset = reader.Position;
            reader.Endian = EndianType.BigEndian;


            if (!VFSUtils.IsValidHeader(reader, game))
            {
                throw new Exception("Not a VFS file / VFS version mismatch");
            }

            // read header
            reader.ReadBytes(8);
            m_Header = VFSUtils.ReadHeader(reader, game);
            Logger.Verbose($"Header : {m_Header.ToString()}");

            // Sanity check: VFS header 字段经过 descramble 后应该合理
            // 文件才 6 KB 但 descramble 出 uncompressedBlocksInfoSize=14 GB → key/常量不匹配
            const uint MaxVFSBlocksInfoSize = 64 * 1024 * 1024;  // 64 MB
            if (m_Header.uncompressedBlocksInfoSize > MaxVFSBlocksInfoSize ||
                m_Header.compressedBlocksInfoSize > MaxVFSBlocksInfoSize)
            {
                throw new IOException(
                    $"VFS header sanity check failed: uncompressedBlocksInfoSize={m_Header.uncompressedBlocksInfoSize:N0} " +
                    $"compressedBlocksInfoSize={m_Header.compressedBlocksInfoSize:N0} " +
                    $"(file size={reader.BaseStream.Length:N0}). Likely VFS key/constant mismatch.");
            }
            CheckAllocation(3L * m_Header.uncompressedBlocksInfoSize + m_Header.compressedBlocksInfoSize, path);

            // go to blocks info
            uint blockInfosOffset;

            if ((m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0)
                blockInfosOffset = (uint)(m_Header.size) - m_Header.compressedBlocksInfoSize;
            else
            {
                if (m_Header.encFlags >= 7)
                    blockInfosOffset = 48;
                else
                    blockInfosOffset = 40;
            }

            if ((long)blockInfosOffset + m_Header.compressedBlocksInfoSize > reader.BaseStream.Length - Offset)
                throw new IOException("VFS block metadata is outside its declared file range");
            reader.Position = Offset + blockInfosOffset;
            ReadBlocksInfoAndDirectory(reader, game);

            // go to data
            uint dataOffset;

            if (m_Header.encFlags >= 7)
                dataOffset = 48;
            else
                dataOffset = 40;
            if (((m_Header.flags) & ArchiveFlags.BlocksInfoAtTheEnd) == 0)
            {
                var temp = m_Header.compressedBlocksInfoSize;
                if (((m_Header.flags) & ArchiveFlags.BlockInfoNeedPaddingAtStart) != 0)
                    temp = (temp + 15) & 0xFFFFFFF0;
                dataOffset += temp;
            }

            reader.Position = Offset + dataOffset;

            // The scrambled declared size is not the physical extracted extent in
            // every Endfield revision. The supplied VFS slice is the authoritative
            // bound; only end-positioned block metadata uses the header offset.
            long dataEnd = (m_Header.flags & ArchiveFlags.BlocksInfoAtTheEnd) != 0 ? blockInfosOffset : reader.BaseStream.Length - Offset;
            ValidateLayout(dataOffset, dataEnd, path);

            //
            using var blocksStream = CreateBlocksStream(path);
            ReadBlocks(reader, blocksStream, game);
            ReadFiles(blocksStream, path);
        }

        private void ReadBlocksInfoAndDirectory(FileReader reader, GameType game)
        {
            byte[] blocksInfoBytes = reader.ReadBytes((int)m_Header.compressedBlocksInfoSize);
            if (blocksInfoBytes.Length != m_Header.compressedBlocksInfoSize)
                throw new EndOfStreamException("Truncated VFS block metadata");

            MemoryStream blocksInfoUncompressedStream = new MemoryStream();
            if (((int)m_Header.flags & 0x3F) != 0)
            {
                // compressed + encrypted
                VFSUtils.DecryptBlock(blocksInfoBytes, game);

                var uncompressedSize = m_Header.uncompressedBlocksInfoSize;
                var blocksInfoBytesSpan = blocksInfoBytes.AsSpan(0, blocksInfoBytes.Length);
                var uncompressedBytes = ArrayPool<byte>.Shared.Rent((int)uncompressedSize);

                try
                {
                    var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, (int)uncompressedSize);
                    // normal LZ4
                    var numWrite = LZ4.Instance.Decompress(blocksInfoBytesSpan, uncompressedBytesSpan);

                    if (numWrite != uncompressedSize)
                    {
                        throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                    }
                    blocksInfoUncompressedStream = new MemoryStream(uncompressedBytesSpan.ToArray());
                } catch (Exception e)
                {
                    throw new IOException($"Lz4 decompression error {e.Message}");
                } finally
                {
                    ArrayPool<byte>.Shared.Return(uncompressedBytes);
                }
            } else
            {
                blocksInfoUncompressedStream = new MemoryStream(blocksInfoBytes);
            }

            // read
            using (var blocksInfoReader = new EndianBinaryReader(blocksInfoUncompressedStream))
            {
                blocksInfoReader.Endian = EndianType.BigEndian;
                m_BlocksInfo = VFSUtils.ReadBlocksInfos(blocksInfoReader, game);
                m_DirectoryInfo = VFSUtils.ReadDirectoryInfos(blocksInfoReader, game);
            }
        }

        private void CheckAllocation(long expectedBytes, string path)
        {
            if (expectedBytes > allocationBudget)
                throw new AllocBudgetExceededException(expectedBytes, allocationBudget, path);
        }

        private void ValidateLayout(long dataStart, long dataEnd, string path)
        {
            if (dataStart < 0 || dataEnd < dataStart)
                throw new IOException("VFS data range is invalid");
            long compressed = 0, uncompressed = 0, largestBuffers = 0, nodes = 0;
            foreach (var block in m_BlocksInfo)
            {
                if ((int)block.flags is not (0 or 5))
                    throw new IOException($"Unsupported VFS block compression type {(int)block.flags}");
                if ((int)block.flags == 0 && block.compressedSize != block.uncompressedSize)
                    throw new IOException("Uncompressed VFS block sizes differ");
                if (block.compressedSize > int.MaxValue || block.uncompressedSize > int.MaxValue)
                    throw new IOException("VFS block exceeds supported buffer size");
                compressed += block.compressedSize;
                uncompressed += block.uncompressedSize;
                // ArrayPool can round each requested buffer up to the next power of two.
                largestBuffers = Math.Max(largestBuffers, 2L * (block.compressedSize + (long)block.uncompressedSize));
            }
            if (compressed > dataEnd - dataStart)
                throw new IOException("VFS compressed blocks exceed the available data range");
            if (uncompressed >= 2L * 1024 * 1024 * 1024)
                throw new IOException("VFS decompressed blocks exceed the 2 GB limit");
            foreach (var node in m_DirectoryInfo)
            {
                if (node.offset < 0 || node.size < 0 || node.offset > uncompressed || node.size > uncompressed - node.offset)
                    throw new IOException($"VFS node is outside the decompressed data: {node.path}");
                if (node.size > 512L * 1024 * 1024)
                    throw new IOException($"VFS node exceeds the 512 MB limit: {node.path}");
                nodes += node.size;
            }
            // Check before MemoryStream/ArrayPool allocations, including the copies retained
            // for embedded files. Compression ratio is not a corruption test: valid highly
            // repetitive shaders/prefabs routinely exceed the previous arbitrary 20x limit.
            CheckAllocation(uncompressed + nodes + largestBuffers, path);
        }

        private Stream CreateBlocksStream(string path)
        {
            Stream blocksStream;
            long uncompressedSizeSum = m_BlocksInfo.Sum(x => (long)x.uncompressedSize);
            Logger.Verbose($"Total size of decompressed blocks: 0x{uncompressedSizeSum:X8}");
            // Layout validation above checks compressed ranges and allocation budget first.
            const long MaxBlocksStreamSize = 2L * 1024 * 1024 * 1024;  // 2 GB
            if (uncompressedSizeSum >= MaxBlocksStreamSize)
                throw new IOException($"VFS blocks stream too large: {uncompressedSizeSum:N0} bytes (max {MaxBlocksStreamSize:N0})");
            blocksStream = new MemoryStream((int)uncompressedSizeSum);
            return blocksStream;
        }

        private void ReadBlocks(FileReader reader, Stream blocksStream, GameType game)
        {
            foreach (var blockInfo in m_BlocksInfo)
            {
                var compressionType = (int)blockInfo.flags; // no mask
                Logger.Verbose($"Block compression type {compressionType}");

                switch (compressionType)
                {
                    case 0:
                        var size = (int)blockInfo.uncompressedSize;
                        var buffer = reader.ReadBytes(size);
                        if (buffer.Length != size) throw new EndOfStreamException("Truncated uncompressed VFS block");
                        blocksStream.Write(buffer);
                        break;
                    case 5:
                        var compressedSize = (int)blockInfo.compressedSize;
                        var uncompressedSize = (int)blockInfo.uncompressedSize;

                        var compressedBytes = ArrayPool<byte>.Shared.Rent(compressedSize);
                        var uncompressedBytes = ArrayPool<byte>.Shared.Rent(uncompressedSize);

                        var compressedBytesSpan = compressedBytes.AsSpan(0, compressedSize);
                        var uncompressedBytesSpan = uncompressedBytes.AsSpan(0, uncompressedSize);

                        try
                        {
                            reader.BaseStream.ReadExactly(compressedBytesSpan);

                            VFSUtils.DecryptBlock(compressedBytesSpan, game);

                            // LZ4Inv this time
                            var numWrite = LZ4Inv.Instance.Decompress(compressedBytesSpan, uncompressedBytesSpan);
                            if (numWrite != uncompressedSize)
                            {
                                throw new IOException($"Lz4 decompression error, write {numWrite} bytes but expected {uncompressedSize} bytes");
                            }
                            blocksStream.Write(uncompressedBytesSpan);
                        }
                        catch (Exception e)
                        {
                            throw new IOException($"VFS Lz4 decompression failed: {e.Message}", e);
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(compressedBytes, true);
                            ArrayPool<byte>.Shared.Return(uncompressedBytes, true);
                        }

                        break;
                    default:
                        throw new Exception($"Unsupported block compression type {compressionType}");
                }
            }
        }

        private void ReadFiles(Stream blocksStream, string path)
        {
            Logger.Verbose($"Writing files from blocks stream...");

            fileList = new List<StreamFile>();
            for (int i = 0; i < m_DirectoryInfo.Count; i++)
            {
                var node = m_DirectoryInfo[i];
                var file = new StreamFile();
                file.path = node.path;
                file.fileName = Path.GetFileName(node.path);
                if (node.size >= int.MaxValue || node.size > 512 * 1024 * 1024)  // 512 MB per file
                {
                    throw new IOException($"VFS node size too large: {node.size:N0} bytes for file {node.path}");
                }
                else
                    file.stream = new MemoryStream((int)node.size);
                blocksStream.Position = node.offset;
                blocksStream.CopyTo(file.stream, node.size);
                file.stream.Position = 0;
                fileList.Add(file);
            }
        }
    }
}
