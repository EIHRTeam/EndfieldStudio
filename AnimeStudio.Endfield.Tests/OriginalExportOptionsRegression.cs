using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Cli;
using CliProgram = AnimeStudio.Endfield.Cli.Program;

internal static class OriginalExportOptionsRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "endfield-original-options-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] payload = MaterialPayload();
            byte[] serialized = SerializedMaterial(payload);
            foreach (var mode in new[] { (Name: "default", Raw: false, Metadata: false),
                (Name: "raw", Raw: true, Metadata: false), (Name: "metadata", Raw: false, Metadata: true),
                (Name: "raw-metadata", Raw: true, Metadata: true) })
            {
                string output = Path.Combine(root, mode.Name);
                string scratch = Path.Combine(root, "scratch-" + mode.Name);
                var sourceType = typeof(CliProgram).GetNestedType("OriginalSource", BindingFlags.NonPublic)!;
                var runType = typeof(CliProgram).GetNestedType("OriginalRun", BindingFlags.NonPublic)!;
                var source = Activator.CreateInstance(sourceType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, args: new object[] { BlockType.Bundle, new ChunkInfo(),
                        new AnimeStudio.Endfield.FileInfo { FileName = "Data/Bundles/Windows/fixture.ab", Length = serialized.Length } }, culture: null)!;
                var run = Activator.CreateInstance(runType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, args: new object[] { output }, culture: null)!;
                using ((IDisposable)run)
                {
                    runType.GetField("KeepRaw")!.SetValue(run, mode.Raw);
                    runType.GetField("KeepMetadata")!.SetValue(run, mode.Metadata);
                    string directory = (string)typeof(CliProgram).GetMethod("OriginalScratchDirectory", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new[] { scratch, source })!;
                    Directory.CreateDirectory(directory);
                    // OriginalBundle also accepts a serialized file directly, avoiding unrelated archive/VFS fixtures.
                    File.WriteAllBytes(Path.Combine(directory, "source.ab"), serialized);
                    typeof(CliProgram).GetMethod("OriginalBundle", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, new object[] { new VfsLoader(root, Keys.ChaCha20Key), source, scratch,
                            64L * 1024 * 1024, Array.Empty<OriginalAssetManifest>(), run });
                    Check((int)runType.GetField("Failures")!.GetValue(run)! == 0, mode.Name + ": export must succeed");
                    Check((int)runType.GetField("Written")!.GetValue(run)! == 1 &&
                        (int)runType.GetField("Bundles")!.GetValue(run)! == 1, mode.Name + ": exactly one material must be exported");
                }

                string[] rows = File.ReadAllLines(Path.Combine(output, "_index.jsonl"));
                Check(rows.Length == 1, mode.Name + ": index must contain exactly one object");
                using var index = JsonDocument.Parse(rows[0]);
                var row = index.RootElement;
                Check(row.GetProperty("status").GetString() == "exported" && row.GetProperty("type").GetString() == "Material" &&
                    row.GetProperty("name").GetString() == "FixtureMaterial" && row.GetProperty("decode_error").ValueKind == JsonValueKind.Null,
                    mode.Name + ": the material must be parsed and exported without a decode error");
                Check(row.GetProperty("source_size").GetUInt32() == payload.Length && row.GetProperty("source_offset").GetInt64() == 256,
                    mode.Name + ": index must retain the exact material source range");
                Check(row.TryGetProperty("details_path", out _), mode.Name + ": index must expose the details sidecar path");
                string? detailsPath = IndexedPath(row, "details_path");
                string? metadataPath = IndexedPath(row, "metadata_path");
                string? rawPath = IndexedPath(row, "raw_path");
                Check((detailsPath is not null) == !mode.Metadata && (metadataPath is not null) == mode.Metadata &&
                    (rawPath is not null) == mode.Raw, mode.Name + ": index paths must match the selected artifacts");
                string detailsFile = Path.Combine(output, detailsPath ?? metadataPath!);
                using var details = JsonDocument.Parse(File.ReadAllText(detailsFile));
                CheckDetails(mode.Metadata ? details.RootElement.GetProperty("details") : details.RootElement, mode.Name);
                if (mode.Metadata)
                {
                    Check(details.RootElement.GetProperty("name").GetString() == "FixtureMaterial" &&
                        IndexedPath(details.RootElement, "raw_path") == rawPath, mode.Name + ": metadata must describe the same material/raw output");
                    Check(!Directory.EnumerateFiles(output, "*.Material.json", SearchOption.AllDirectories).Any(),
                        mode.Name + ": metadata must not produce a duplicate details sidecar");
                }
                if (mode.Raw)
                    Check(File.ReadAllBytes(Path.Combine(output, rawPath!)).SequenceEqual(payload),
                        mode.Name + ": raw material bytes must match the complete serialized payload");
                else
                    Check(!Directory.EnumerateFiles(output, "*.raw", SearchOption.AllDirectories).Any(),
                        mode.Name + ": raw payloads must remain opt-in");
            }
            Console.WriteLine("PASS: original default/raw/metadata/raw+metadata actual output, retained material details, index paths, and exact raw bytes");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void CheckDetails(JsonElement details, string mode)
    {
        Check(details.GetProperty("shader").GetProperty("path_id").GetInt64() == 111,
            mode + ": material shader reference must survive");
        var textures = details.GetProperty("textures");
        Check(textures.GetArrayLength() == 1 && textures[0].GetProperty("slot").GetString() == "_MainTex" &&
            textures[0].GetProperty("file_id").GetInt32() == 0 && textures[0].GetProperty("path_id").GetInt64() == 222 &&
            textures[0].GetProperty("scale").GetProperty("X").GetSingle() == 1.25f &&
            textures[0].GetProperty("offset").GetProperty("Y").GetSingle() == 0.5f,
            mode + ": material texture slots, references, scale, and offset must survive");
        var ints = details.GetProperty("m_Ints");
        var floats = details.GetProperty("m_Floats");
        Check(ints.GetArrayLength() == 1 && ints[0].GetProperty("Key").GetString() == "_Mode" &&
            ints[0].GetProperty("Value").GetInt32() == 2 && floats.GetArrayLength() == 1 &&
            floats[0].GetProperty("Key").GetString() == "_Roughness" && floats[0].GetProperty("Value").GetSingle() == 0.625f,
            mode + ": integer and floating-point material properties must survive");
    }

    private static string? IndexedPath(JsonElement row, string name) =>
        row.TryGetProperty(name, out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;

    private static byte[] MaterialPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        AlignedString(writer, "FixtureMaterial");
        Pointer(writer, 111);
        writer.Write(0); // Valid and invalid keyword arrays.
        writer.Write(0);
        writer.Write(0u); // Lightmap flags.
        writer.Write(false);
        Align(writer);
        writer.Write(-1); // Render queue, tag map, and disabled passes.
        writer.Write(0);
        writer.Write(0);
        writer.Write(1); // One texture environment, including Endfield's UV set index.
        AlignedString(writer, "_MainTex");
        Pointer(writer, 222);
        writer.Write(1.25f); writer.Write(0.75f);
        writer.Write(0.25f); writer.Write(0.5f);
        writer.Write(0);
        writer.Write(1);
        AlignedString(writer, "_Mode");
        writer.Write(2);
        writer.Write(1);
        AlignedString(writer, "_Roughness");
        writer.Write(0.625f);
        writer.Write(0); // Color properties and Endfield subsurface profile.
        Pointer(writer, 0);
        return stream.ToArray();
    }

    private static byte[] SerializedMaterial(byte[] payload)
    {
        using var metadata = new MemoryStream();
        using var writer = new BinaryWriter(metadata, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("2021.3.0f1\0"));
        writer.Write(19); // StandaloneWindows64, type tree disabled, one Material type.
        writer.Write(false);
        writer.Write(1);
        writer.Write(21);
        writer.Write(false);
        writer.Write((short)-1);
        writer.Write(new byte[16]);
        writer.Write(1);
        Align(writer);
        writer.Write(1L);
        writer.Write(0u);
        writer.Write((uint)payload.Length);
        writer.Write(0);
        writer.Write(0); // Scripts, externals, and user information.
        writer.Write(0);
        writer.Write((byte)0);
        const int dataOffset = 256;
        byte[] result = new byte[dataOffset + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), (uint)result.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), 17);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), dataOffset);
        metadata.ToArray().CopyTo(result, 20);
        payload.CopyTo(result, dataOffset);
        return result;
    }

    private static void Pointer(BinaryWriter writer, long pathId)
    {
        writer.Write(0);
        writer.Write(pathId);
    }

    private static void AlignedString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
        Align(writer);
    }

    private static void Align(BinaryWriter writer)
    {
        while (writer.BaseStream.Position % 4 != 0) writer.Write((byte)0);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
