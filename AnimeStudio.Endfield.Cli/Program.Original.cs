using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using AS = AnimeStudio;

namespace AnimeStudio.Endfield.Cli;

internal static partial class Program
{
    // Unity legitimately stores NaN/Infinity in some auxiliary fields. JSON has
    // no numeric equivalents: retain them as the standard named strings while
    // the accompanying .raw keeps their exact IEEE-754 bit representations.
    internal static readonly JsonSerializerOptions OriginalJson = new()
    {
        IncludeFields = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    internal static bool IsOriginalBlock(BlockType block) => block is not (
        BlockType.None or BlockType.Raw or BlockType.Table or BlockType.JsonData or
        BlockType.Audio or BlockType.InitialAudio or BlockType.AuditAudio or BlockType.HotfixAudio or
        BlockType.AudioChinese or BlockType.AudioEnglish or BlockType.AudioJapanese or BlockType.AudioKorean or
        BlockType.Video or BlockType.AuditVideo);

    internal static bool IsOriginalMediaPath(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".wem" or ".bnk" or ".pck" or ".akpk" or ".wav" or ".mp3" or ".ogg" or ".opus" or
        ".aac" or ".flac" or ".aiff" or ".aif" or ".usm" or ".mp4" or ".webm" or ".mov" or ".avi";

    internal static string OriginalRelativePath(string path)
    {
        string value = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains(':') ||
            value.Any(char.IsControl) || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Unsafe original path: {path}");
        return value;
    }

    private static string OriginalSegment(string value) => string.IsNullOrEmpty(value) ? "unnamed" :
        string.Concat(value.Select(c => char.IsControl(c) || "/\\:*?\"<>|".Contains(c) ? '_' : c));

    internal sealed class OriginalOutput
    {
        private readonly string root;
        private readonly Dictionary<string, string> claims = new(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new();
        public OriginalOutput(string path)
        {
            root = Path.GetFullPath(path);
            Directory.CreateDirectory(root);
            CheckLinks(root);
            claims["_index.jsonl"] = "reserved:index";
        }
        private void CheckLinks(string path)
        {
            for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
                if (current.LinkTarget is not null) throw new IOException($"Symbolic link in output path: {current.FullName}");
        }
        public string Write(string relative, string identity, Action<Stream> action)
        {
            relative = OriginalRelativePath(relative);
            lock (gate)
            {
                if (claims.TryGetValue(relative, out string? previous) && previous != identity)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
                    string extension = Path.GetExtension(relative);
                    relative = relative[..^extension.Length] + "__" + hash + extension;
                    if (claims.TryGetValue(relative, out previous) && previous != identity)
                        throw new IOException($"Output identity collision: {relative}");
                }
                claims[relative] = identity;
            }
            string destination = Path.GetFullPath(Path.Combine(root, relative));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Output path escapes root");
            string parent = Path.GetDirectoryName(destination)!;
            CheckLinks(parent);
            if (File.Exists(destination) && new System.IO.FileInfo(destination).LinkTarget is not null)
                throw new IOException($"Symbolic link at output file: {destination}");
            Directory.CreateDirectory(parent);
            string temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) action(stream);
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return relative;
        }
    }

    private sealed record OriginalSource(BlockType Block, ChunkInfo Chunk, AnimeStudio.Endfield.FileInfo File)
    {
        public string Identity => $"{Block}/{File.FileName}";
    }

    private sealed class OriginalRun : IDisposable
    {
        public readonly OriginalOutput Output;
        private readonly StreamWriter index;
        private readonly object gate = new();
        public int Failures, Written, Unmapped, Skipped, Bundles;
        // Lean by default: undecoded object bytes, per-object metadata JSON and
        // .resS copies are reconstructible from the bundles named in the index
        // (source_path + serialized_file + source_offset/source_size), and
        // writing them multiplied the tree to ~14.5M files. They are opt-in.
        public bool KeepRaw, KeepMetadata, KeepResources;
        public OriginalRun(string root)
        {
            Output = new(root);
            // Index is replaced for each run; output files are written atomically.
            string indexPath = Path.Combine(Path.GetFullPath(root), "_index.jsonl");
            if (new System.IO.FileInfo(indexPath).LinkTarget is not null) throw new IOException("Index cannot be a symbolic link");
            index = new StreamWriter(new FileStream(indexPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        }
        public void Record(object row) { lock (gate) { index.WriteLine(JsonSerializer.Serialize(row, OriginalJson)); index.Flush(); } }
        public void Fail(string source, Exception exception)
        {
            Interlocked.Increment(ref Failures);
            Console.Error.WriteLine($"original error: {source}: {exception.Message}");
            Record(new { status = "error", source, error = exception.Message });
        }
        public void Dispose() => index.Dispose();
    }

    private static int RunOriginal(string[] args)
    {
        string? vfs = null, output = null, scratchArg = null, pattern = null;
        int threads = Math.Min(Environment.ProcessorCount, 4), limit = int.MaxValue;
        double memoryGb = 4;
        bool keepRaw = false, keepMetadata = false, keepResources = false;
        var blocks = new List<BlockType>();
        var exactSources = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            string value(string name) => RequireValue(args, ref i, name);
            switch (args[i])
            {
                case "--vfs": vfs = value(args[i]); break;
                case "--out": output = value(args[i]); break;
                case "--scratch": scratchArg = value(args[i]); break;
                case "--threads": threads = int.Parse(value(args[i]), CultureInfo.InvariantCulture); break;
                case "--limit": limit = int.Parse(value(args[i]), CultureInfo.InvariantCulture); break;
                case "--max-memory-gb": memoryGb = double.Parse(value(args[i]), CultureInfo.InvariantCulture); break;
                case "--bundle-name": pattern = value(args[i]); break;
                case "--source": exactSources.Add(value(args[i])); break;
                case "--block": blocks.Add(ParseBlockType(value(args[i]))); break;
                case "--raw": keepRaw = true; break;
                case "--metadata": keepMetadata = true; break;
                case "--resources": keepResources = true; break;
                case "--full": keepRaw = keepMetadata = keepResources = true; break;
                default: throw new ArgumentException($"Unknown original argument: {args[i]}");
            }
        }
        if (vfs is null || output is null) throw new ArgumentException("original requires --vfs and --out");
        if (threads < 1 || limit < 1 || !double.IsFinite(memoryGb) || memoryGb < 0.25 || memoryGb > 128)
            throw new ArgumentException("threads/limit must be positive; max-memory-gb must be between 0.25 and 128");
        if (blocks.Any(block => !IsOriginalBlock(block))) throw new ArgumentException("original excludes classified Table/JsonData and audio/video blocks");
        if (exactSources.Count > 0 && (pattern is not null || limit != int.MaxValue))
            throw new ArgumentException("--source cannot be combined with --bundle-name or --limit");
        if ((pattern is not null || limit != int.MaxValue) &&
            (blocks.Count == 0 || blocks.Any(block => block is not (BlockType.Bundle or BlockType.InitialBundle))))
            throw new ArgumentException("--bundle-name/--limit require explicit --block Bundle and/or --block InitialBundle");
        var regex = pattern is null ? null : new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));
        var loader = new VfsLoader(vfs, Keys.ChaCha20Key);
        long memory = checked((long)(memoryGb * 1024 * 1024 * 1024));
        long allocationBudget = Math.Min(512L * 1024 * 1024, memory / 2);
        threads = Math.Max(1, Math.Min(threads, (int)(memory / (allocationBudget * 2))));
        var selected = blocks.Count == 0 ? BlockTypes.AllDumpable.Where(IsOriginalBlock).ToArray() : blocks.Distinct().ToArray();
        // Validate all present indexes before opening the output; absent optional blocks are normal.
        var sources = new List<OriginalSource>();
        foreach (var block in selected)
        {
            if (!loader.TryLoadBlockInfo(block, out var info))
            {
                if (blocks.Count > 0) throw new FileNotFoundException($"Requested block {block} is absent");
                continue;
            }
            foreach (var chunk in info.Chunks)
            foreach (var file in chunk.Files)
            {
                if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) continue;
                if (string.IsNullOrEmpty(file.FileName)) throw new InvalidDataException($"Unnamed VFS entry in {block}");
                if (regex is not null && !regex.IsMatch(file.FileName)) continue;
                sources.Add(new(block, chunk, file));
            }
        }
        if (exactSources.Count > 0)
        {
            var available = sources.Select(source => source.Identity).ToHashSet(StringComparer.Ordinal);
            var missing = exactSources.Where(source => !available.Contains(source)).ToArray();
            if (missing.Length > 0) throw new FileNotFoundException("Requested original sources are absent or excluded: " + string.Join(", ", missing));
            sources = sources.Where(source => exactSources.Contains(source.Identity)).ToList();
        }
        sources = sources.OrderBy(source => source.Identity, StringComparer.Ordinal).Take(limit).ToList();
        if (sources.Count == 0) throw new InvalidDataException("original selection contains no files");
        if (sources.Select(source => source.Identity).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new InvalidDataException("original selection contains duplicate VFS source paths");
        string scratch = Path.Combine(scratchArg ?? Path.GetTempPath(), "endfield-original-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        using var run = new OriginalRun(output) { KeepRaw = keepRaw, KeepMetadata = keepMetadata, KeepResources = keepResources };
        var manifests = new List<OriginalAssetManifest>();
        try
        {
            if (sources.Any(source => source.Block is BlockType.Bundle or BlockType.InitialBundle) &&
                loader.TryLoadBlockInfo(BlockType.BundleManifest, out var manifestInfo))
            {
                foreach (var chunk in manifestInfo.Chunks)
                foreach (var file in chunk.Files.Where(file => file.FileName.EndsWith(".hgmmap", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        using var stream = new MemoryStream();
                        OriginalExtract(loader, new(BlockType.BundleManifest, chunk, file), stream);
                        stream.Position = 0;
                        manifests.Add(OriginalAssetManifest.Load(stream));
                    }
                    catch (Exception exception) { run.Fail($"BundleManifest/{file.FileName}", exception); }
                }
            }
            Console.WriteLine($"original mode = {(keepRaw || keepMetadata || keepResources ? $"raw={keepRaw} metadata={keepMetadata} resources={keepResources}" : "lean")}");
            Console.WriteLine($"original files = {sources.Count}; stage1 threads = {threads}; allocation budget = {allocationBudget / 1024 / 1024} MB");
            int completedSources = 0;
            // Stage1 I/O runs in parallel, but Unity objects are emitted in source order.
            // This makes collision ownership and index order independent of thread scheduling.
            foreach (var batch in sources.Chunk(threads))
            {
                var preparationErrors = new ConcurrentDictionary<string, Exception>();
                Parallel.ForEach(batch, new ParallelOptions { MaxDegreeOfParallelism = threads }, source =>
                {
                    if (source.Block is not (BlockType.Bundle or BlockType.InitialBundle)) return;
                    try
                    {
                        string directory = OriginalScratchDirectory(scratch, source);
                        Directory.CreateDirectory(directory);
                        using var stream = File.Create(Path.Combine(directory, "source.ab"));
                        OriginalExtract(loader, source, stream);
                    }
                    catch (Exception exception) { preparationErrors[source.Identity] = exception; }
                });
                foreach (var source in batch)
                {
                    try
                    {
                        if (preparationErrors.TryGetValue(source.Identity, out var preparationError)) throw preparationError;
                        if (IsOriginalMediaPath(source.File.FileName)) { Interlocked.Increment(ref run.Skipped); continue; }
                        if (source.Block is BlockType.Bundle or BlockType.InitialBundle)
                            OriginalBundle(loader, source, scratch, allocationBudget, manifests, run);
                        else
                            OriginalVfsFile(loader, source, run);
                    }
                    catch (Exception exception) { run.Fail(source.Identity, exception); }
                    finally
                    {
                        completedSources++;
                        if (completedSources % 100 == 0 || completedSources == sources.Count)
                            Console.Write(FormattableString.Invariant($"[{completedSources}/{sources.Count} {100.0 * completedSources / sources.Count:F1}%] original written={run.Written} failures={run.Failures}\r"));
                    }
                }
                // The Unity loader allocates large decompression buffers; do not leave completed batches live.
                GC.Collect(2, GCCollectionMode.Optimized, blocking: true);
            }
        }
        finally { Directory.Delete(scratch, recursive: true); }
        Console.WriteLine($"original written = {run.Written}; bundles = {run.Bundles}; unmapped = {run.Unmapped}; skipped = {run.Skipped}");
        Console.WriteLine($"original failures = {run.Failures}");
        return run.Failures == 0 ? 0 : 2;
    }

    private static void OriginalExtract(VfsLoader loader, OriginalSource source, Stream destination)
    {
        long actual = loader.ExtractFile(source.Block, source.Chunk, source.File, destination);
        if (actual != source.File.Length) throw new EndOfStreamException($"Truncated VFS file: expected {source.File.Length}, got {actual}");
    }

    private static void OriginalVfsFile(VfsLoader loader, OriginalSource source, OriginalRun run)
    {
        string originalPath = OriginalRelativePath(source.File.FileName);
        string written = run.Output.Write(originalPath, source.Identity, destination =>
        {
            if (source.Block == BlockType.Lua)
            {
                using var data = new MemoryStream();
                OriginalExtract(loader, source, data);
                destination.Write(LuaProcessor.ProcessVfsFile(source.File.FileName, data.ToArray()));
            }
            else OriginalExtract(loader, source, destination);
        });
        run.Record(new { status = "exported", source = source.Identity, block = source.Block.ToString(),
            source_path = source.File.FileName, original_path = originalPath, path_source = "vfs", output_path = written,
            format = source.Block == BlockType.Lua && LuaProcessor.IsLuaSource(source.File.FileName) ? "lua" : "vfs_payload" });
        Interlocked.Increment(ref run.Written);
    }

    private sealed class OriginalNamedObject(AS.ObjectReader reader) : AS.NamedObject(reader);
    private sealed record OriginalPath(string Path, string Kind);

    internal static bool ClaimOriginalPreloadRange(HashSet<(string Path, int Index, int Size)> visited,
        string path, int index, int size, int tableSize)
    {
        if (index < 0 || size < 0 || (long)index + size > tableSize)
            throw new InvalidDataException("AssetBundle preload range is invalid");
        return visited.Add((path, index, size));
    }

    private static string OriginalScratchDirectory(string scratch, OriginalSource source) =>
        Path.Combine(scratch, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Identity)))[..24]);

    private static void OriginalBundle(VfsLoader loader, OriginalSource source, string scratch, long budget,
        IReadOnlyList<OriginalAssetManifest> manifests, OriginalRun run)
    {
        string directory = OriginalScratchDirectory(scratch, source);
        string bundlePath = Path.Combine(directory, "source.ab");
        var manager = new AS.AssetsManager { Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
            Silent = true, SkipProcess = true, ThrowOnLoadError = true, PerFileAllocBudgetBytes = budget };
        try
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            manager.LoadFiles(bundlePath);
            if (manager.assetsFileList.Count == 0) throw new InvalidDataException("Unity bundle yielded no serialized files");
            void CheckBudget()
            {
                long delta = GC.GetAllocatedBytesForCurrentThread() - allocated;
                if (delta > budget) throw new AS.AllocBudgetExceededException(delta, budget, source.File.FileName);
            }
            CheckBudget();
            var paths = new Dictionary<(string file, long id), List<OriginalPath>>();
            var parents = new Dictionary<(string file, long id), List<OriginalPath>>();
            void Add(Dictionary<(string, long), List<OriginalPath>> target, AS.SerializedFile sf, AS.PPtr<AS.Object> pointer, OriginalPath path)
            {
                if (pointer.m_PathID == 0) return;
                string name = pointer.m_FileID == 0 ? sf.fileName :
                    pointer.m_FileID > 0 && pointer.m_FileID <= sf.m_Externals.Count ? sf.m_Externals[pointer.m_FileID - 1].fileName :
                    throw new InvalidDataException("AssetBundle reference has invalid external file ID");
                var key = (name, pointer.m_PathID);
                if (!target.TryGetValue(key, out var list)) target[key] = list = new();
                if (!list.Contains(path)) list.Add(path);
            }
            foreach (var sf in manager.assetsFileList)
            foreach (var info in sf.m_Objects.Where(info => info.classID == (int)AS.ClassIDType.AssetBundle))
            {
                var bundle = new AS.AssetBundle(new AS.ObjectReader(sf.reader, sf, info, manager.Game));
                var visitedPreloadRanges = new HashSet<(string Path, int Index, int Size)>();
                foreach (var item in bundle.m_Container)
                {
                    string path = OriginalRelativePath(item.Key);
                    int separator = path.IndexOf("##", StringComparison.Ordinal);
                    bool physicalPath = path.Contains('/') && !string.IsNullOrEmpty(Path.GetExtension(path));
                    if (separator >= 0)
                    {
                        path = OriginalRelativePath(path[..separator]);
                        Add(parents, sf, item.Value.asset, new(path, "container_subasset"));
                    }
                    else if (physicalPath)
                        Add(paths, sf, item.Value.asset, new(path, "container"));
                    // Every imported subasset may repeat its owner's entire
                    // preload range. Walking it once per identical owner/range
                    // preserves all references without millions of duplicate
                    // records (3010 entries can repeat the same 7392 pointers).
                    bool newRange = ClaimOriginalPreloadRange(visitedPreloadRanges, path,
                        item.Value.preloadIndex, item.Value.preloadSize, bundle.m_PreloadTable.Count);
                    if (physicalPath && newRange)
                    {
                        var dependency = new OriginalPath(path, "container_dependency");
                        for (int p = item.Value.preloadIndex; p < item.Value.preloadIndex + item.Value.preloadSize; p++)
                            Add(parents, sf, bundle.m_PreloadTable[p], dependency);
                    }
                }
                CheckBudget();
            }
            // Imported FBX files often register every mesh under the same
            // container key. That key identifies the owning source asset,
            // not a unique standalone mesh output path.
            var sharedContainerPaths = paths.Values.SelectMany(list => list.Select(path => path.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sf in manager.assetsFileList)
            foreach (var info in sf.m_Objects.OrderBy(info => info.m_PathID))
            {
                string type = ((AS.ClassIDType)info.classID).ToString();
                if (type.StartsWith("Audio", StringComparison.Ordinal) || type is "VideoClip" or "MovieTexture")
                { Interlocked.Increment(ref run.Skipped); continue; }
                string identity = source.Identity + "/" + sf.fileName + "/" + info.m_PathID;
                try
                {
                    // Allocation counters are cumulative, even after memory has
                    // been collected. Start afresh for each object's decoding;
                    // streaming previous objects must not consume this budget.
                    allocated = GC.GetAllocatedBytesForCurrentThread();
                    var reader = new AS.ObjectReader(sf.reader, sf, info, manager.Game);
                    string name = "";
                    // Only these types are known to begin with NamedObject; never guess for components.
                    if ((AS.ClassIDType)info.classID is AS.ClassIDType.Texture2D or AS.ClassIDType.Mesh or AS.ClassIDType.Material or
                        AS.ClassIDType.TextAsset or AS.ClassIDType.AssetBundle or AS.ClassIDType.Shader or AS.ClassIDType.AnimationClip or
                        AS.ClassIDType.MonoScript or AS.ClassIDType.Avatar or AS.ClassIDType.AnimatorController)
                        name = new OriginalNamedObject(reader).m_Name ?? "";
                    var key = (sf.fileName, info.m_PathID);
                    OriginalPath? resolved = paths.GetValueOrDefault(key)?.OrderBy(p => p.Path, StringComparer.Ordinal).FirstOrDefault();
                    string? containerParent = resolved is not null && sharedContainerPaths.Contains(resolved.Path) ? resolved.Path : null;
                    if (containerParent is not null) resolved = null;
                    string? manifestParent = null;
                    if (name.Length > 0)
                    {
                        var subassets = manifests.Select(manifest => manifest.TryResolveSubAsset(source.File.FileName, name, out string owner, out _) ? owner : null)
                            .Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (subassets.Length == 1)
                        {
                            manifestParent = OriginalRelativePath(subassets[0]!);
                            resolved = null;
                        }
                        else if (resolved is null && containerParent is null)
                        {
                            var matches = manifests.Select(manifest => manifest.TryResolve(source.File.FileName, name, out string path) ? path : null)
                                .Where(path => path is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            if (matches.Length == 1) resolved = new(OriginalRelativePath(matches[0]!), "manifest_unique_name");
                        }
                    }
                    string leaf = OriginalSegment(name) + "_" + info.m_PathID.ToString(CultureInfo.InvariantCulture);
                    string? parent = manifestParent ?? containerParent ?? parents.GetValueOrDefault(key)?.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();
                    string basis = resolved?.Path ?? (parent is not null
                        ? parent + ".subassets/" + type + "/" + leaf
                        : "_unmapped/" + source.Block + "/" + OriginalRelativePath(source.File.FileName) + "/" + OriginalSegment(sf.fileName) + "/" + type + "/" + leaf);
                    string pathKind = resolved?.Kind ?? (parent is not null ? "derived_subasset" : "unmapped");
                    if (pathKind == "unmapped") Interlocked.Increment(ref run.Unmapped);
                    if (resolved is not null && IsOriginalMediaPath(resolved.Path)) { Interlocked.Increment(ref run.Skipped); continue; }
                    string? decoded = null;
                    string? decodeError = null;
                    string? rawOnlyReason = null;
                    object? details = null;
                    try
                    {
                        if (info.classID == (int)AS.ClassIDType.Texture2D)
                        {
                            var texture = new AS.Texture2D(reader);
                            rawOnlyReason = OriginalTextureRawOnlyReason(texture, reader.Position == info.byteStart + info.byteSize);
                            details = new { texture.m_Width, texture.m_Height, texture.m_TextureFormat, texture.m_CompleteImageSize,
                                texture.m_MipCount, texture.m_MipsStripped, texture.m_ImageCount, texture.m_TextureDimension,
                                image_data_size = texture.image_data.Size, texture.m_StreamData };
                            if (rawOnlyReason is null)
                            {
                                if (texture.m_Width < 1 || texture.m_Height < 1 || (long)texture.m_Width * texture.m_Height > budget / 16)
                                    throw new InvalidDataException("Texture dimensions exceed decode memory budget");
                                using var image = AS.Texture2DExtensions.ConvertToImage(texture, flip: true)
                                    ?? throw new InvalidDataException("Texture decoder returned no image");
                                CheckBudget();
                                decoded = run.Output.Write(basis + ".png", identity, stream => image.Save(stream, new PngEncoder { CompressionLevel = PngCompressionLevel.BestSpeed }));
                            }
                        }
                        else if (info.classID == (int)AS.ClassIDType.Mesh)
                        {
                            var mesh = new AS.Mesh(reader);
                            CheckBudget();
                            rawOnlyReason = OriginalMeshRawOnlyReason(mesh);
                            details = new { mesh.m_VertexCount, mesh.m_SubMeshes, mesh.m_BindPose, mesh.m_BoneNameHashes, mesh.m_Skin,
                                mesh.m_Shapes, mesh.m_UV1, mesh.m_UV2, mesh.m_UV3, mesh.m_Colors, mesh.m_Tangents,
                                mesh.IsBakedCollisionMesh, mesh.BakedConvexCollisionBytes, mesh.BakedTriangleCollisionBytes,
                                mesh.HasNoPrimitives, mesh.IsStrippedBakedCollisionMesh };
                            if (rawOnlyReason is null)
                                decoded = run.Output.Write(basis + ".obj", identity, stream => WriteOriginalMesh(mesh, stream));
                        }
                        else if (info.classID == (int)AS.ClassIDType.Material)
                        {
                            var material = new AS.Material(reader);
                            CheckBudget();
                            details = new { shader = new { file_id = material.m_Shader.m_FileID, path_id = material.m_Shader.m_PathID },
                                textures = material.m_SavedProperties.m_TexEnvs.Select(pair => new { slot = pair.Key,
                                    file_id = pair.Value.m_Texture.m_FileID, path_id = pair.Value.m_Texture.m_PathID,
                                    scale = pair.Value.m_Scale, offset = pair.Value.m_Offset }),
                                material.m_SavedProperties.m_Floats, material.m_SavedProperties.m_Ints, material.m_SavedProperties.m_Colors };
                        }
                        else if (info.classID == (int)AS.ClassIDType.TextAsset)
                        {
                            var asset = new AS.TextAsset(reader);
                            CheckBudget();
                            decoded = run.Output.Write(basis + ".bytes", identity, stream => stream.Write(asset.m_Script));
                        }
                    }
                    catch (Exception exception)
                    {
                        decodeError = exception.Message;
                        // Unsupported material variants still have exact raw bytes; model/image decode failures fail the run.
                        if (info.classID is (int)AS.ClassIDType.Texture2D or (int)AS.ClassIDType.Mesh) run.Fail(identity, exception);
                    }
                    bool failedDecode = decodeError is not null && info.classID is (int)AS.ClassIDType.Texture2D or (int)AS.ClassIDType.Mesh;
                    // Keep structured details alongside --raw unless metadata already contains them.
                    string? detailsPath = !run.KeepMetadata && details is not null && info.classID is (int)AS.ClassIDType.Material or (int)AS.ClassIDType.Mesh
                        ? run.Output.Write(basis + "." + type + ".json", identity, stream => JsonSerializer.Serialize(stream, details, OriginalJson))
                        : null;
                    if (!run.KeepRaw && !run.KeepMetadata)
                    {
                        // Lean row: everything needed to slice the object back
                        // out of its bundle on demand, plus structured details
                        // (material slots, mesh skin/bind poses) as a sidecar.
                        run.Record(new { status = failedDecode ? "decode_error" : decoded is not null || detailsPath is not null ? "exported" : rawOnlyReason is not null ? "raw_only" : "indexed",
                            source = identity, block = source.Block.ToString(), source_path = source.File.FileName, serialized_file = sf.fileName,
                            path_id = info.m_PathID, class_id = info.classID, type, name, original_path = resolved?.Path, path_source = pathKind,
                            parent_asset_path = parent, source_offset = info.byteStart, source_size = info.byteSize,
                            output_path = decoded, details_path = detailsPath, decode_error = decodeError, raw_only_reason = rawOnlyReason,
                            texture = info.classID == (int)AS.ClassIDType.Texture2D ? details : null });
                        Interlocked.Increment(ref run.Written);
                        continue;
                    }
                    string? raw = !run.KeepRaw ? null : run.Output.Write(basis + "." + type + ".raw", identity, stream =>
                    {
                        sf.reader.Position = info.byteStart;
                        CopyOriginalRange(sf.reader.BaseStream, stream, info.byteSize);
                    });
                    var metadata = new { source = source.Identity, block = source.Block.ToString(), bundle_path = source.File.FileName,
                        serialized_file = sf.fileName, path_id = info.m_PathID, class_id = info.classID, type, name,
                        original_path = resolved?.Path, path_source = pathKind, parent_asset_path = parent,
                        container_asset_paths = paths.GetValueOrDefault(key)?.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal),
                        related_asset_paths = parents.GetValueOrDefault(key)?.Select(p => p.Path).OrderBy(p => p, StringComparer.Ordinal),
                        source_offset = info.byteStart, source_size = info.byteSize, unity_version = sf.unityVersion,
                        endian = sf.reader.Endian.ToString(), serialized_format = (int)sf.header.m_Version,
                        externals = sf.m_Externals.Select(external => new { external.fileName, external.pathName, external.type, external.guid }),
                        serialized_type = info.serializedType, raw_path = raw, output_path = decoded, decode_error = decodeError,
                        raw_only_reason = rawOnlyReason, details };
                    string? metadataPath = !run.KeepMetadata ? null : run.Output.Write(basis + "." + type + ".metadata.json", identity,
                        stream => JsonSerializer.Serialize(stream, metadata, OriginalJson));
                    run.Record(new { status = failedDecode ? "decode_error" : rawOnlyReason is not null ? "raw_only" : decoded is not null || raw is not null || metadataPath is not null ? "exported" : "indexed",
                        source = identity, block = source.Block.ToString(), source_path = source.File.FileName, serialized_file = sf.fileName,
                        path_id = info.m_PathID, class_id = info.classID, type, name, original_path = resolved?.Path, path_source = pathKind, parent_asset_path = parent,
                        source_offset = info.byteStart, source_size = info.byteSize,
                        output_path = decoded ?? raw, raw_path = raw, metadata_path = metadataPath, details_path = detailsPath, decode_error = decodeError,
                        raw_only_reason = rawOnlyReason });
                    Interlocked.Increment(ref run.Written);
                }
                catch (AS.AllocBudgetExceededException) { throw; }
                catch (Exception exception) { run.Fail(identity, exception); }
            }
            if (run.KeepResources) OriginalResources(manager, source, run);
            Interlocked.Increment(ref run.Bundles);
        }
        finally { manager.Clear(); Directory.Delete(directory, recursive: true); }
    }

    internal static string? OriginalTextureRawOnlyReason(AS.Texture2D texture, bool serializedPayloadFullyRead)
    {
        // Dynamic font atlases are serialized as empty textures, then populated
        // at runtime. A missing or corrupt pixel payload must still be an error.
        return serializedPayloadFullyRead && texture.m_Width == 0 && texture.m_Height == 0 &&
            texture.m_CompleteImageSize == 0 && texture.m_MipsStripped == 0 && texture.m_MipCount is 0 or 1 &&
            texture.m_ImageCount == 0 && texture.image_data is not null && texture.image_data.Size == 0 &&
            Enum.IsDefined(texture.m_TextureFormat) &&
            (texture.m_StreamData is null || texture.m_StreamData.offset == 0 && texture.m_StreamData.size == 0 &&
                string.IsNullOrEmpty(texture.m_StreamData.path)) ? "empty_texture" : null;
    }

    internal static void CopyOriginalRange(Stream input, Stream output, long length)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (read == 0) throw new EndOfStreamException("Truncated serialized object");
                output.Write(buffer, 0, read);
                length -= read;
            }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static string? OriginalMeshRawOnlyReason(AS.Mesh mesh)
    {
        // Baked collision blobs contain PhysX data, not renderable vertex/index
        // buffers. Preserve exact serialization and state the representation.
        if (mesh.IsBakedCollisionMesh && (mesh.BakedConvexCollisionBytes > 0 || mesh.BakedTriangleCollisionBytes > 0))
            return "baked_collision_mesh";
        if (mesh.IsStrippedBakedCollisionMesh) return "stripped_baked_collision_mesh";
        if (mesh.IsEmptyGeometry) return "empty_mesh";
        // VAT placeholder assets can contain a valid vertex buffer but no
        // source primitives at all. Do not invent faces or mislabel them as a
        // failed triangle decoder; retain the object and resource bytes.
        if (mesh.HasNoPrimitives && mesh.m_VertexCount > 0 && mesh.m_Vertices is not null &&
            (mesh.m_Vertices.LongLength == (long)mesh.m_VertexCount * 3 ||
             mesh.m_Vertices.LongLength == (long)mesh.m_VertexCount * 4))
            return "mesh_without_primitives";
        return null;
    }

    internal static void WriteOriginalMesh(AS.Mesh mesh, Stream stream)
    {
        int count = mesh.m_VertexCount;
        if (count <= 0 || mesh.m_Vertices is null || mesh.m_Vertices.Length % count != 0 ||
            mesh.m_Vertices.Length / count is not (3 or 4)) throw new InvalidDataException("Mesh has no valid vertex data");
        int positions = mesh.m_Vertices.Length / count;
        int uv = mesh.m_UV0 is { Length: > 0 } ? mesh.m_UV0.Length / count : 0;
        int normals = mesh.m_Normals is { Length: > 0 } ? mesh.m_Normals.Length / count : 0;
        if (uv > 0 && (uv is < 2 or > 4 || mesh.m_UV0.Length != uv * count)) throw new InvalidDataException("Invalid mesh UV count");
        if (normals > 0 && (normals is not (3 or 4) || mesh.m_Normals.Length != normals * count)) throw new InvalidDataException("Invalid mesh normal count");
        if (mesh.m_Indices.Count == 0 || mesh.m_Indices.Count % 3 != 0) throw new InvalidDataException("Mesh has no valid triangles");
        if (mesh.m_SubMeshes.Sum(sub => (long)sub.indexCount) != mesh.m_Indices.Count) throw new InvalidDataException("Mesh submesh index ranges do not cover triangles");
        int checkedOffset = 0;
        foreach (var submesh in mesh.m_SubMeshes)
        {
            if (submesh.indexCount % 3 != 0) throw new InvalidDataException("Submesh has an incomplete triangle");
            for (int index = 0; index < submesh.indexCount; index++)
                if ((ulong)mesh.m_Indices[checkedOffset++] + submesh.baseVertex >= (ulong)count)
                    throw new InvalidDataException("Submesh baseVertex places index outside vertices");
        }
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        string Number(float value) => float.IsFinite(value) ? value.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidDataException("Nonfinite mesh coordinate");
        writer.WriteLine("# Endfield mesh; Unity X axis mirrored, triangle winding reversed; skeleton data is in the .Mesh.json (or .Mesh.metadata.json) sidecar");
        for (int v = 0; v < count; v++) writer.WriteLine($"v {Number(-mesh.m_Vertices[v * positions])} {Number(mesh.m_Vertices[v * positions + 1])} {Number(mesh.m_Vertices[v * positions + 2])}");
        if (uv > 0) for (int v = 0; v < count; v++) writer.WriteLine($"vt {Number(mesh.m_UV0[v * uv])} {Number(mesh.m_UV0[v * uv + 1])}");
        if (normals > 0) for (int v = 0; v < count; v++) writer.WriteLine($"vn {Number(-mesh.m_Normals[v * normals])} {Number(mesh.m_Normals[v * normals + 1])} {Number(mesh.m_Normals[v * normals + 2])}");
        string Vertex(uint index)
        {
            string value = (index + 1).ToString(CultureInfo.InvariantCulture);
            return uv > 0 ? (normals > 0 ? $"{value}/{value}/{value}" : $"{value}/{value}") : (normals > 0 ? $"{value}//{value}" : value);
        }
        int offset = 0;
        for (int group = 0; group < mesh.m_SubMeshes.Count; group++)
        {
            writer.WriteLine($"g mesh_{group}");
            int end = checked(offset + (int)mesh.m_SubMeshes[group].indexCount);
            uint baseVertex = mesh.m_SubMeshes[group].baseVertex;
            for (; offset < end; offset += 3)
                writer.WriteLine($"f {Vertex(mesh.m_Indices[offset + 2] + baseVertex)} {Vertex(mesh.m_Indices[offset + 1] + baseVertex)} {Vertex(mesh.m_Indices[offset] + baseVertex)}");
        }
    }
}
