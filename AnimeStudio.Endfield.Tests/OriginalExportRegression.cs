using System.Globalization;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AnimeStudio.Endfield;
using AS = AnimeStudio;
using CliProgram = AnimeStudio.Endfield.Cli.Program;

internal static class OriginalExportRegression
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
    private static void Reject(Action action, string reason)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { return; }
        throw new InvalidOperationException(reason);
    }
    public static void Run()
    {
        Check(CliProgram.OriginalRelativePath("Assets\\Models\\hero.fbx") == "Assets/Models/hero.fbx", "Unity slashes must normalize without changing source case");
        foreach (string bad in new[] { "../escape", "a/../escape", "/tmp/escape", "C:\\escape", "a//b", "a/./b", "a\u0000b", "\\\\host\\share" })
            Reject(() => CliProgram.OriginalRelativePath(bad), "Unsafe source path must be rejected: " + bad);
        foreach (var block in new[] { BlockType.Audio, BlockType.HotfixAudio, BlockType.InitialAudio, BlockType.AuditAudio,
            BlockType.AudioChinese, BlockType.AudioEnglish, BlockType.AudioJapanese, BlockType.AudioKorean, BlockType.Table, BlockType.JsonData, BlockType.Video })
            Check(!CliProgram.IsOriginalBlock(block), "Classified/media block should not leak into original: " + block);
        foreach (var block in new[] { BlockType.Lua, BlockType.Streaming, BlockType.DynamicStreaming, BlockType.InitialBundle, BlockType.Bundle, BlockType.ExtendData })
            Check(CliProgram.IsOriginalBlock(block), "Previously filtered block should be included: " + block);
        Check(CliProgram.IsOriginalMediaPath("Data/Streaming/MUSIC.WEM") && CliProgram.IsOriginalMediaPath("x.bnk") && !CliProgram.IsOriginalMediaPath("x.fbx"), "Embedded media filename exclusion must be case insensitive");
        var preloadRanges = new HashSet<(string Path, int Index, int Size)>();
        int referencesVisited = 0;
        for (int entry = 0; entry < 3010; entry++)
            if (CliProgram.ClaimOriginalPreloadRange(preloadRanges, "assets/dialog/actor.playable", 0, 7392, 7392))
                referencesVisited += 7392;
        Check(referencesVisited == 7392, "Thousands of subassets sharing one owner and preload range must only traverse that range once");
        Check(CliProgram.ClaimOriginalPreloadRange(preloadRanges, "assets/dialog/other.playable", 0, 7392, 7392),
            "Distinct source owners sharing pointers must retain their independent dependency paths");
        Check(CliProgram.ClaimOriginalPreloadRange(preloadRanges, "assets/dialog/actor.playable", 1, 7391, 7392),
            "A distinct range for the same source owner must not lose references");
        Reject(() => CliProgram.ClaimOriginalPreloadRange(preloadRanges, "assets/dialog/actor.playable", 0, 7392, 7391),
            "Even an already seen range must be validated against its actual preload table");
        Reject(() => CliProgram.ClaimOriginalPreloadRange(preloadRanges, "assets/dialog/actor.playable", int.MaxValue, 1, 7392),
            "Preload range addition cannot overflow into an apparently valid interval");
        using (var nonfinite = JsonDocument.Parse(JsonSerializer.Serialize(new { nan = float.NaN, positive = float.PositiveInfinity, negative = double.NegativeInfinity, ordinary = 1.25 }, CliProgram.OriginalJson)))
        {
            Check(nonfinite.RootElement.GetProperty("nan").GetString() == "NaN" &&
                nonfinite.RootElement.GetProperty("positive").GetString() == "Infinity" &&
                nonfinite.RootElement.GetProperty("negative").GetString() == "-Infinity" &&
                nonfinite.RootElement.GetProperty("ordinary").GetDouble() == 1.25,
                "Metadata must preserve nonfinite Unity values as named strings in valid JSON and finite values as numbers");
        }

        string root = Path.Combine(Path.GetTempPath(), "endfield-original-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var output = new CliProgram.OriginalOutput(root);
            string first = output.Write("Assets/a.png", "Bundle/a", stream => stream.WriteByte(1));
            string second = output.Write("Assets/a.png", "Bundle/b", stream => stream.WriteByte(2));
            Check(first != second && File.ReadAllBytes(Path.Combine(root, first))[0] == 1 && File.ReadAllBytes(Path.Combine(root, second))[0] == 2,
                "Two same-name source assets must not overwrite one another");
            Check(second.EndsWith(".png", StringComparison.Ordinal), "Collision suffixes must preserve the usable output format extension");
            var repeat = new CliProgram.OriginalOutput(root);
            Check(repeat.Write("Assets/a.png", "Bundle/a", stream => stream.WriteByte(1)) == first &&
                repeat.Write("Assets/a.png", "Bundle/b", stream => stream.WriteByte(2)) == second,
                "Ordered rerun must choose identical paths");
            Reject(() => output.Write("Assets/a.png", "Bundle/a", stream => { stream.WriteByte(9); throw new IOException("write failed"); }),
                "Write failure must propagate");
            Check(File.ReadAllBytes(Path.Combine(root, first))[0] == 1 && !Directory.EnumerateFiles(root, "*.partial-*", SearchOption.AllDirectories).Any(),
                "Failed atomic write must preserve previous file and remove partial output");
            Directory.CreateDirectory(Path.Combine(root, "real"));
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "real"));
            Reject(() => output.Write("link/escape.raw", "evil", stream => stream.WriteByte(1)), "Existing directory symlinks cannot redirect exports");
            File.CreateSymbolicLink(Path.Combine(root, "alias.raw"), Path.Combine(root, first));
            Reject(() => output.Write("alias.raw", "evil", stream => stream.WriteByte(9)), "Existing file symlinks cannot redirect exports");
            Reject(() => CliProgram.CopyOriginalRange(new MemoryStream(new byte[1]), new MemoryStream(), 2), "Short object data must fail instead of silently truncating");

            // Unity uses baseVertex to index later vertex ranges; OBJ must use absolute indices
            // and omit nonexistent normal/UV references. This catches real geometry corruption.
            var mesh = (AS.Mesh)RuntimeHelpers.GetUninitializedObject(typeof(AS.Mesh));
            mesh.m_VertexCount = 4;
            mesh.m_Vertices = new float[] { 0, 0, 0, 1.5f, 0, 0, 0, 1, 0, 0, 0, 1 };
            mesh.m_Indices = new() { 0, 1, 2 };
            var sub = (AS.SubMesh)RuntimeHelpers.GetUninitializedObject(typeof(AS.SubMesh));
            sub.indexCount = 3;
            sub.baseVertex = 1;
            mesh.m_SubMeshes = new() { sub };
            Check(CliProgram.OriginalMeshRawOnlyReason(mesh) is null, "Renderable meshes must still be converted to OBJ");
            var emptyMesh = (AS.Mesh)RuntimeHelpers.GetUninitializedObject(typeof(AS.Mesh));
            emptyMesh.m_Indices = new();
            emptyMesh.m_SubMeshes = new();
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) == "empty_mesh", "Genuinely empty placeholder meshes should be preserved as raw-only data");
            emptyMesh.m_VertexCount = 10;
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) is null, "Missing vertices in a nonempty mesh must not be silently classified as an empty placeholder");
            emptyMesh.m_VertexCount = 1;
            emptyMesh.m_Vertices = new float[3];
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) == "mesh_without_primitives", "A valid VAT placeholder with a vertex but no input indices should be preserved as raw-only");
            typeof(AS.Mesh).GetField("m_IndexBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(emptyMesh, new uint[] { 0 });
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) is null, "An input index buffer with no decoded primitives must not be silently accepted as a placeholder");
            typeof(AS.Mesh).GetField("m_IndexBuffer", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(emptyMesh, Array.Empty<uint>());
            emptyMesh.m_Vertices = null!;
            typeof(AS.Mesh).GetField("m_CollisionMeshBaked", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(emptyMesh, true);
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) is null, "A collision flag without collision payload is insufficient to suppress decode errors");
            typeof(AS.Mesh).GetProperty(nameof(AS.Mesh.BakedTriangleCollisionBytes))!.GetSetMethod(nonPublic: true)!.Invoke(emptyMesh, new object[] { 100 });
            Check(CliProgram.OriginalMeshRawOnlyReason(emptyMesh) == "baked_collision_mesh", "Confirmed baked collision payload must be preserved as raw-only, not misreported as a failed render mesh");
            var stripped = (AS.Mesh)RuntimeHelpers.GetUninitializedObject(typeof(AS.Mesh));
            void MeshField(string name, object value) => typeof(AS.Mesh).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stripped, value);
            stripped.m_Indices = new();
            stripped.m_SubMeshes = new() { sub }; // Retained source extents, with no actual geometry buffers.
            var strippedVertex = (AS.VertexData)RuntimeHelpers.GetUninitializedObject(typeof(AS.VertexData));
            strippedVertex.m_DataSize = Array.Empty<byte>();
            strippedVertex.m_Channels = new();
            var strippedStream = (AS.StreamingInfo)RuntimeHelpers.GetUninitializedObject(typeof(AS.StreamingInfo));
            strippedStream.path = "";
            MeshField("m_CollisionMeshBaked", true);
            MeshField("m_IndexBuffer", Array.Empty<uint>());
            MeshField("m_VertexData", strippedVertex);
            MeshField("m_StreamData", strippedStream);
            Check(CliProgram.OriginalMeshRawOnlyReason(stripped) is null, "A partly decoded collision object must not be accepted as stripped");
            MeshField("m_SerializedPayloadFullyRead", true);
            Check(CliProgram.OriginalMeshRawOnlyReason(stripped) == "stripped_baked_collision_mesh", "A fully parsed collision record with all payloads explicitly stripped should preserve raw metadata");
            strippedStream.size = 16;
            Check(CliProgram.OriginalMeshRawOnlyReason(stripped) is null, "A stream payload prevents the stripped-collision exception");
            strippedStream.size = 0;
            strippedVertex.m_DataSize = new byte[1];
            Check(CliProgram.OriginalMeshRawOnlyReason(stripped) is null, "Undecoded vertex bytes prevent the stripped-collision exception");
            strippedVertex.m_DataSize = Array.Empty<byte>();
            MeshField("m_IndexBuffer", new uint[] { 0 });
            Check(CliProgram.OriginalMeshRawOnlyReason(stripped) is null, "Undecoded source indices prevent the stripped-collision exception");
            string Obj()
            {
                using var stream = new MemoryStream();
                CliProgram.WriteOriginalMesh(mesh, stream);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string plain = Obj();
                Check(plain.Contains("v -1.5 0 0") && plain.Contains("f 4 3 2") && !plain.Contains("4/4"),
                    "OBJ must use invariant numbers, reversed winding, baseVertex, and valid plain face references");
                mesh.m_UV0 = new float[8];
                Check(Obj().Contains("f 4/4 3/3 2/2"), "UV-only OBJ face references must not pretend normals exist");
                mesh.m_Normals = new float[12];
                Check(Obj().Contains("f 4/4/4 3/3/3 2/2/2"), "UV+normal OBJ face references must remain complete");
                mesh.m_UV0 = null!;
                Check(Obj().Contains("f 4//4 3//3 2//2"), "Normals without UVs use double slash OBJ syntax");
                sub.baseVertex = 2;
                Reject(() => Obj(), "Out of range baseVertex must not silently corrupt geometry");
                sub.baseVertex = 1;
                mesh.m_Vertices[1] = float.NaN;
                Reject(() => Obj(), "Nonfinite geometry must be reported as an error");
            }
            finally { CultureInfo.CurrentCulture = previous; }

            var boundedMesh = (AS.Mesh)RuntimeHelpers.GetUninitializedObject(typeof(AS.Mesh));
            var serialized = (AS.SerializedFile)RuntimeHelpers.GetUninitializedObject(typeof(AS.SerializedFile));
            serialized.assetsManager = new AS.AssetsManager { PerFileAllocBudgetBytes = 64 * 1024 };
            serialized.fileName = "budget-fixture";
            boundedMesh.assetsFile = serialized;
            var vertexData = (AS.VertexData)RuntimeHelpers.GetUninitializedObject(typeof(AS.VertexData));
            vertexData.m_VertexCount = 1_000_000_000;
            typeof(AS.Mesh).GetField("m_VertexData", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(boundedMesh, vertexData);
            bool bounded = false;
            try { typeof(AS.Mesh).GetMethod("ReadVertexData", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(boundedMesh, null); }
            catch (TargetInvocationException exception) when (exception.InnerException is AS.AllocBudgetExceededException) { bounded = true; }
            Check(bounded, "Corrupt mesh vertex counts must hit the existing allocation budget before allocating channels");

            var strict = new AS.AssetsManager { Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield), ThrowOnLoadError = true };
            using var corruptReader = new AS.FileReader("broken.assets", new MemoryStream(new byte[512]));
            corruptReader.BaseStream.SetLength(4);
            corruptReader.Position = 0;
            bool rejectedCab = false;
            try
            {
                typeof(AS.AssetsManager).GetMethod("LoadAssetsFromMemory", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(strict, new object?[] { corruptReader, "bundle.ab", null, 0L });
            }
            catch (TargetInvocationException) { rejectedCab = true; }
            Check(rejectedCab && strict.ResourceFileReaders.Count == 0 && strict.assetsFileList.Count == 0,
                "Strict bundle loading must propagate a corrupt CAB instead of hiding it in exported resource streams");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("PASS: original export paths, media policy, atomic collision writes, symlink rejection, truncated reads, OBJ baseVertex/UV/normals/invariant numbers");
    }
}
