using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AnimeStudio.Endfield.Cli;

internal static class OriginalManifestRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        Fixture fixture = Build();
        var manifest = Load(fixture.Body);
        Check(manifest.PathsByBundle.Count == 2, "Both bundle tables must survive parsing");
        Check(manifest.GetAssetPaths("Data\\Bundles\\Windows\\MAIN\\fixture.ab").Count == 3,
            "VFS prefix, Windows separators, and bundle case must resolve consistently");
        Check(manifest.TryResolve("main/fixture.ab", "T_UNIQUE", out string unique) &&
            unique == "assets/美术/t_unique.tga", "Unique Unicode source paths must be retained exactly");
        Check(!manifest.TryResolve("main/fixture.ab", "same", out _),
            "Same-named assets in distinct folders must not be guessed");
        Check(manifest.TryResolve("initial/other.ab", "same", out string other) &&
            other == "packages/render/mesh/same.fbx", "Name matching must be scoped to the selected bundle");
        Check(!manifest.TryResolve("main/missing.ab", "same", out _) &&
            !manifest.TryResolve("main/fixture.ab", "absent", out _) &&
            !manifest.TryResolve("main/fixture.ab", "", out _), "Unknown or empty names have no original path");

        var subassets = Load(Build("assets/美术/model.fbx##MeshPart").Body);
        Check(subassets.TryResolveSubAsset("main/fixture.ab", "meshpart", out string owner, out string part) &&
            owner == "assets/美术/model.fbx" && part == "MeshPart", "Manifest subasset addresses preserve owner path and label");
        Check(!subassets.TryResolve("main/fixture.ab", "model", out _) &&
            !subassets.TryResolveSubAsset("main/fixture.ab", "same", out _, out _),
            "Subasset addresses and physical source paths must remain distinct");
        var ambiguousParts = Load(Build("assets/a/model.fbx##MeshPart", "assets/b/model.fbx##MeshPart").Body);
        Check(!ambiguousParts.TryResolveSubAsset("main/fixture.ab", "MeshPart", out _, out _),
            "Identically named subassets belonging to distinct owners must not be guessed");

        Reject(fixture.Body, body => Write(body, 0, 0), "bad magic");
        Reject(fixture.Body, body => Write(body, fixture.AssetStart + 4, 4), "slot points inside metadata");
        Reject(fixture.Body, body => Write(body, fixture.AssetStart + 12, (uint)(fixture.RowStart - fixture.AssetStart + 24)),
            "overlapping slots");
        Reject(fixture.Body, body => Write(body, fixture.RowStart + 12, 2), "unknown bundle");
        Reject(fixture.Body, body => Write(body, fixture.RowStart + 8, uint.MaxValue), "oversized string offset");
        Reject(fixture.Body, body => Write(body, fixture.BundleStart + 4, 1), "non-contiguous bundle indexes");
        Reject(fixture.Body, body => Write(body, fixture.StringStart, uint.MaxValue), "oversized string length");
        Reject(fixture.Body[..^1], _ => { }, "truncated trailing framing");
        Reject(Build("../escape.tga").Body, _ => { }, "traversal source path");
        Reject(Build("/root/escape.tga").Body, _ => { }, "absolute source path");
        Reject(Build("C:/escape.tga").Body, _ => { }, "drive-qualified source path");
        Console.WriteLine("PASS: original BundleManifest paths, slotted records, Unicode, ambiguity, corrupt indexes, and path containment");
    }

    private static OriginalAssetManifest Load(byte[] raw)
    {
        using var compressed = new MemoryStream(Compress(raw));
        return OriginalAssetManifest.Load(compressed);
    }

    private static void Reject(byte[] raw, Action<byte[]> change, string label)
    {
        byte[] copy = (byte[])raw.Clone();
        change(copy);
        try { _ = Load(copy); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Malformed manifest was accepted: " + label);
    }

    private static byte[] Compress(byte[] value)
    {
        using var memory = new MemoryStream();
        using (var brotli = new BrotliStream(memory, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(value);
        return memory.ToArray();
    }

    private static void Write(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private sealed record Fixture(byte[] Body, int AssetStart, int RowStart, int BundleStart, int StringStart);

    private static Fixture Build(string unique = "assets/美术/t_unique.tga", string second = "assets/a/same.png")
    {
        string[] paths = [unique, second, "assets/b/same.tga", "packages/render/mesh/same.fbx"];
        string[] names = ["main/fixture.ab", "initial/other.ab"];
        using var strings = new MemoryStream();
        using var stringWriter = new BinaryWriter(strings, Encoding.UTF8, leaveOpen: true);
        var nameOffsets = new List<int>();
        foreach (string name in names)
        {
            nameOffsets.Add((int)strings.Position);
            byte[] bytes = Encoding.Unicode.GetBytes(name);
            stringWriter.Write(bytes.Length);
            stringWriter.Write(bytes);
        }
        var pathOffsets = new List<int>();
        foreach (string path in paths)
        {
            pathOffsets.Add((int)strings.Position);
            byte[] bytes = Compress(Encoding.Unicode.GetBytes(path));
            stringWriter.Write(bytes.Length);
            stringWriter.Write(bytes);
        }

        using var assets = new MemoryStream();
        using var assetWriter = new BinaryWriter(assets, Encoding.UTF8, leaveOpen: true);
        assetWriter.Write(paths.Length);
        int firstRow = 4 + paths.Length * 8;
        // The hash slots can be sparse and their ranges need not be sorted.
        assetWriter.Write(firstRow + 3 * 24); assetWriter.Write(1);
        assetWriter.Write(firstRow); assetWriter.Write(3);
        assetWriter.Write(0); assetWriter.Write(0);
        assetWriter.Write(0); assetWriter.Write(0);
        for (int index = 0; index < paths.Length; index++)
        {
            assetWriter.Write((ulong)(123 + index));
            assetWriter.Write(pathOffsets[index]);
            assetWriter.Write(index == 3 ? 1 : 0);
            assetWriter.Write(0); assetWriter.Write(0);
        }

        using var bundles = new MemoryStream();
        using var bundleWriter = new BinaryWriter(bundles, Encoding.UTF8, leaveOpen: true);
        bundleWriter.Write(names.Length);
        for (int index = 0; index < names.Length; index++)
        {
            bundleWriter.Write(index); bundleWriter.Write(nameOffsets[index]);
            bundleWriter.Write(new byte[40]);
        }

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        void HeaderString(string value) { writer.Write(value.Length); writer.Write(Encoding.Unicode.GetBytes(value)); }
        int Section(byte[] value)
        {
            writer.Write(value.Length);
            int start = (int)output.Position;
            writer.Write(value);
            return start;
        }
        writer.Write(0xFF11FF11u); HeaderString("version");
        writer.Write(0xF1F2F3F4u); HeaderString("Windows"); HeaderString("test");
        int assetStart = Section(assets.ToArray());
        _ = Section([]);
        int bundleStart = Section(bundles.ToArray());
        int stringStart = Section(strings.ToArray());
        writer.Write(0);
        return new Fixture(output.ToArray(), assetStart, assetStart + firstRow, bundleStart, stringStart);
    }
}
