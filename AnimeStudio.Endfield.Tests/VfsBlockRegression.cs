using System.Reflection;
using System.Runtime.CompilerServices;
using AS = AnimeStudio;

internal static class VfsBlockRegression
{
    public static void Run()
    {
        // One literal followed by a 4095-byte match, encoded in Endfield's LZ4Inv
        // token layout. A legitimate 200x expansion used to fail the 20x guess.
        var encoded = new List<byte> { 0xCD, (byte)'A', 0, 1 };
        int extension = 4095 - 19;
        while (extension >= 255) { encoded.Add(255); extension -= 255; }
        encoded.Add((byte)extension);
        byte[] compressed = Encrypt(encoded.ToArray());
        var file = Fixture((uint)compressed.Length, 4096, 5);
        Layout(file, compressed.Length);
        using (var output = Decode(file, compressed))
            Check(output.Length == 4096 && output.ToArray().All(value => value == (byte)'A'),
                "Highly compressed valid VFS blocks must decode exactly");

        Reject(() => Layout(file, compressed.Length - 1), "Compressed range must remain inside source data");
        var huge = Fixture(20, 1_000_000_000, 5);
        Reject(() => Layout(huge, 20), "Allocation budget must reject huge headers before MemoryStream or pool allocation");
        var invalidNode = Fixture(20, 4096, 5, nodeOffset: 4095, nodeSize: 2);
        Reject(() => Layout(invalidNode, 20), "Node ranges cannot extend beyond decoded bytes");
        var negativeNode = Fixture(20, 4096, 5, nodeOffset: -1);
        Reject(() => Layout(negativeNode, 20), "Negative node offsets cannot be accepted");

        byte[] shortLz4 = Encrypt(new byte[] { 1, (byte)'A' });
        var shortFile = Fixture((uint)shortLz4.Length, 4096, 5);
        using var noOutput = new MemoryStream();
        Reject(() => DecodeTo(shortFile, shortLz4, noOutput), "Short decoder output must be an error");
        Check(noOutput.Length == 0, "Failed decompression must never publish uninitialized ArrayPool bytes");
        var raw = Fixture(8, 8, 0);
        using var rawOutput = new MemoryStream();
        Reject(() => DecodeTo(raw, new byte[4], rawOutput), "Short uncompressed blocks must not be silently accepted");
        Check(rawOutput.Length == 0, "Truncated raw blocks must not publish partial bytes");

        // PreProcessing can leave a correctly identified VFS file unchanged.
        // The dispatcher must reach the VFS parser, including its strict errors,
        // rather than silently returning an empty manager for that file type.
        var manager = new AS.AssetsManager {
            Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield), ThrowOnLoadError = true };
        using var badVfs = new AS.FileReader("dispatcher-fixture.ab", new MemoryStream(new byte[512]));
        badVfs.FileType = AS.FileType.VFSFile;
        bool dispatched = false;
        try
        {
            typeof(AS.AssetsManager).GetMethod("LoadFile", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: new[] { typeof(AS.FileReader) }, modifiers: null)!.Invoke(manager, new object[] { badVfs });
        }
        catch (TargetInvocationException exception) when (exception.InnerException?.Message == "Not a VFS file / VFS version mismatch")
        { dispatched = true; }
        Check(dispatched, "Identified VFS input must be dispatched to its parser, never silently ignored");
        ProtectedBlocks();

