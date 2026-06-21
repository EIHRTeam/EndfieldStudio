using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;
using AS = AnimeStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;
using System.Diagnostics;
using System.Runtime;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AnimeStudio.Endfield.Cli;

internal static class Program
{
    private const string Usage =
        """
        Usage:
          endfield-dump dump    --vfs <streaming_assets_path> --out <dir> [--block <type>...] [--threads N]
          endfield-dump list    --vfs <streaming_assets_path> [--block <type>...]
          endfield-dump inspect --vfs <streaming_assets_path> [--limit N] [--min-size N] [--names <regex>]
          endfield-dump extract --vfs <streaming_assets_path> --out <dir>
                                [--bundle-name <regex>] [--asset-name <regex>] [--types <T>...]
                                [--block <type>...] [--threads N] [--scratch <dir>] [--keep-bundles]
                                [--format png|bmp|tga] [--png-compression none|fast|default]
                                [--max-memory-gb N] [--no-chunk-batching]

        Block types (case-insensitive). If omitted, all dumpable types are processed.
          InitialAudio, InitialBundle, InitialExtendData, BundleManifest, IFixPatch,
          AuditStreaming, AuditDynamicStreaming, AuditIV, AuditAudio, AuditVideo,
          Bundle, Audio, Video, IV, Streaming, DynamicStreaming, Lua, Table, JsonData,
          ExtendData, HotfixAudio, AudioChinese, AudioEnglish, AudioJapanese, AudioKorean

        `inspect` decrypts up to N (default=1) bundles into /dev/shm/efend, loads them
        with AnimeStudio (Stage 2 + 3), and lists Texture2D names matching --names regex.

        `extract` runs the full Stage1+Stage2+Stage3 pipeline:
          1. Stage1 streams matching VFS bundles into <scratch> (default /dev/shm/efend-bundles).
          2. Parallel.ForEach over bundles loads them with a per-thread AssetsManager.
          3. Texture2D objects matching --asset-name regex are saved as <out>/<name>.png.
        Use --threads to control parallelism (default = ProcessorCount).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 1;
        }

        try
        {
            string command = args[0];
            return command switch
            {
                "dump" => RunDump(args.AsSpan(1)),
                "list" => RunList(args.AsSpan(1)),
                "inspect" => RunInspect(args.AsSpan(1)),
                "extract" => await RunExtract(args[1..]),
                "--help" or "-h" or "help" => PrintHelpAndExit(),
                _ => UnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int PrintHelpAndExit()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine(Usage);
        return 1;
    }

    private sealed class ParsedArgs
    {
        public string? VfsPath;
        public string? OutPath;
        public List<BlockType> Blocks = new();
        public int Threads;
    }

    private static ParsedArgs ParseArgs(ReadOnlySpan<string> args)
    {
        var parsed = new ParsedArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs":
                case "-s":
                    parsed.VfsPath = RequireValue(args, ref i, a);
                    break;
                case "--out":
                case "-o":
                    parsed.OutPath = RequireValue(args, ref i, a);
                    break;
                case "--block":
                case "-b":
                    {
                        string val = RequireValue(args, ref i, a);
                        parsed.Blocks.Add(ParseBlockType(val));
                        break;
                    }
                case "--threads":
                case "-t":
                    parsed.Threads = int.Parse(RequireValue(args, ref i, a));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        return parsed;
    }

    private static string RequireValue(ReadOnlySpan<string> args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {name}");
        i += 1;
        return args[i];
    }

    private static BlockType ParseBlockType(string s)
    {
        foreach (var bt in BlockTypes.AllDumpable)
        {
            if (string.Equals(bt.ToString(), s, StringComparison.OrdinalIgnoreCase))
                return bt;
            if (string.Equals(bt.Name(), s, StringComparison.OrdinalIgnoreCase))
                return bt;
        }
        throw new ArgumentException($"Unknown block type: {s}");
    }

    private static int RunList(ReadOnlySpan<string> args)
    {
        string? vfsPath = null;
        var blocks = new List<BlockType>();
        bool listFiles = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs":
                case "-s":
                    vfsPath = RequireValue(args, ref i, a);
                    break;
                case "--block":
                case "-b":
                    blocks.Add(ParseBlockType(RequireValue(args, ref i, a)));
                    break;
                case "--list-files":
                    listFiles = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (vfsPath is null) throw new ArgumentException("--vfs is required");

        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key);
        var blockEnumerable = blocks.Count > 0 ? blocks : (IEnumerable<BlockType>)BlockTypes.AllDumpable;

        foreach (var bt in blockEnumerable)
        {
            BlockMainInfo info;
            try
            {
                info = loader.LoadBlockInfo(bt);
            }
            catch (DirectoryNotFoundException)
            {
                Console.WriteLine($"[{bt.Name()}] (not present, skipped)");
                continue;
            }

            if (listFiles)
            {
                foreach (var c in info.Chunks)
                    foreach (var f in c.Files)
                        if (!string.IsNullOrEmpty(f.FileName)
                            && !f.FileName.EndsWith('/')
                            && !f.FileName.EndsWith('\\'))
                            Console.WriteLine($"{bt.Name()}\t{f.FileName}\t{f.Length}");
            }
            else
            {
                int totalFiles = 0;
                foreach (var c in info.Chunks) totalFiles += c.Files.Count;
                Console.WriteLine(
                    $"[{bt.Name()}] version={info.Version} codeVersion={info.CodeVersion} chunks={info.Chunks.Count} files={totalFiles}");
            }
        }
        return 0;
    }

    private static int RunDump(ReadOnlySpan<string> args)
    {
        var parsed = ParseArgs(args);
        if (parsed.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (parsed.OutPath is null) throw new ArgumentException("--out is required");

        Directory.CreateDirectory(parsed.OutPath);
        var loader = new VfsLoader(parsed.VfsPath, Keys.ChaCha20Key);
        var blocks = parsed.Blocks.Count > 0 ? parsed.Blocks : (IEnumerable<BlockType>)BlockTypes.AllDumpable;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = parsed.Threads > 0 ? parsed.Threads : Environment.ProcessorCount,
        };

        foreach (var bt in blocks)
        {
            DumpBlock(loader, bt, parsed.OutPath, parallelOptions);
        }
        return 0;
    }

    private static void DumpBlock(VfsLoader loader, BlockType bt, string outRoot, ParallelOptions opts)
    {
        Console.WriteLine($"Dumping {bt.Name()} files...");

        BlockMainInfo info;
        try
        {
            info = loader.LoadBlockInfo(bt);
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.WriteLine($"  Warning: {ex.Message}, skipping");
            return;
        }

        int total = 0;
        foreach (var c in info.Chunks) total += c.Files.Count;

        int success = 0;
        int errors = 0;

        foreach (var chunk in info.Chunks)
        {
            Parallel.ForEach(chunk.Files, opts, file =>
            {
                try
                {
                    if (string.IsNullOrEmpty(file.FileName)) return;
                    if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) return;

                    string outPath = ResolveOutputPath(bt, file.FileName, outRoot);
                    string? dir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    if (bt == BlockType.Lua)
                    {
                        // Lua 文件普遍很小（~KB），后处理需要整块内存
                        byte[] data = loader.ExtractFileToBytes(bt, chunk, file);
                        byte[] processed = LuaProcessor.DecryptAndNormalize(data);
                        File.WriteAllBytes(outPath, processed);
                    }
                    else
                    {
                        // 其他类型直接流式：ChaCha20 解密 → 直接写盘，零拷贝中间态
                        using var fs = new FileStream(
                            outPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            bufferSize: 64 * 1024,
                            options: FileOptions.SequentialScan);
                        loader.ExtractFile(bt, chunk, file, fs);
                    }

                    Interlocked.Increment(ref success);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref errors);
                    Console.Error.WriteLine($"  Error: failed to extract {file.FileName}: {ex.Message}");
                }
            });
        }

        Console.WriteLine($"  Done: extracted {success}/{total} files");
        if (errors > 0)
        {
            Console.WriteLine($"  Warning: {errors} files failed");
        }
    }

    private static string ResolveOutputPath(BlockType bt, string fileName, string outRoot)
    {
        if (bt == BlockType.Lua)
        {
            string outName = fileName.EndsWith(".lua", StringComparison.Ordinal)
                ? fileName
                : (fileName.EndsWith(".lua.enc", StringComparison.Ordinal)
                    ? fileName.Substring(0, fileName.Length - ".lua.enc".Length) + ".lua"
                    : fileName + ".lua");
            return Path.Combine(outRoot, "Lua", outName);
        }
        return Path.Combine(outRoot, fileName);
    }

    /// <summary>
    /// Sanity-check pipeline: extracts up to N bundles into a tmpfs scratch dir,
    /// loads them via AnimeStudio.AssetsManager (Stage 2 + 3), and prints
    /// Texture2D names matching --names regex.
    /// </summary>
    private static int RunInspect(ReadOnlySpan<string> args)
    {
        string? vfsPath = null;
        int limit = 1;
        long minSize = 0;
        string? namesRegex = null;
        var types = new List<string>();
        var blocks = new List<BlockType>();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs": vfsPath = RequireValue(args, ref i, a); break;
                case "--limit": limit = int.Parse(RequireValue(args, ref i, a)); break;
                case "--min-size": minSize = long.Parse(RequireValue(args, ref i, a)); break;
                case "--names": namesRegex = RequireValue(args, ref i, a); break;
                case "--types": types.Add(RequireValue(args, ref i, a)); break;
                case "--block": blocks.Add(ParseBlockType(RequireValue(args, ref i, a))); break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (vfsPath is null) throw new ArgumentException("--vfs is required");
        if (blocks.Count == 0) blocks.Add(BlockType.Bundle);

        string scratch = "/dev/shm/efend-inspect";
        if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key);

        // Stage 1: collect candidate (chunk, file) pairs across selected blocks,
        // optionally filter by minimum size, then sort by size (largest first).
        var candidates = new List<(BlockType bt, ChunkInfo chunk, AnimeStudio.Endfield.FileInfo file)>();
        foreach (var bt in blocks)
        {
            BlockMainInfo info = loader.LoadBlockInfo(bt);
            foreach (var chunk in info.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    if (string.IsNullOrEmpty(file.FileName)) continue;
                    if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) continue;
                    if (file.Length < minSize) continue;
                    candidates.Add((bt, chunk, file));
                }
            }
        }
        candidates.Sort((a, b) => b.file.Length.CompareTo(a.file.Length));

        var extracted = new List<string>();
        foreach (var (bt, chunk, file) in candidates)
        {
            if (extracted.Count >= limit) break;
            string outPath = Path.Combine(scratch, Path.GetFileName(file.FileName));
            using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                loader.ExtractFile(bt, chunk, file, fs);
            extracted.Add(outPath);
        }

        Console.WriteLine($"Stage 1: extracted {extracted.Count} bundles to {scratch} (min-size={minSize}, picked top {limit} by size)");
        foreach (var p in extracted)
            Console.WriteLine($"  {Path.GetFileName(p)}  ({new System.IO.FileInfo(p).Length:N0} bytes)");

        // Stage 2 + 3: load via AnimeStudio.
        Console.WriteLine();
        Console.WriteLine("Stage 2+3: loading via AnimeStudio.AssetsManager...");

        var mgr = new AS.AssetsManager
        {
            Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
            Silent = true,
            SkipProcess = false,
        };
        mgr.LoadFiles(extracted.ToArray());

        Console.WriteLine($"  assetsFileList.Count = {mgr.assetsFileList.Count}");

        var rx = namesRegex is null ? null : new System.Text.RegularExpressions.Regex(namesRegex);
        int total = 0, t2d = 0, matched = 0;
        var matchedNames = new List<(string name, int width, int height, AS.TextureFormat fmt)>();
        var typeHisto = new Dictionary<string, int>();

        foreach (var sf in mgr.assetsFileList)
        {
            foreach (var obj in sf.Objects)
            {
                total++;
                string typeName = obj.GetType().Name;
                typeHisto[typeName] = typeHisto.GetValueOrDefault(typeName) + 1;
                if (obj is AS.Texture2D tex)
                {
                    t2d++;
                    string name = tex.m_Name ?? "";
                    if (rx is null || rx.IsMatch(name))
                    {
                        matched++;
                        matchedNames.Add((name, tex.m_Width, tex.m_Height, (AS.TextureFormat)tex.m_TextureFormat));
                    }
                }
            }
        }

        Console.WriteLine($"  total objects = {total}");
        Console.WriteLine($"  Texture2D     = {t2d}");
        Console.WriteLine($"  matched       = {matched}");
        if (typeHisto.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Object type histogram (top 15):");
            foreach (var kv in typeHisto.OrderByDescending(p => p.Value).Take(15))
                Console.WriteLine($"  {kv.Key,-30} {kv.Value}");
        }
        if (matched > 0)
        {
            Console.WriteLine();
            Console.WriteLine("First 30 matched Texture2D:");
            foreach (var (name, w, h, fmt) in matchedNames.Take(30))
                Console.WriteLine($"  {name,-50} {w}x{h} {fmt}");
        }

        return 0;
    }

    /// <summary>
    /// B1 implementation: full Stage1+Stage2+Stage3 pipeline with per-thread
    /// AssetsManager so the main loop is parallelized (no global state).
    /// Stage1 → bounded channel → Stage2+3 形成流水线，/dev/shm 占用稳态受控。
    /// </summary>
    private static async Task<int> RunExtract(string[] args)
    {
        string? vfsPath = null;
        string? outPath = null;
        string? bundleNameRegex = null;
        string? assetNameRegex = null;
        var typeFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocks = new List<BlockType>();
        int threads = 0;
        int limit = 0;
        long minSize = 0;
        string scratch = "/dev/shm/efend-bundles";
        bool keepBundles = false;
        string format = "png";          // png | bmp | tga
        string pngCompression = "none"; // none | fast | default
        int maxMemoryGb = 64;           // 内存预算上限（GB）
        bool noChunkBatching = false;   // 默认按 chunk 分批

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs": vfsPath = RequireValue(args, ref i, a); break;
                case "--out": outPath = RequireValue(args, ref i, a); break;
                case "--bundle-name": bundleNameRegex = RequireValue(args, ref i, a); break;
                case "--asset-name":
                case "--names": assetNameRegex = RequireValue(args, ref i, a); break;
                case "--types": typeFilters.Add(RequireValue(args, ref i, a)); break;
                case "--block": blocks.Add(ParseBlockType(RequireValue(args, ref i, a))); break;
                case "--threads": threads = int.Parse(RequireValue(args, ref i, a)); break;
                case "--limit": limit = int.Parse(RequireValue(args, ref i, a)); break;
                case "--min-size": minSize = long.Parse(RequireValue(args, ref i, a)); break;
                case "--scratch": scratch = RequireValue(args, ref i, a); break;
                case "--keep-bundles": keepBundles = true; break;
                case "--format": format = RequireValue(args, ref i, a).ToLowerInvariant(); break;
                case "--png-compression": pngCompression = RequireValue(args, ref i, a).ToLowerInvariant(); break;
                case "--max-memory-gb": maxMemoryGb = int.Parse(RequireValue(args, ref i, a)); break;
                case "--no-chunk-batching": noChunkBatching = true; break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (vfsPath is null) throw new ArgumentException("--vfs is required");
        if (outPath is null) throw new ArgumentException("--out is required");
        if (blocks.Count == 0) blocks.Add(BlockType.Bundle);
        if (typeFilters.Count == 0) typeFilters.Add("Texture2D");
        if (threads <= 0) threads = Math.Min(Environment.ProcessorCount, 16);
        if (maxMemoryGb <= 0) maxMemoryGb = 64;
        long maxMemoryBytes = (long)maxMemoryGb * 1_000_000_000L;

        // Pick encoder + extension by --format.
        ImageEncoder encoder;
        string ext;
        switch (format)
        {
            case "bmp":
                encoder = new BmpEncoder
                {
                    BitsPerPixel = BmpBitsPerPixel.Pixel32,
                    SupportTransparency = true,
                };
                ext = ".bmp";
                break;
            case "tga":
                encoder = new TgaEncoder
                {
                    BitsPerPixel = TgaBitsPerPixel.Pixel32,
                    Compression = TgaCompression.None,
                };
                ext = ".tga";
                break;
            case "png":
                encoder = new PngEncoder
                {
                    CompressionLevel = pngCompression switch
                    {
                        "fast" => PngCompressionLevel.BestSpeed,
                        "default" => PngCompressionLevel.DefaultCompression,
                        _ => PngCompressionLevel.NoCompression,
                    },
                };
                ext = ".png";
                break;
            default:
                throw new ArgumentException($"Unknown --format: {format} (expected png | bmp | tga)");
        }

        Directory.CreateDirectory(outPath);
        Directory.CreateDirectory(scratch);

        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key);
        var bundleRx = bundleNameRegex is null ? null : new Regex(bundleNameRegex, RegexOptions.Compiled);
        var assetRx = assetNameRegex is null ? null : new Regex(assetNameRegex, RegexOptions.Compiled);

        var swTotal = Stopwatch.StartNew();

        // ── Build batches: per-chunk (default) or single (--no-chunk-batching) ──
        // 每个 batch = 一个 chunk 里的所有匹配 file。按 chunk 分批保证一个 chk 彻底处理完再下一个。
        var batches = new List<List<(BlockType bt, ChunkInfo chunk, AnimeStudio.Endfield.FileInfo file)>>();
        foreach (var bt in blocks)
        {
            BlockMainInfo info;
            try { info = loader.LoadBlockInfo(bt); }
            catch (DirectoryNotFoundException) { continue; }

            foreach (var chunk in info.Chunks)
            {
                var files = new List<(BlockType, ChunkInfo, AnimeStudio.Endfield.FileInfo)>();
                foreach (var file in chunk.Files)
                {
                    if (string.IsNullOrEmpty(file.FileName)) continue;
                    if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) continue;
                    if (file.Length < minSize) continue;
                    if (bundleRx != null && !bundleRx.IsMatch(file.FileName)) continue;
                    files.Add((bt, chunk, file));
                }
                if (files.Count == 0) continue;

                if (noChunkBatching && batches.Count > 0)
                    batches[0].AddRange(files);
                else
                    batches.Add(files);
            }
        }

        // --limit: pick largest N across all, collapses into single batch
        if (limit > 0 && batches.Count > 0)
        {
            var allFiles = batches.SelectMany(b => b)
                .OrderByDescending(x => x.Item3.Length)
                .Take(limit)
                .ToList();
            batches = new List<List<(BlockType, ChunkInfo, AnimeStudio.Endfield.FileInfo)>> { allFiles };
        }

        long totalCandidates = batches.Sum(b => b.Count);
        if (totalCandidates == 0)
        {
            Console.WriteLine("No bundles matched, nothing to do.");
            return 0;
        }

        Console.WriteLine(
            $"  total candidates = {totalCandidates:#,0} across {batches.Count} batch(es)  "
            + $"| max-memory = {maxMemoryGb} GB  | chunk-batching = {!noChunkBatching}");

        // ── Global counters (persist across batches) ──
        long pngWritten = 0, bundlesProcessed = 0, bundlesFailed = 0;
        long objectsScanned = 0, texturesMatched = 0, texturesDecodeFailed = 0;
        long totalBytes = 0;
        int stage1Extracted = 0;

        // ── Progress task (global) ──
        bool progressEnabled = !Console.IsErrorRedirected;
        var progressCts = new CancellationTokenSource();
        var progressTask = Task.Run(async () =>
        {
            if (!progressEnabled) return;
            try
            {
                while (!progressCts.Token.IsCancellationRequested)
                {
                    PrintProgress(totalCandidates, stage1Extracted, bundlesProcessed, totalBytes);
                    await Task.Delay(500, progressCts.Token);
                }
            }
            catch (TaskCanceledException) { }
        });

        var pngEncoder = encoder;

        // ── Process each batch (chunk) sequentially ──
        int batchIdx = 0;
        foreach (var batch in batches)
        {
            batchIdx++;
            if (batch.Count == 0) continue;

            // ── Compute safe parallelism from memory budget ──
            // 每个 bundle 在 LoadFiles 时的峰值堆开销 ≈ bundle_size × EXPANSION
            //   (LZ4 解压 ~3-5× + Object 实例化 + GC 滞后)
            // 用保守 EXPANSION=15，并按 P95（而非 max）算，避免被单个异常大值拖累
            long maxBundleBytes = batch.Max(x => x.Item3.Length);
            const long EXPANSION = 15;
            long peakPerBundleHeap = maxBundleBytes * EXPANSION;

            // 内存预算对半分：一半给 scratch (channel 里的 bundle)，一半给 heap (正在处理的)
            int effectiveThreads = Math.Max(1, (int)Math.Min(threads,
                maxMemoryBytes / 2 / Math.Max(1, peakPerBundleHeap)));

            // channel cap：既受内存约束，也受硬上限约束（threads × 4），
            // 避免小 bundle 海洋撑爆 channel → /dev/shm 堆积 + producer 跑太快
            int capByMemory = (int)Math.Min(batch.Count, maxMemoryBytes / 2 / Math.Max(1, maxBundleBytes));
            int channelCapacity = Math.Max(effectiveThreads, Math.Min(capByMemory, effectiveThreads * 4));

            if (batches.Count > 1)
            {
                Console.Error.Write('\r');
                Console.Error.WriteLine(
                    $"  batch {batchIdx}/{batches.Count}: {batch.Count} bundles, "
                    + $"max={maxBundleBytes / 1024.0 / 1024.0:F1} MB, "
                    + $"threads={effectiveThreads}, cap={channelCapacity}            ");
            }

            // Per-batch channel
            var channelOpts = new BoundedChannelOptions(capacity: channelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
            };
            var bundleChannel = Channel.CreateBounded<(string path, long size)>(channelOpts);

            // Producer: 串行解 bundle 到 scratch
            var producer = Task.Run(async () =>
            {
                try
                {
                    foreach (var (bt, chunk, file) in batch)
                    {
                        string baseName = Path.GetFileName(file.FileName);
                        string outBundle = Path.Combine(scratch, baseName);
                        using (var fs = new FileStream(outBundle, FileMode.Create, FileAccess.Write,
                            FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                        {
                            loader.ExtractFile(bt, chunk, file, fs);
                        }
                        Interlocked.Increment(ref stage1Extracted);
                        Interlocked.Add(ref totalBytes, file.Length);
                        await bundleChannel.Writer.WriteAsync((outBundle, file.Length));
                    }
                }
                finally
                {
                    bundleChannel.Writer.Complete();
                }
            });

            // Consumers: parallel LoadFiles + decode
            var consumers = new Task[effectiveThreads];
            for (int i = 0; i < effectiveThreads; i++)
            {
                consumers[i] = Task.Run(async () =>
                {
                    var mgr = new AS.AssetsManager
                    {
                        Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
                        Silent = true,
                        SkipProcess = false,
                    };

                    int localCount = 0;
                    await foreach (var (bundlePath, _) in bundleChannel.Reader.ReadAllAsync())
                    {
                        try
                        {
                            mgr.LoadFiles(bundlePath);

                            foreach (var sf in mgr.assetsFileList)
                            {
                                foreach (var obj in sf.Objects)
                                {
                                    Interlocked.Increment(ref objectsScanned);

                                    if (!typeFilters.Contains(obj.GetType().Name)) continue;

                                    if (obj is AS.Texture2D tex)
                                    {
                                        string name = tex.m_Name ?? "";
                                        if (assetRx != null && !assetRx.IsMatch(name)) continue;

                                        Interlocked.Increment(ref texturesMatched);

                                        try
                                        {
                                            using var image = tex.ConvertToImage(flip: true);
                                            if (image is null)
                                            {
                                                Interlocked.Increment(ref texturesDecodeFailed);
                                                continue;
                                            }

                                            string safeName = string.IsNullOrEmpty(name)
                                                ? $"texture_{tex.m_PathID}"
                                                : SanitizeFileName(name);
                                            string outFile = Path.Combine(outPath, safeName + ext);

                                            if (File.Exists(outFile))
                                            {
                                                outFile = Path.Combine(outPath,
                                                    $"{safeName}_{tex.m_PathID:x}{ext}");
                                            }

                                            using var fs = new FileStream(outFile, FileMode.Create,
                                                FileAccess.Write, FileShare.None, 64 * 1024,
                                                FileOptions.SequentialScan);
                                            image.Save(fs, pngEncoder);
                                            Interlocked.Increment(ref pngWritten);
                                        }
                                        catch
                                        {
                                            Interlocked.Increment(ref texturesDecodeFailed);
                                        }
                                    }
                                }
                            }

                            Interlocked.Increment(ref bundlesProcessed);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref bundlesFailed);
                            Console.Error.WriteLine($"  bundle failed: {Path.GetFileName(bundlePath)}: {ex.Message}");
                        }
                        finally
                        {
                            mgr.Clear();
                            if (!keepBundles)
                            {
                                try { File.Delete(bundlePath); } catch { /* ignore */ }
                            }

                            // ── 关键：每 200 个 bundle 强制 LOH 压缩 GC，归还内存给 OS ──
                            // .NET 默认不压缩 LOH，处理大量 bundle 后 RSS 只涨不降。
                            // LargeObjectHeapCompactionMode = CompactOnce + Gen2 collect → 真正归还。
                            localCount++;
                            if (localCount % 200 == 0)
                            {
                                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                                GC.WaitForPendingFinalizers();
                            }
                        }
                    }
                });
            }

            await Task.WhenAll(consumers);
            await producer;

            // Between batches: scratch cleanup + LOH compact GC
            if (!keepBundles)
            {
                try { foreach (var f in Directory.GetFiles(scratch)) File.Delete(f); } catch { }
            }
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        // Stop progress, print final line
        progressCts.Cancel();
        try { await progressTask; } catch { }
        if (progressEnabled)
        {
            PrintProgress(totalCandidates, stage1Extracted, bundlesProcessed, totalBytes);
            Console.Error.WriteLine();
        }

        swTotal.Stop();
        Console.WriteLine(
            $"  bundles processed       = {bundlesProcessed:#,0} ({bundlesFailed:#,0} failed)");
        Console.WriteLine(
            $"  objects scanned         = {objectsScanned:#,0}");
        Console.WriteLine(
            $"  Texture2D matched       = {texturesMatched:#,0}");
        Console.WriteLine(
            $"  images written          = {pngWritten:#,0}  ({outPath})");
        Console.WriteLine(
            $"  decode/save failed      = {texturesDecodeFailed:#,0}");
        Console.WriteLine(
            $"  max-memory-gb           = {maxMemoryGb}");
        Console.WriteLine(
            $"  total wall              = {swTotal.Elapsed.TotalSeconds:F2} s");
        return 0;
    }

    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private static string SanitizeFileName(string name)
    {
        Span<char> buf = stackalloc char[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            buf[i] = Array.IndexOf(InvalidFileNameChars, c) >= 0 ? '_' : c;
        }
        return new string(buf);
    }

    /// <summary>
    /// 用 \r 覆盖输出一行进度，显示 Stage 1 / Stage 2+3 / 累计字节 / images 写出数。
    /// </summary>
    private static void PrintProgress(long total, int stage1Done, long stage2Done, long totalBytes)
    {
        long s1 = Volatile.Read(ref stage1Done);
        long s2 = Interlocked.Read(ref stage2Done);
        long b = Interlocked.Read(ref totalBytes);

        double s1Pct = total > 0 ? s1 * 100.0 / total : 0;
        double s2Pct = total > 0 ? s2 * 100.0 / total : 0;

        string line = total > 1000
            ? $"  progress: stage1 {s1:#,0}/{total:#,0} ({s1Pct:F1}%) | stage2+3 {s2:#,0}/{total:#,0} ({s2Pct:F1}%) | {b / 1024.0 / 1024.0:F1} MiB"
            : $"  progress: stage1 {s1}/{total} ({s1Pct:F1}%) | stage2+3 {s2}/{total} ({s2Pct:F1}%) | {b / 1024.0 / 1024.0:F1} MiB";

        // 用空格填充避免上一行残留字符；用 \r 回到行首覆盖
        Console.Error.Write('\r' + line.PadRight(Math.Max(line.Length + 3, 60)));
    }
}
