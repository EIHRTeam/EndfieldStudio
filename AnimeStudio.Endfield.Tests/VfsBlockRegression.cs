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
        Console.WriteLine("PASS: high-ratio VFS LZ4, source/node bounds, preallocation budget, truncated input, strict decoding, and explicit VFS dispatch");
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