        Console.WriteLine("PASS: high-ratio VFS LZ4, source/node bounds, preallocation budget, truncated input, strict decoding, and explicit VFS dispatch");
    }


    // 1.7.2 起，块索引里 flags 带 0x80 的块会多一个 uint32；数据区是受保护的密文。
    // 解析时少读这 4 字节会让后续块整体错位（曾表现为 nodesCount too large）。
    private static void ProtectedBlocks()
    {
        uint Cat(ushort hi, ushort lo) => ((uint)hi << 16) | lo;
        uint RotR32(uint v, int c) => (v >> c) | (v << (32 - c));

        var payload = new List<byte>();
        // 块记录按大端读（ReadBlocksInfos 只对计数部分临时切到小端），额外字段同样按原端序读。
        void U16(ushort value) => payload.AddRange(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value)));
        void U32be(uint value) => payload.AddRange(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value)));

        const uint blocksCount = 2;
        // blocksCount = RotR(Cat(lo^hi, lo), 18) ^ 0x91CE0A4F  →  Cat = RotL(count ^ key, 18)
        uint mixed = blocksCount ^ 0x91CE0A4F;
        uint catValue = (mixed << 18) | (mixed >> 14);
        ushort lo = (ushort)catValue;
        ushort hi = (ushort)((catValue >> 16) ^ lo);
        uint encCount = ((uint)hi << 16) | lo;
        uint rawCount = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(encCount) ^ 0x8A7BF723;
        payload.AddRange(BitConverter.GetBytes(rawCount));

        // flags = C ^ RotL(flags0, 14) ^ 0x523F，取 a0 = a1 = 0 → flags0 = 0
        ushort C0 = (ushort)(0x0000 ^ 0x523F);
        ushort C1 = (ushort)(0x0080 ^ 0x523F);
        U16(0x1111); U16(0x2222); U16(C0); U16(0x9CD6); U16(0x3333);
        uint expectedUs0 = RotR32(Cat((ushort)(0x1111 ^ C0 ^ 0xA121), C0), 18) ^ 0xF74324EE;
        uint expectedCs0 = RotR32(Cat((ushort)(0x2222 ^ 0x3333 ^ 0xA121), 0x3333), 18) ^ 0xF74324EE;

        U16(0x4444); U16(0x5555); U16(C1); U16(0x9CD6); U16(0x6666);
        uint expectedUs1 = RotR32(Cat((ushort)(0x4444 ^ C1 ^ 0xA121), C1), 18) ^ 0xF74324EE;
        uint expectedCs1 = RotR32(Cat((ushort)(0x5555 ^ 0x6666 ^ 0xA121), 0x6666), 18) ^ 0xF74324EE;
        U32be(0xDEADBEEFu);

        using var infoReader = new AS.FileReader("blocks-info", new MemoryStream(payload.ToArray()));
        infoReader.Endian = AS.EndianType.BigEndian;
        var blocks = AS.VFSUtils.ReadBlocksInfos(infoReader, AS.GameType.ArknightsEndfield);
        Check(blocks.Count == 2, "Two blocks must be decoded from the index");
        Console.WriteLine($"DBG b1 flags={(int)blocks[1].flags} extra={blocks[1].extra} us={blocks[1].uncompressedSize} expUs={expectedUs1} cs={blocks[1].compressedSize} expCs={expectedCs1}");
        Check((int)blocks[0].flags == 0 && blocks[0].uncompressedSize == expectedUs0
              && blocks[0].compressedSize == expectedCs0, "Plain block fields must stay byte-exact");
        Check(((int)blocks[1].flags & 0x80) != 0 && blocks[1].extra == 0xDEADBEEF,
            "Protected block must consume its trailing uint32 instead of shifting the stream");
        Check(blocks[1].uncompressedSize == expectedUs1 && blocks[1].compressedSize == expectedCs1,
            "Protected block sizes must decode exactly like plain blocks");

        var file = (AS.VFSFile)RuntimeHelpers.GetUninitializedObject(typeof(AS.VFSFile));
        void Field(string name, object value) => typeof(AS.VFSFile).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(file, value);
        Field("allocationBudget", 1024L * 1024);
        Field("m_BlocksInfo", new List<AS.BundleFile.StorageBlock> {
            new() { compressedSize = 8, uncompressedSize = 8, flags = (AS.StorageBlockFlags)0 },
            new() { compressedSize = 32, uncompressedSize = 4096, flags = (AS.StorageBlockFlags)0x80, extra = 1 },
        });
        Field("m_DirectoryInfo", new List<AS.BundleFile.Node> { new() { offset = 0, size = 4104, path = "CAB-fixture" } });
        Layout(file, 40);
        var source = new byte[40];
        Array.Fill(source, (byte)'A', 0, 8);
        using var output = Decode(file, source);
        Check(output.Length == 8 + 4096, "Protected blocks must still occupy their declared size");
        Check(output.ToArray().Take(8).All(b => b == (byte)'A'), "Plain block bytes must be preserved");
        Check(output.ToArray().Skip(8).All(b => b == 0), "Protected block bytes must be zero-filled");
        Console.WriteLine("PASS: 0x80 protected blocks (extra uint32 in the index, zero-filled data, correct layout)");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException e) when (e.InnerException is IOException or AS.AllocBudgetExceededException) { return; }
        throw new InvalidOperationException(message);
    }

    private static AS.VFSFile Fixture(uint compressed, uint uncompressed, int flags, long nodeOffset = 0, long? nodeSize = null)
    {
        var file = (AS.VFSFile)RuntimeHelpers.GetUninitializedObject(typeof(AS.VFSFile));
        void Field(string name, object value) => typeof(AS.VFSFile).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(file, value);
        Field("allocationBudget", 1024L * 1024);
        Field("m_BlocksInfo", new List<AS.BundleFile.StorageBlock> { new() {
            compressedSize = compressed, uncompressedSize = uncompressed, flags = (AS.StorageBlockFlags)flags } });
        Field("m_DirectoryInfo", new List<AS.BundleFile.Node> { new() { offset = nodeOffset, size = nodeSize ?? uncompressed, path = "CAB-fixture" } });
        return file;
    }

    private static void Layout(AS.VFSFile file, long bytes) => typeof(AS.VFSFile)
        .GetMethod("ValidateLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(file, new object[] { 0L, bytes, "fixture.ab" });

    private static byte[] Encrypt(byte[] bytes)
    {
        // VFS uses a ciphertext-independent XOR keystream, so the same operation
        // encodes a controlled test payload and decrypts it during ReadBlocks.
        AS.VFSUtils.DecryptBlock(bytes, AS.GameType.ArknightsEndfield);
        return bytes;
    }

    private static MemoryStream Decode(AS.VFSFile file, byte[] compressed)
    {
        var output = new MemoryStream();
        DecodeTo(file, compressed, output);
        return output;
    }

    private static void DecodeTo(AS.VFSFile file, byte[] bytes, Stream output)
    {
        using var source = new MemoryStream(new byte[Math.Max(512, bytes.Length)], writable: true);
        using var reader = new AS.FileReader("fixture.ab", source);
        source.Position = 0;
        source.Write(bytes);
        source.SetLength(bytes.Length);
        source.Position = 0;
        typeof(AS.VFSFile).GetMethod("ReadBlocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(file, new object[] { reader, output, AS.GameType.ArknightsEndfield });
    }
}
