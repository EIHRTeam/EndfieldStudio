using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;
using AS = AnimeStudio;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
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
                                [--exclude-material] [--classify]
          endfield-dump audio   --vfs <streaming_assets_path> --out <dir>
                                [--language all|chinese|english|japanese|korean]
                                [--format wem|wav|mp3] [--block all|audio|initialaudio|auditaudio|voice]
                                [--vgmstream <path>] [--ffmpeg <path>]
                                [--mp3-bitrate 192] [--mp3-quality best|high|medium|low|minimum|0-9]
                                [--threads N] [--base-vfs <base_streaming_assets>]
          endfield-dump video   --vfs <streaming_assets_path> --out <dir>
                                [--format mp4|usm] [--block all|video|auditvideo]
                                [--ffmpeg <path>] [--threads N]

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
                "audio" => RunAudio(args.AsSpan(1)),
                "video" => RunVideo(args.AsSpan(1)),
                "inspect-bundle" => RunInspectBundle(args.AsSpan(1)),
                "find-bad-bundles" => RunFindBadBundles(args.AsSpan(1)),
                "classify-bundles" => RunClassifyBundles(args.AsSpan(1)),
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

                    if (bt == BlockType.Lua)
                    {
                        // Lua 文件普遍很小（~KB），后处理需要整块内存
                        string? dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        byte[] data = loader.ExtractFileToBytes(bt, chunk, file);
                        byte[] processed = LuaProcessor.DecryptAndNormalize(data);
                        File.WriteAllBytes(outPath, processed);
                    }
                    else if (bt == BlockType.Table)
                    {
                        // Table 文件 = SparkBuffer 二进制格式，需解析成 JSON
                        // 输出文件名用 SparkBuffer 内 rootDef.name（非 VFS 文件名）
                        byte[] data = loader.ExtractFileToBytes(bt, chunk, file);
                        var (rootName, json) = SparkBuffer.Parse(data);
                        string tableOutDir = Path.Combine(outRoot, "Table");
                        Directory.CreateDirectory(tableOutDir);
                        outPath = Path.Combine(tableOutDir, rootName + ".json");
                        File.WriteAllText(outPath, json);
                    }
                    else
                    {
                        string? dir = Path.GetDirectoryName(outPath);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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

    // ── audio 子命令 ──────────────────────────────────────────────

    private sealed class AudioArgs
    {
        public string? VfsPath;
        public string? OutPath;
        public List<AudioMap.Language> Languages = new();
        public string Format = "wem";  // wem | wav | mp3
        public List<BlockType> Blocks = new();
        public string? VgmstreamPath;
        public string? FfmpegPath;
        public int Mp3Bitrate = 192;  // kbps，仅 mp3 模式使用（默认 192，VBR-quality 折中）
        public int? Mp3Vbr;            // VBR 质量等级 0-9（数字越小越好，0≈245kbps, 4≈165kbps, 9≈65kbps）
        public int Threads;
        public Regex? AudioFilter;     // 映射路径正则过滤器（null = 不过滤）
        public string? BaseVfsPath;    // 基础游戏 StreamingAssets（热更模式下用于回退加载 AudioDialog）
    }

    private enum AudioBlockGroup { All, Audio, InitialAudio, AuditAudio, Voice }

    private static int RunAudio(ReadOnlySpan<string> args)
    {
        var parsed = new AudioArgs();
        var langStr = "all";
        var blockStr = "all";

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
                case "--language":
                case "-l":
                    langStr = RequireValue(args, ref i, a);
                    break;
                case "--format":
                case "-f":
                    parsed.Format = RequireValue(args, ref i, a).ToLowerInvariant();
                    break;
                case "--block":
                case "-b":
                    blockStr = RequireValue(args, ref i, a);
                    break;
                case "--vgmstream":
                    parsed.VgmstreamPath = RequireValue(args, ref i, a);
                    break;
                case "--ffmpeg":
                    parsed.FfmpegPath = RequireValue(args, ref i, a);
                    break;
                case "--mp3-bitrate":
                    parsed.Mp3Bitrate = int.Parse(RequireValue(args, ref i, a));
                    parsed.Mp3Vbr = null;  // CBR 模式覆盖 VBR
                    break;
                case "--mp3-quality":
                case "--mp3-vbr":
                    {
                        string val = RequireValue(args, ref i, a).ToLowerInvariant();
                        // 预设别名
                        parsed.Mp3Vbr = val switch
                        {
                            "best"    => 0,  // ~245 kbps
                            "high"    => 2,  // ~190 kbps
                            "medium"  => 4,  // ~165 kbps
                            "low"     => 6,  // ~115 kbps
                            "minimum" => 9,  // ~65 kbps
                            _ when int.TryParse(val, out int q) && q is >= 0 and <= 9 => q,
                            _ => throw new ArgumentException(
                                $"--mp3-quality must be 0-9 or best|high|medium|low|minimum, got: {val}"),
                        };
                    }
                    break;
                case "--threads":
                case "-t":
                    parsed.Threads = int.Parse(RequireValue(args, ref i, a));
                    break;
                case "--audio-filter":
                    {
                        string pattern = RequireValue(args, ref i, a);
                        parsed.AudioFilter = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    }
                    break;
                case "--operator-only":
                    // 快捷方式：只提取干员语音
                    parsed.AudioFilter = new Regex(@"Characters/chr_\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
                    break;
                case "--base-vfs":
                    // 热更模式：基础游戏 StreamingAssets，用于回退加载 AudioDialog 表
                    parsed.BaseVfsPath = RequireValue(args, ref i, a);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }

        if (parsed.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (parsed.OutPath is null) throw new ArgumentException("--out is required");
        if (parsed.Format is not ("wem" or "wav" or "mp3"))
            throw new ArgumentException($"--format must be wem, wav, or mp3, got: {parsed.Format}");
        if (parsed.Mp3Bitrate < 8 || parsed.Mp3Bitrate > 320)
            throw new ArgumentException($"--mp3-bitrate must be in range [8, 320], got: {parsed.Mp3Bitrate}");

        // 语言
        parsed.Languages = langStr.ToLowerInvariant() switch
        {
            "all" => AudioMap.AllLanguages().ToList(),
            "chinese" or "cn" => new() { AudioMap.Language.Chinese },
            "english" or "en" => new() { AudioMap.Language.English },
            "japanese" or "jp" => new() { AudioMap.Language.Japanese },
            "korean" or "kr" => new() { AudioMap.Language.Korean },
            _ => throw new ArgumentException($"Unknown language: {langStr}"),
        };

        // block group → 具体 BlockType 列表
        var group = blockStr.ToLowerInvariant() switch
        {
            "all" => AudioBlockGroup.All,
            "audio" => AudioBlockGroup.Audio,
            "initialaudio" or "initaudio" => AudioBlockGroup.InitialAudio,
            "auditaudio" => AudioBlockGroup.AuditAudio,
            "voice" => AudioBlockGroup.Voice,
            _ => throw new ArgumentException($"Unknown audio block group: {blockStr}"),
        };

        parsed.Blocks = ResolveAudioBlocks(group, parsed.Languages);

        Directory.CreateDirectory(parsed.OutPath);
        RunAudioPipeline(parsed);
        return 0;
    }

    private static List<BlockType> ResolveAudioBlocks(AudioBlockGroup group, List<AudioMap.Language> languages)
    {
        var blocks = new List<BlockType>();
        if (group is AudioBlockGroup.All or AudioBlockGroup.Audio)
            blocks.Add(BlockType.Audio);
        if (group is AudioBlockGroup.All or AudioBlockGroup.InitialAudio)
            blocks.Add(BlockType.InitialAudio);
        if (group is AudioBlockGroup.All or AudioBlockGroup.AuditAudio)
            blocks.Add(BlockType.AuditAudio);
        if (group is AudioBlockGroup.All or AudioBlockGroup.Voice)
        {
            foreach (var lang in languages)
            {
                blocks.Add(lang switch
                {
                    AudioMap.Language.Chinese => BlockType.AudioChinese,
                    AudioMap.Language.English => BlockType.AudioEnglish,
                    AudioMap.Language.Japanese => BlockType.AudioJapanese,
                    AudioMap.Language.Korean => BlockType.AudioKorean,
                    _ => BlockType.AudioChinese,
                });
            }
        }
        return blocks;
    }

    private static void RunAudioPipeline(AudioArgs parsed)
    {
        var loader = new VfsLoader(parsed.VfsPath!, Keys.ChaCha20Key);
        // 当前生效的输出格式（探测失败会降级到 wem）
        string outFmt = parsed.Format;  // wem | wav | mp3
        bool needVgmstream = outFmt is "wav" or "mp3";
        bool needFfmpeg = outFmt == "mp3";

        // vgmstream 探测
        string? vgmstream = needVgmstream ? parsed.VgmstreamPath : null;
        if (needVgmstream && string.IsNullOrEmpty(vgmstream))
        {
            string vfsAbs = Path.GetFullPath(parsed.VfsPath!);
            string? vfsParent = Path.GetDirectoryName(vfsAbs.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string? gameRoot = vfsParent != null ? Path.GetDirectoryName(vfsParent) : null;
            string cwd = Directory.GetCurrentDirectory();
            string exeDir = AppContext.BaseDirectory;

            var candidates = new List<string>();
            void Add(string? baseDir)
            {
                if (string.IsNullOrEmpty(baseDir)) return;
                candidates.Add(Path.Combine(baseDir, "fluffy-dumper", "vgmstream", "bin", "linux", "vgmstream-cli"));
                candidates.Add(Path.Combine(baseDir, "vgmstream", "bin", "linux", "vgmstream-cli"));
                candidates.Add(Path.Combine(baseDir, "vgmstream-cli"));
            }
            Add(cwd);
            Add(vfsParent);
            Add(gameRoot);
            Add(exeDir);
            candidates.Add("/usr/local/bin/vgmstream-cli");
            candidates.Add("/usr/bin/vgmstream-cli");
            candidates.Add("vgmstream-cli");

            foreach (var c in candidates)
            {
                try
                {
                    if (File.Exists(c))
                    {
                        vgmstream = Path.GetFullPath(c);
                        Console.WriteLine($"  Found vgmstream-cli: {vgmstream}");
                        break;
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(vgmstream))
            {
                Console.WriteLine("  Warning: vgmstream-cli not found, falling back to .wem output.");
                Console.WriteLine("  Hint: pass --vgmstream <path> or place vgmstream-cli in fluffy-dumper/vgmstream/bin/linux/");
                outFmt = "wem";
                needVgmstream = false;
                needFfmpeg = false;
            }
        }

        // ffmpeg 探测（mp3 模式需要）
        string? ffmpeg = needFfmpeg ? parsed.FfmpegPath : null;
        if (needFfmpeg && string.IsNullOrEmpty(ffmpeg))
        {
            var ffCandidates = new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "ffmpeg",
            };
            foreach (var c in ffCandidates)
            {
                try
                {
                    // 尝试在 PATH 中查找
                    if (c == "ffmpeg" || File.Exists(c))
                    {
                        var psi = new ProcessStartInfo
                        {
                            FileName = c,
                            ArgumentList = { "-version" },
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc != null)
                        {
                            if (proc.WaitForExit(3000))
                            {
                                if (proc.ExitCode == 0)
                                {
                                    ffmpeg = c;
                                    Console.WriteLine($"  Found ffmpeg: {c}");
                                    break;
                                }
                            }
                            else
                            {
                                // 超时：进程可能挂死，必须 kill 避免泄漏
                                try { proc.Kill(entireProcessTree: true); } catch { }
                            }
                        }
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Console.WriteLine("  Warning: ffmpeg not found, falling back to .wav output.");
                Console.WriteLine("  Hint: install ffmpeg or pass --ffmpeg <path>");
                outFmt = "wav";
                needFfmpeg = false;
            }
            else
            {
                if (parsed.Mp3Vbr.HasValue)
                {
                    string preset = parsed.Mp3Vbr.Value switch
                    {
                        0 => "best (~245kbps)",
                        2 => "high (~190kbps)",
                        4 => "medium (~165kbps)",
                        6 => "low (~115kbps)",
                        9 => "minimum (~65kbps)",
                        _ => $"Q{parsed.Mp3Vbr.Value}",
                    };
                    Console.WriteLine($"  MP3 mode: VBR {preset}");
                }
                else
                {
                    Console.WriteLine($"  MP3 mode: CBR {parsed.Mp3Bitrate} kbps");
                }
            }
        }

        // 1. 加载 AudioDialog 表（从 Table block）
        Console.WriteLine("Loading AudioDialog table...");
        string? audioDialogJson = LoadAudioDialog(loader);

        // 热更模式回退：当前 VFS 里 AudioDialog 缺失（增量数据，chunk 不在）时，
        // 从基础游戏 StreamingAssets 加载完整 AudioDialog（WEM id 跨版本通常稳定）。
        if (audioDialogJson == null && !string.IsNullOrEmpty(parsed.BaseVfsPath))
        {
            Console.WriteLine($"  AudioDialog missing in hot-update, falling back to base game: {parsed.BaseVfsPath}");
            try
            {
                var baseLoader = new VfsLoader(parsed.BaseVfsPath, Keys.ChaCha20Key);
                audioDialogJson = LoadAudioDialog(baseLoader);
                if (audioDialogJson != null)
                    Console.WriteLine("  Loaded AudioDialog from base game successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: failed to load AudioDialog from base game: {ex.Message}");
            }
        }

        if (audioDialogJson == null)
        {
            Console.WriteLine("  Warning: AudioDialog table not found, all WEMs will be unmapped.");
        }

        int totalSuccess = 0;
        int totalErrors = 0;
        int totalUnmapped = 0;

        foreach (var lang in parsed.Languages)
        {
            Console.WriteLine($"\nProcessing {AudioMap.LanguageName(lang)} audio...");

            var audioMap = audioDialogJson != null
                ? AudioMap.FromAudioDialog(audioDialogJson, lang)
                : new AudioMap();

            if (audioMap.Count > 0)
                Console.WriteLine($"  Found {audioMap.Count} audio entries");

            foreach (var bt in parsed.Blocks)
            {
                Console.WriteLine($"  Extracting {AudioMap.LanguageName(lang)} from {bt.Name()}...");

                List<(string Name, byte[] Data)> pckFiles;
                try
                {
                    pckFiles = ExtractPckFiles(loader, bt);
                }
                catch (DirectoryNotFoundException)
                {
                    Console.WriteLine($"    Skip: no PCK files found in {bt.Name()}");
                    continue;
                }

                if (pckFiles.Count == 0)
                {
                    Console.WriteLine($"    Skip: no PCK files found");
                    continue;
                }

                Console.WriteLine($"    Found {pckFiles.Count} PCK files");

                var opts = new ParallelOptions
                {
                    MaxDegreeOfParallelism = parsed.Threads > 0 ? parsed.Threads : Environment.ProcessorCount,
                };

                int success = 0;
                int errors = 0;
                int unmapped = 0;
                int skipped = 0;
                Regex? audioFilter = parsed.AudioFilter;

                foreach (var (pckName, pckData) in pckFiles)
                {
                    Console.WriteLine($"    Processing {pckName}");

                    AkpkPackage package;
                    try
                    {
                        package = AkpkPackage.Parse(pckData);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"      Error: failed to parse {pckName}: {ex.Message}");
                        errors++;
                        continue;
                    }

                    var entries = package.Entries;
                    int localSuccess = 0;
                    int localErrors = 0;
                    int localUnmapped = 0;
                    int localSkipped = 0;

                    Parallel.ForEach(entries, opts, entry =>
                    {
                        try
                        {
                            byte[] wemData = package.GetWemData(entry);
                            if (wemData.Length < 4)
                            {
                                Interlocked.Increment(ref localErrors);
                                return;
                            }

                            // 校验 RIFF/RIFX 头
                            if (wemData[0] != (byte)'R' || wemData[1] != (byte)'I' ||
                                wemData[2] != (byte)'F' ||
                                (wemData[3] != (byte)'F' && wemData[3] != (byte)'X'))
                            {
                                Interlocked.Increment(ref localErrors);
                                return;
                            }

                            string hash = $"{entry.Id:x}";
                            string outPath;
                            string outExt = "." + outFmt;  // .wem | .wav | .mp3

                            if (audioMap.GetPath(hash) is string mappedPath)
                            {
                                // 正则过滤：如果不匹配则跳过
                                if (audioFilter != null && !audioFilter.IsMatch(mappedPath))
                                {
                                    Interlocked.Increment(ref localSkipped);
                                    return;
                                }
                                string pathWithExt = Path.ChangeExtension(mappedPath, outExt);
                                outPath = Path.Combine(parsed.OutPath!, pathWithExt);
                            }
                            else
                            {
                                // 未映射的文件：导出到 unmapped/<lang>/<id>.<ext>（不再丢弃）
                                Interlocked.Increment(ref localUnmapped);
                                string langDir = AudioMap.LanguageToLower(lang);
                                string unmappedPath = Path.Combine(
                                    parsed.OutPath!, "unmapped", langDir, $"{entry.Id}{outExt}");
                                outPath = unmappedPath;
                            }

                            string? dir = Path.GetDirectoryName(outPath);
                            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                            if (outFmt == "mp3" && vgmstream != null && ffmpeg != null)
                            {
                                WriteMp3ViaPipe(wemData, outPath, vgmstream, ffmpeg, parsed.Mp3Bitrate, parsed.Mp3Vbr);
                            }
                            else if (outFmt == "wav" && vgmstream != null)
                            {
                                WriteWavViaVgmstream(wemData, outPath, vgmstream);
                            }
                            else
                            {
                                File.WriteAllBytes(outPath, wemData);
                            }

                            Interlocked.Increment(ref localSuccess);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref localErrors);
                            Console.Error.WriteLine($"      Error: failed to write {entry.Id}: {ex.Message}");
                        }
                    });

                    success += localSuccess;
                    errors += localErrors;
                    unmapped += localUnmapped;
                    skipped += localSkipped;
                }

                if (audioFilter != null)
                    Console.WriteLine($"    Done: processed {success} entries ({skipped} filtered, {unmapped} unmapped, {errors} errors)");
                else
                    Console.WriteLine($"    Done: processed {success} entries ({unmapped} unmapped, {errors} errors)");
                Interlocked.Add(ref totalSuccess, success);
                Interlocked.Add(ref totalErrors, errors);
                Interlocked.Add(ref totalUnmapped, unmapped);
            }
        }

        Console.WriteLine($"\nComplete: extracted {totalSuccess} files ({totalUnmapped} unmapped, {totalErrors} errors)");

        // 注意：不删除 WavScratchDir 目录本身，否则同一进程内后续调用 RunAudioPipeline
        // 会因为 Lazy<> 不再重新初始化而失败。临时文件已经在 WriteWav/WriteMp3 的 finally 中清理。
    }

    /// <summary>
    /// 从 Table block 加载 AudioDialog 表的 JSON。找不到或 chunk 缺失时返回 null。
    /// 热更模式下部分 chunk 文件可能缺失（增量数据），此处逐文件容错：跳过缺失的 chunk 继续扫描。
    /// </summary>
    private static string? LoadAudioDialog(VfsLoader loader)
    {
        BlockMainInfo tableInfo;
        try { tableInfo = loader.LoadBlockInfo(BlockType.Table); }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: failed to load Table block: {ex.Message}");
            return null;
        }

        foreach (var chunk in tableInfo.Chunks)
        {
            foreach (var file in chunk.Files)
            {
                byte[] data;
                try { data = loader.ExtractFileToBytes(BlockType.Table, chunk, file); }
                catch (FileNotFoundException) { continue; }  // 热更模式：chunk 缺失，跳过该文件
                var (rootName, json) = SparkBuffer.Parse(data);
                if (rootName == "AudioDialog")
                    return json;
            }
        }
        return null;
    }

    private static List<(string Name, byte[] Data)> ExtractPckFiles(VfsLoader loader, BlockType bt)
    {
        var result = new List<(string, byte[])>();
        BlockMainInfo info;
        try { info = loader.LoadBlockInfo(bt); }
        catch (DirectoryNotFoundException) { return result; }

        foreach (var chunk in info.Chunks)
        {
            foreach (var file in chunk.Files)
            {
                if (file.FileName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        byte[] data = loader.ExtractFileToBytes(bt, chunk, file);
                        result.Add((file.FileName, data));
                    }
                    catch (FileNotFoundException)
                    {
                        // 热更模式：chunk 缺失，跳过
                        Console.WriteLine($"  Skip: chunk missing for {file.FileName}");
                    }
                }
            }
        }

        return result;
    }

    // 用于 vgmstream 临时输入文件的目录（优先 /dev/shm，其次系统 temp）
    private static readonly Lazy<string> WavScratchDir = new(() =>
    {
        string baseDir = Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        string dir = Path.Combine(baseDir, $"endfield-wav-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static void WriteWavViaVgmstream(byte[] wemData, string outPath, string vgmstreamCli)
    {
        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 临时 wem 文件放在 /dev/shm（tmpfs，纯内存）下，避免磁盘 IO 干扰
        // 并发安全：每个 wem 用 GUID + 线程 ID 命名，绝不重复
        string tempWem = Path.Combine(
            WavScratchDir.Value,
            $"w_{Environment.CurrentManagedThreadId}_{Guid.NewGuid():N}.wem");
        File.WriteAllBytes(tempWem, wemData);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vgmstreamCli,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(outPath);
            psi.ArgumentList.Add(tempWem);

            using var proc = Process.Start(psi)!;
            // 同时读 stderr/stdout 避免管道阻塞
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            proc.WaitForExit();
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"vgmstream exit {proc.ExitCode}: {stderr}");
            }
        }
        finally
        {
            try { File.Delete(tempWem); } catch { }
        }
    }

    /// <summary>
    /// 流水线：vgmstream-cli 解码 WEM 到 stdout (-p) → ffmpeg 从 stdin 编码 MP3 写到 outPath。
    /// 全程零中间 WAV 文件落盘，节省 IO 和磁盘空间。
    /// </summary>
    private static void WriteMp3ViaPipe(byte[] wemData, string outPath, string vgmstreamCli, string ffmpegPath, int bitrate, int? vbrQuality)
    {
        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 输入 WEM 写到 tmpfs 临时文件（vgmstream-cli 不支持 stdin）
        string tempWem = Path.Combine(
            WavScratchDir.Value,
            $"w_{Environment.CurrentManagedThreadId}_{Guid.NewGuid():N}.wem");
        File.WriteAllBytes(tempWem, wemData);

        Process? vgm = null;
        Process? ff = null;
        try
        {
            // 1. 启动 vgmstream-cli：-p 输出 WAV 到 stdout
            var vgmPsi = new ProcessStartInfo
            {
                FileName = vgmstreamCli,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            vgmPsi.ArgumentList.Add("-p");
            vgmPsi.ArgumentList.Add(tempWem);

            vgm = Process.Start(vgmPsi) ?? throw new InvalidOperationException("启动 vgmstream-cli 失败");

            // 2. 启动 ffmpeg：从 stdin 读 wav，编码 MP3 写 outPath
            var ffPsi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            ffPsi.ArgumentList.Add("-y");                   // 覆盖输出
            ffPsi.ArgumentList.Add("-loglevel"); ffPsi.ArgumentList.Add("error");
            ffPsi.ArgumentList.Add("-i"); ffPsi.ArgumentList.Add("pipe:0");
            ffPsi.ArgumentList.Add("-codec:a"); ffPsi.ArgumentList.Add("libmp3lame");
            if (vbrQuality.HasValue)
            {
                // VBR：-q:a N（N=0~9，越小越好）
                ffPsi.ArgumentList.Add("-q:a"); ffPsi.ArgumentList.Add(vbrQuality.Value.ToString());
            }
            else
            {
                // CBR：-b:a Nk
                ffPsi.ArgumentList.Add("-b:a"); ffPsi.ArgumentList.Add($"{bitrate}k");
            }
            ffPsi.ArgumentList.Add(outPath);

            ff = Process.Start(ffPsi) ?? throw new InvalidOperationException("启动 ffmpeg 失败");

            // 3. vgmstream stdout → ffmpeg stdin（管道转发）
            var copyTask = Task.Run(() =>
            {
                try
                {
                    vgm.StandardOutput.BaseStream.CopyTo(ff.StandardInput.BaseStream);
                }
                finally
                {
                    try { ff.StandardInput.Close(); } catch { }
                }
            });

            // 同时读两边 stderr 防止阻塞
            var vgmErrTask = vgm.StandardError.ReadToEndAsync();
            var ffErrTask = ff.StandardError.ReadToEndAsync();
            // ffmpeg stdout 也读掉避免阻塞
            var ffOutTask = ff.StandardOutput.ReadToEndAsync();

            vgm.WaitForExit();
            if (!ff.WaitForExit(60_000))
            {
                try { ff.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException("ffmpeg 超时 (>60s)，已终止");
            }
            // ffmpeg/vgmstream 已退出后再等 copyTask；若 ffmpeg 提前失败导致 broken pipe,
            // copyTask 会抛 IOException, 这里安全吞掉以让真实 exitCode 错误透出
            try { copyTask.Wait(); } catch { }

            string vgmErr = vgmErrTask.GetAwaiter().GetResult();
            string ffErr = ffErrTask.GetAwaiter().GetResult();

            if (vgm.ExitCode != 0)
                throw new InvalidOperationException($"vgmstream exit {vgm.ExitCode}: {vgmErr}");
            if (ff.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exit {ff.ExitCode}: {ffErr}");
        }
        finally
        {
            // Process.Dispose() 只释放 handle, 不会终止运行中的进程; 主动 kill 避免泄漏
            try { if (vgm is { HasExited: false }) vgm.Kill(entireProcessTree: true); } catch { }
            try { if (ff is { HasExited: false }) ff.Kill(entireProcessTree: true); } catch { }
            try { vgm?.Dispose(); } catch { }
            try { ff?.Dispose(); } catch { }
            try { File.Delete(tempWem); } catch { }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  video 子命令：USM → MP4 转换
    // ─────────────────────────────────────────────────────────────

    private sealed class VideoArgs
    {
        public string? VfsPath;
        public string? OutPath;
        public string Format = "mp4";  // mp4 | usm
        public string Block = "all";   // all | video | auditvideo
        public string? FfmpegPath;
        public int Threads = Environment.ProcessorCount;
    }

    private static int RunVideo(ReadOnlySpan<string> args)
    {
        var parsed = new VideoArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs":
                    parsed.VfsPath = RequireValue(args, ref i, a);
                    break;
                case "--out":
                case "-o":
                    parsed.OutPath = RequireValue(args, ref i, a);
                    break;
                case "--format":
                case "-f":
                    parsed.Format = RequireValue(args, ref i, a).ToLowerInvariant();
                    break;
                case "--block":
                case "-b":
                    parsed.Block = RequireValue(args, ref i, a).ToLowerInvariant();
                    break;
                case "--ffmpeg":
                    parsed.FfmpegPath = RequireValue(args, ref i, a);
                    break;
                case "--threads":
                case "-t":
                    parsed.Threads = int.Parse(RequireValue(args, ref i, a));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }

        if (parsed.VfsPath is null) throw new ArgumentException("--vfs is required");
        if (parsed.OutPath is null) throw new ArgumentException("--out is required");
        if (parsed.Format is not ("mp4" or "usm"))
            throw new ArgumentException($"--format must be mp4 or usm, got: {parsed.Format}");

        var blocks = parsed.Block switch
        {
            "all"       => new[] { BlockType.Video, BlockType.AuditVideo },
            "video"     => new[] { BlockType.Video },
            "auditvideo" => new[] { BlockType.AuditVideo },
            _ => throw new ArgumentException($"Unknown block: {parsed.Block} (expected all|video|auditvideo)"),
        };

        Directory.CreateDirectory(parsed.OutPath);
        RunVideoPipeline(parsed, blocks);
        return 0;
    }

    private static void RunVideoPipeline(VideoArgs parsed, BlockType[] blocks)
    {
        var loader = new VfsLoader(parsed.VfsPath!, Keys.ChaCha20Key);
        bool wantMp4 = parsed.Format == "mp4";

        // ffmpeg 探测
        string? ffmpeg = wantMp4 ? parsed.FfmpegPath : null;
        if (wantMp4 && string.IsNullOrEmpty(ffmpeg))
        {
            var candidates = new[] { "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "ffmpeg" };
            foreach (var c in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = c,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    psi.ArgumentList.Add("-version");
                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        if (proc.WaitForExit(3000))
                        {
                            if (proc.ExitCode == 0)
                            {
                                ffmpeg = c;
                                Console.WriteLine($"  Found ffmpeg: {c}");
                                break;
                            }
                        }
                        else
                        {
                            // 超时：进程可能挂死，必须 kill 避免泄漏
                            try { proc.Kill(entireProcessTree: true); } catch { }
                        }
                    }
                }
                catch { }
            }
            if (string.IsNullOrEmpty(ffmpeg))
            {
                Console.WriteLine("  Warning: ffmpeg not found, falling back to .usm output.");
                Console.WriteLine("  Hint: install ffmpeg or pass --ffmpeg <path>");
                wantMp4 = false;
            }
        }

        // 收集所有 USM 文件
        int skippedChunks = 0;
        var usmFiles = new List<(string Name, byte[] Data, BlockType BT)>();
        foreach (var bt in blocks)
        {
            Console.WriteLine($"Scanning {bt}...");
            BlockMainInfo info;
            try { info = loader.LoadBlockInfo(bt); }
            catch (DirectoryNotFoundException) { continue; }  // 该 block 目录不存在，跳过

            foreach (var chunk in info.Chunks)
            {
                foreach (var file in chunk.Files)
                {
                    if (file.FileName.EndsWith(".usm", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[] data;
                        try { data = loader.ExtractFileToBytes(bt, chunk, file); }
                        catch (FileNotFoundException)
                        {
                            // 热更模式：chunk 缺失（增量数据），跳过该文件
                            skippedChunks++;
                            continue;
                        }
                        usmFiles.Add((file.FileName, data, bt));
                    }
                }
            }
        }

        if (skippedChunks > 0)
            Console.WriteLine($"  (skipped {skippedChunks} USM files whose chunk is missing in this VFS)");
        Console.WriteLine($"Found {usmFiles.Count} USM files");
        if (usmFiles.Count == 0) return;

        int success = 0, errors = 0;
        var locker = new object();

        Parallel.ForEach(usmFiles, new ParallelOptions { MaxDegreeOfParallelism = parsed.Threads }, (usm) =>
        {
            try
            {
                string outName = wantMp4
                    ? Path.ChangeExtension(usm.Name, ".mp4")
                    : usm.Name;
                string outPath = Path.Combine(parsed.OutPath!, outName);
                string? dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                if (wantMp4 && ffmpeg != null)
                {
                    ConvertUsmToMp4(usm.Data, outPath, ffmpeg);
                }
                else
                {
                    File.WriteAllBytes(outPath, usm.Data);
                }

                Interlocked.Increment(ref success);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref errors);
                Console.Error.WriteLine($"  Error: {usm.Name}: {ex.Message}");
            }
        });

        Console.WriteLine($"\nComplete: {success} files converted ({errors} errors)");
    }

    /// <summary>
    /// USM → MP4：demux 提取 m2v + 音频 → ffmpeg 封装 MP4（stream copy，不重编码）。
    /// 临时文件写到 /dev/shm。
    /// </summary>
    private static void ConvertUsmToMp4(byte[] usmData, string outPath, string ffmpegPath)
    {
        var demuxed = UsmDemuxer.Demux(usmData);

        string scratchDir = WavScratchDir.Value;
        string stamp = $"v_{Environment.CurrentManagedThreadId}_{Guid.NewGuid():N}";
        string tempVideo = Path.Combine(scratchDir, $"{stamp}.m2v");
        string? tempAudio = null;

        File.WriteAllBytes(tempVideo, demuxed.Video);
        if (demuxed.Audio != null && demuxed.Audio.Length > 0)
        {
            tempAudio = Path.Combine(scratchDir, $"{stamp}{demuxed.AudioExtension}");
            File.WriteAllBytes(tempAudio, demuxed.Audio);
        }

        try
        {
            // 先尝试视频+音频合并
            bool ok = TryRemuxWithFfmpeg(ffmpegPath, tempVideo, tempAudio, outPath);
            if (!ok && tempAudio != null)
            {
                // 音频合并失败 → 仅视频
                ok = TryRemuxWithFfmpeg(ffmpegPath, tempVideo, null, outPath);
            }
            if (!ok)
                throw new InvalidOperationException("ffmpeg remux failed (see stderr above)");
        }
        finally
        {
            try { File.Delete(tempVideo); } catch { }
            if (tempAudio != null) { try { File.Delete(tempAudio); } catch { }
            }
        }
    }

    private static bool TryRemuxWithFfmpeg(string ffmpegPath, string videoPath, string? audioPath, string outPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,   // 防止 ffmpeg 读终端 stdin 挂死
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(videoPath);
        if (audioPath != null)
        {
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(audioPath);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0:v");
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("1:a");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        }
        else
        {
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
        }
        psi.ArgumentList.Add("-nostdin");  // 明确禁用交互式 stdin
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("启动 ffmpeg 失败");
        proc.StandardInput.Close();  // 立即关闭 stdin，防止 ffmpeg 等待终端输入
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        // 超时保护：防止 ffmpeg 在畸形输入上挂死（60 秒上限）
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("ffmpeg 超时 (>60s)，已终止");
        }
        string stderr = stderrTask.GetAwaiter().GetResult();
        return proc.ExitCode == 0;
    }

    /// <summary>
    /// find-bad-bundles: 扫描所有 VFS bundle，用 per-bundle alloc 检测识别坏 bundle，
    /// 把 hash + 元信息写到 JSON 清单，并把坏 bundle 文件 copy 到 out-dir 供后续研究。
    /// 单线程串行处理，不并发，确保 alloc delta 精确。
    /// </summary>
    private static int RunFindBadBundles(ReadOnlySpan<string> args)
    {
        string? vfsPath = null;
        string outDir = "bad-bundles";
        long allocThresholdMb = 200;
        int limit = 0;
        int saveGoodCount = 0;
        string goodDir = "good-bundles";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs": vfsPath = RequireValue(args, ref i, a); break;
                case "--out-dir": outDir = RequireValue(args, ref i, a); break;
                case "--threshold-mb": allocThresholdMb = long.Parse(RequireValue(args, ref i, a)); break;
                case "--limit": limit = int.Parse(RequireValue(args, ref i, a)); break;
                case "--save-good": saveGoodCount = int.Parse(RequireValue(args, ref i, a)); break;
                case "--good-dir": goodDir = RequireValue(args, ref i, a); break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (vfsPath is null) throw new ArgumentException("--vfs is required");

        Directory.CreateDirectory(outDir);
        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key);
        long allocThresholdBytes = allocThresholdMb * 1024L * 1024L;
        string scratch = Path.Combine(Path.GetTempPath(), "efend-bad-scan");
        Directory.CreateDirectory(scratch);

        var allFiles = new List<(BlockType bt, ChunkInfo chunk, AnimeStudio.Endfield.FileInfo file)>();
        foreach (var bt in new[] { BlockType.Bundle })
        {
            BlockMainInfo info;
            try { info = loader.LoadBlockInfo(bt); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var chunk in info.Chunks)
                foreach (var file in chunk.Files)
                {
                    if (string.IsNullOrEmpty(file.FileName)) continue;
                    if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) continue;
                    allFiles.Add((bt, chunk, file));
                }
        }

        Console.WriteLine($"Scanning {allFiles.Count:N0} bundles (threshold={allocThresholdMb} MB)...");
        var badBundles = new List<(string hash, long fileSize, long allocBytes, double decodeMs, string? error)>();
        int scanned = 0, badCount = 0, goodSaved = 0;
        int[] goodBucketCounts = new int[9];  // 9 个大小区间，每个最多存 saveGoodCount 个
        var sw = Stopwatch.StartNew();

        foreach (var (bt, chunk, file) in allFiles)
        {
            if (limit > 0 && scanned >= limit) break;
            scanned++;
            string hash = Path.GetFileNameWithoutExtension(file.FileName);
            string bundlePath = Path.Combine(scratch, hash + ".ab");

            try
            {
                using (var fs = new FileStream(bundlePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                    loader.ExtractFile(bt, chunk, file, fs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{scanned}/{allFiles.Count}] EXTRACT FAIL: {hash} - {ex.Message}");
                try { File.Delete(bundlePath); } catch { }
                continue;
            }

            // 计算大小桶
            int bucket = file.Length switch
            {
                < 2048 => 0, < 8192 => 1, < 32768 => 2, < 131072 => 3,
                < 524288 => 4, < 2097152 => 5, < 8388608 => 6, < 33554432 => 7, _ => 8
            };

            // 快速采样模式（threshold-mb < 0）：只按大小分桶保存，不 LoadFiles
            if (allocThresholdMb < 0)
            {
                if (saveGoodCount > 0 && goodBucketCounts[bucket] < saveGoodCount)
                {
                    goodBucketCounts[bucket]++;
                    Directory.CreateDirectory(goodDir);
                    string goodFile = Path.Combine(goodDir, $"good_b{bucket}_{hash}.ab");
                    try { File.Copy(bundlePath, goodFile, overwrite: true); } catch { }
                    goodSaved++;
                    Console.WriteLine($"  saved bucket{bucket} {hash} ({file.Length}B) [{goodSaved} total]");
                }
                try { File.Delete(bundlePath); } catch { }
                // 全部桶都满了就停止
                bool allFull = true;
                for (int bi = 0; bi < 9; bi++) if (goodBucketCounts[bi] < saveGoodCount) { allFull = false; break; }
                if (allFull)
                {
                    Console.WriteLine($"All 9 buckets filled, stopping.");
                    break;
                }
                continue;
            }

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var swBundle = Stopwatch.StartNew();
            string? error = null;
            long allocDelta;
            try
            {
                var mgr = new AS.AssetsManager
                {
                    Game = AS.GameManager.GetGame(AS.GameType.ArknightsEndfield),
                    Silent = true, SkipProcess = false,
                };
                // 让 SerializedFile 解析层在超限时立即中断
                mgr.PerFileAllocBudgetBytes = allocThresholdBytes;
                mgr.LoadFiles(bundlePath);
                allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                mgr.Clear();
            }
            catch (Exception ex)
            {
                allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                error = ex.GetType().Name + ": " + ex.Message;
            }
            swBundle.Stop();

            if (allocDelta > allocThresholdBytes)
            {
                badCount++;
                badBundles.Add((hash, file.Length, allocDelta, swBundle.Elapsed.TotalMilliseconds, error));
                string destFile = Path.Combine(outDir, $"alloc{allocDelta / 1024 / 1024}MB_{hash}.ab");
                try { File.Copy(bundlePath, destFile, overwrite: true); } catch { }
                Console.WriteLine($"  [{scanned}/{allFiles.Count}] BAD: {hash} alloc={allocDelta / 1024.0 / 1024.0:F0}MB size={file.Length}B dur={swBundle.Elapsed.TotalMilliseconds:F0}ms → {destFile}");
            }
            else
            {
                goodSaved++;
                // 复用上面算的 bucket
                if (saveGoodCount > 0 && goodBucketCounts[bucket] < saveGoodCount)
                {
                    goodBucketCounts[bucket]++;
                    Directory.CreateDirectory(goodDir);
                    string goodFile = Path.Combine(goodDir, $"good_b{bucket}_{hash}.ab");
                    try { File.Copy(bundlePath, goodFile, overwrite: true); } catch { }
                }
                if (scanned % 1000 == 0)
                {
                    double rate = scanned / sw.Elapsed.TotalSeconds;
                    Console.WriteLine($"  [{scanned}/{allFiles.Count}] OK ({rate:F0}/s, {badCount} bad, {goodSaved} good)");
                }
            }

            try { File.Delete(bundlePath); } catch { }
            if (allocDelta > allocThresholdBytes)
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
        }
        sw.Stop();

        string manifestPath = Path.Combine(outDir, "bad-bundles.json");
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"scan_date\": \"{DateTime.UtcNow:O}\",");
        sb.AppendLine($"  \"total_scanned\": {scanned},");
        sb.AppendLine($"  \"bad_count\": {badCount},");
        sb.AppendLine($"  \"threshold_mb\": {allocThresholdMb},");
        sb.AppendLine($"  \"elapsed_seconds\": {sw.Elapsed.TotalSeconds:F2},");
        sb.AppendLine("  \"bundles\": [");
        for (int i = 0; i < badBundles.Count; i++)
        {
            var b = badBundles[i];
            sb.Append("    {");
            sb.Append($"\"hash\": \"{b.hash}\", ");
            sb.Append($"\"file_size\": {b.fileSize}, ");
            sb.Append($"\"alloc_mb\": {b.allocBytes / 1024.0 / 1024.0:F1}, ");
            sb.Append($"\"decode_ms\": {b.decodeMs:F1}");
            if (b.error != null)
                sb.Append($", \"error\": \"{b.error.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            sb.Append(i < badBundles.Count - 1 ? "}," : "}");
            sb.AppendLine();
        }
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        File.WriteAllText(manifestPath, sb.ToString());

        Console.WriteLine();
        Console.WriteLine($"  Done: {badCount} bad / {scanned} scanned ({sw.Elapsed.TotalSeconds:F1}s)");
        Console.WriteLine($"  Manifest: {Path.GetFullPath(manifestPath)}");
        Console.WriteLine($"  Files: {Path.GetFullPath(outDir)}/");
        return 0;
    }

    /// <summary>
    /// classify-bundles: 只读 VFS header + descramble，不走 LoadFiles，零额外内存分配。
    /// 把每个 bundle 的 descramble 后的 header 字段 dump 出来，用于离线分析好坏 bundle 的差异。
    /// </summary>
    private static int RunClassifyBundles(ReadOnlySpan<string> args)
    {
        string? vfsPath = null;
        string outFile = "bundle-classification.tsv";

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--vfs": vfsPath = RequireValue(args, ref i, a); break;
                case "--out": outFile = RequireValue(args, ref i, a); break;
                default: throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (vfsPath is null) throw new ArgumentException("--vfs is required");

        var loader = new VfsLoader(vfsPath, Keys.ChaCha20Key);

        // 收集所有 bundle
        var allFiles = new List<(BlockType bt, ChunkInfo chunk, AnimeStudio.Endfield.FileInfo file)>();
        foreach (var bt in new[] { BlockType.Bundle })
        {
            BlockMainInfo info;
            try { info = loader.LoadBlockInfo(bt); }
            catch (DirectoryNotFoundException) { continue; }
            foreach (var chunk in info.Chunks)
                foreach (var file in chunk.Files)
                {
                    if (string.IsNullOrEmpty(file.FileName)) continue;
                    if (file.FileName.EndsWith('/') || file.FileName.EndsWith('\\')) continue;
                    allFiles.Add((bt, chunk, file));
                }
        }
        Console.WriteLine($"Classifying {allFiles.Count:N0} bundles (header-only, zero alloc)...");

        using var writer = new StreamWriter(outFile);
        // TSV header
        writer.WriteLine("hash\tfile_size\tcompressedSize\tuncompressedSize\tflags\tencFlags\theaderSize\tblocksCount\tstatus\tnotes");

        int good = 0, bad = 0, error = 0;
        var sw = Stopwatch.StartNew();

        string scratch = Path.Combine(Path.GetTempPath(), "efend-classify");
        Directory.CreateDirectory(scratch);

        foreach (var (bt, chunk, file) in allFiles)
        {
            string hash = Path.GetFileNameWithoutExtension(file.FileName);
            string bundlePath = Path.Combine(scratch, $"_{hash}.ab");

            // 解密到临时文件
            long fileLen = file.Length;
            try
            {
                using (var fs = new FileStream(bundlePath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.SequentialScan))
                {
                    loader.ExtractFile(bt, chunk, file, fs);
                }
            }
            catch (Exception ex)
            {
                writer.WriteLine($"{hash}\t{fileLen}\t-\t-\t-\t-\t-\t-\tEXTRACT_ERROR\t{ex.Message}");
                error++;
                continue;
            }

            // 读取并解析 VFS header（复用 FileReader 逻辑，不进入 LoadFiles）
            string status;
            string notes = "";
            uint csz = 0, usz = 0, flags = 0, encFlags = 0, hdrSize = 0;
            int blocksCount = -1;

            try
            {
                using var reader = new AnimeStudio.FileReader(bundlePath);
                // VFSUtils.IsValidHeader 已在 FileReader.CheckFileType 里调用
                if (reader.FileType != AnimeStudio.FileType.VFSFile)
                {
                    status = "NOT_VFS";
                    notes = $"fileType={reader.FileType}";
                    bad++;
                }
                else
                {
                    // 手动读 header（复刻 VFSFile 构造函数开头）
                    reader.Endian = EndianType.BigEndian;
                    reader.ReadBytes(8); // skip magic
                    var hdr = AnimeStudio.VFSUtils.ReadHeader(reader, GameType.ArknightsEndfield);
                    csz = hdr.compressedBlocksInfoSize;
                    usz = hdr.uncompressedBlocksInfoSize;
                    flags = (uint)hdr.flags;
                    encFlags = hdr.encFlags;
                    hdrSize = (uint)hdr.size;

                    // 简单判别：header 字段是否合理
                    bool sizeOk = usz > 0 && usz < 10_000_000 && csz > 0 && csz <= fileLen + 256;
                    if (sizeOk)
                        status = "GOOD";
                    else
                    {
                        status = "BAD_HEADER";
                        notes = $"usz={usz} csz={csz}";
                        bad++;
                    }
                    if (status == "GOOD") good++;
                }
            }
            catch (Exception ex)
            {
                status = "PARSE_ERROR";
                notes = ex.GetType().Name + ":" + ex.Message.Replace("\t", " ").Replace("\n", " ");
                error++;
            }

            writer.WriteLine($"{hash}\t{fileLen}\t{csz}\t{usz}\t0x{flags:X8}\t0x{encFlags:X8}\t{hdrSize}\t{blocksCount}\t{status}\t{notes}");

            try { File.Delete(bundlePath); } catch { }

            if ((good + bad + error) % 5000 == 0)
            {
                double rate = (good + bad + error) / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"  [{good + bad + error}/{allFiles.Count}] good={good} bad={bad} err={error} ({rate:F0}/s)");
            }
        }
        sw.Stop();

        writer.Flush();
        Console.WriteLine();
        Console.WriteLine($"  Done in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Total: {allFiles.Count}  Good: {good}  Bad: {bad}  Error: {error}");
        Console.WriteLine($"  TSV: {Path.GetFullPath(outFile)}");
        return 0;
    }

    /// <summary>
    /// inspect-bundle: 探查一个未知格式的 bundle 文件，识别 magic、计算分段熵、尝试常见解密变换、
    /// 打印 hex dump 和 ASCII strings，帮助确定文件内部到底是什么。
    /// </summary>
    private static int RunInspectBundle(ReadOnlySpan<string> args)
    {
        string? filePath = null;
        int dumpBytes = 512;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--file":
                case "-f": filePath = RequireValue(args, ref i, a); break;
                case "--dump-bytes": dumpBytes = int.Parse(RequireValue(args, ref i, a)); break;
                default:
                    if (filePath == null) { filePath = a; break; }
                    throw new ArgumentException($"Unknown argument: {a}");
            }
        }
        if (filePath == null) throw new ArgumentException("--file is required (or pass path as positional arg)");
        if (!File.Exists(filePath)) throw new FileNotFoundException(filePath);

        byte[] data = File.ReadAllBytes(filePath);
        Console.WriteLine($"File:       {Path.GetFullPath(filePath)}");
        Console.WriteLine($"Size:       {data.Length:N0} bytes ({data.Length / 1024.0:F2} KB)");
        Console.WriteLine();

        // ── 1. Magic 探测 ──
        Console.WriteLine("── Magic detection ──");
        var magics = new (string name, byte[] sig, int offset)[]
        {
            ("UnityFS",  System.Text.Encoding.ASCII.GetBytes("UnityFS"), 0),
            ("UnityWeb", System.Text.Encoding.ASCII.GetBytes("UnityWeb"), 0),
            ("UnityRaw", System.Text.Encoding.ASCII.GetBytes("UnityRaw"), 0),
            ("Blb\\x02", new byte[]{(byte)'B',(byte)'l',(byte)'b',0x02}, 0),
            ("Blb\\x03", new byte[]{(byte)'B',(byte)'l',(byte)'b',0x03}, 0),
            ("AKPK",     System.Text.Encoding.ASCII.GetBytes("AKPK"), 0),
            ("RIFF",     System.Text.Encoding.ASCII.GetBytes("RIFF"), 0),
            ("RIFX",     System.Text.Encoding.ASCII.GetBytes("RIFX"), 0),
            (":)xD",     System.Text.Encoding.ASCII.GetBytes(":)xD"), 0),
            ("PK\\x03",  new byte[]{0x50,0x4B,0x03,0x04}, 0),
            ("Gzip",     new byte[]{0x1F,0x8B}, 0),
            ("LZ4 Frame",new byte[]{0x04,0x22,0x4D,0x18}, 0),
            ("Zstd",     new byte[]{0x28,0xB5,0x2F,0xFD}, 0),
            ("XZ",       new byte[]{0xFD,(byte)'7',(byte)'z',(byte)'X',(byte)'Z',0x00}, 0),
            ("LZMA",     new byte[]{0x5D,0x00,0x00}, 0),
        };
        bool anyMatch = false;
        foreach (var (name, sig, offset) in magics)
        {
            if (data.Length >= offset + sig.Length &&
                data.AsSpan(offset, sig.Length).SequenceEqual(sig))
            {
                Console.WriteLine($"  ✓ Match at offset {offset}: {name}");
                anyMatch = true;
            }
        }
        if (!anyMatch) Console.WriteLine("  (no known magic at offset 0)");
        Console.WriteLine();

        // ── 2. 分段熵分析 ──
        Console.WriteLine("── Entropy by chunk (256 bytes per chunk, 0=all-zero, 8=random) ──");
        const int chunkSize = 256;
        int chunks = Math.Min(20, (data.Length + chunkSize - 1) / chunkSize);
        for (int c = 0; c < chunks; c++)
        {
            int start = c * chunkSize;
            int len = Math.Min(chunkSize, data.Length - start);
            double h = ShannonEntropy(data, start, len);
            string bar = new string('█', (int)Math.Round(h * 5));
            Console.WriteLine($"  offset 0x{start:X4}-0x{start + len - 1:X4} ({len,3} bytes) entropy={h:F2}  {bar}");
        }
        Console.WriteLine();

        // ── 3. ASCII strings extraction ──
        Console.WriteLine("── ASCII strings (≥6 printable chars) ──");
        var strings = ExtractStrings(data, minLen: 6, maxOutput: 30);
        foreach (var (offset, s) in strings)
            Console.WriteLine($"  0x{offset:X4}: {s}");
        if (strings.Count == 0) Console.WriteLine("  (none found)");
        Console.WriteLine();

        // ── 4. 寻找 Unity 版本/revision 字符串 (常出现在 bundle header 里) ──
        Console.WriteLine("── Unity version probe ──");
        int versionIdx = IndexOfPattern(data, System.Text.Encoding.ASCII.GetBytes("2021."));
        if (versionIdx < 0) versionIdx = IndexOfPattern(data, System.Text.Encoding.ASCII.GetBytes("2020."));
        if (versionIdx < 0) versionIdx = IndexOfPattern(data, System.Text.Encoding.ASCII.GetBytes("2019."));
        if (versionIdx >= 0)
        {
            int end = versionIdx;
            while (end < data.Length && end < versionIdx + 32 && data[end] >= 0x20 && data[end] < 0x7F) end++;
            string ver = System.Text.Encoding.ASCII.GetString(data, versionIdx, end - versionIdx);
            Console.WriteLine($"  Found Unity version string at offset 0x{versionIdx:X}: \"{ver}\"");
            Console.WriteLine($"  → bundle header 不是从 offset 0 开始，前 0x{versionIdx:X} 字节可能是另一层 wrapper/加密");
        }
        else
        {
            Console.WriteLine("  (no Unity version string found)");
        }
        Console.WriteLine();

        // ── 5. 尝试若干常见解密：跳过前 N 字节后是否出现 UnityFS ──
        Console.WriteLine("── Try skipping leading bytes + check for UnityFS ──");
        byte[] unityFs = System.Text.Encoding.ASCII.GetBytes("UnityFS");
        bool found = false;
        for (int skip = 1; skip <= Math.Min(256, data.Length - 8); skip++)
        {
            if (data.AsSpan(skip, 7).SequenceEqual(unityFs))
            {
                Console.WriteLine($"  ✓ UnityFS magic at offset {skip}!");
                found = true;
                break;
            }
        }
        if (!found) Console.WriteLine("  no UnityFS in first 256 bytes");

        // 尝试 XOR with various single-byte keys
        Console.WriteLine();
        Console.WriteLine("── Try single-byte XOR keys, check for UnityFS in first 16 bytes ──");
        bool xorFound = false;
        for (int key = 1; key < 256; key++)
        {
            byte k = (byte)key;
            bool match = true;
            for (int j = 0; j < 7; j++)
            {
                if ((data[j] ^ k) != unityFs[j]) { match = false; break; }
            }
            if (match)
            {
                Console.WriteLine($"  ✓ XOR key 0x{k:X2} produces UnityFS at offset 0");
                xorFound = true;
            }
        }
        if (!xorFound) Console.WriteLine("  no single-byte XOR yields UnityFS");
        Console.WriteLine();

        // ── 6. Hex dump ──
        Console.WriteLine($"── Hex dump (first {Math.Min(dumpBytes, data.Length)} bytes) ──");
        HexDump(data, 0, Math.Min(dumpBytes, data.Length));

        // ── 7. Try to interpret as UnityFS-like header (if anyone reaches here, parse defensively) ──
        Console.WriteLine();
        Console.WriteLine("── Tentative UnityFS-header read (raw bytes, no decryption) ──");
        try
        {
            int pos = 0;
            string sig = ReadCStringAt(data, ref pos, 32);
            uint ver = ReadU32BE(data, ref pos);
            string unityVer = ReadCStringAt(data, ref pos, 32);
            string unityRev = ReadCStringAt(data, ref pos, 32);
            long sz = ReadI64BE(data, ref pos);
            uint csz = ReadU32BE(data, ref pos);
            uint usz = ReadU32BE(data, ref pos);
            uint fl = ReadU32BE(data, ref pos);
            Console.WriteLine($"  signature(读取的字符串至 \\0): \"{Escape(sig)}\"");
            Console.WriteLine($"  version(BE u32): {ver} (0x{ver:X8})");
            Console.WriteLine($"  unityVersion: \"{Escape(unityVer)}\"");
            Console.WriteLine($"  unityRevision: \"{Escape(unityRev)}\"");
            Console.WriteLine($"  size(BE i64): {sz:N0}");
            Console.WriteLine($"  compressedBlocksInfoSize(BE u32):   {csz:N0}  (0x{csz:X8})");
            Console.WriteLine($"  uncompressedBlocksInfoSize(BE u32): {usz:N0}  (0x{usz:X8})   ← OOM 元凶字段");
            Console.WriteLine($"  flags(BE u32): 0x{fl:X8}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (failed to read as UnityFS header: {ex.Message})");
        }

        return 0;
    }

    private static string ReadCStringAt(byte[] data, ref int pos, int maxLen)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < maxLen && pos < data.Length; i++)
        {
            byte b = data[pos++];
            if (b == 0) return sb.ToString();
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static uint ReadU32BE(byte[] data, ref int pos)
    {
        if (pos + 4 > data.Length) throw new EndOfStreamException();
        uint v = ((uint)data[pos] << 24) | ((uint)data[pos + 1] << 16) | ((uint)data[pos + 2] << 8) | data[pos + 3];
        pos += 4;
        return v;
    }

    private static long ReadI64BE(byte[] data, ref int pos)
    {
        if (pos + 8 > data.Length) throw new EndOfStreamException();
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | data[pos + i];
        pos += 8;
        return v;
    }

    private static double ShannonEntropy(byte[] data, int offset, int length)
    {
        if (length <= 0) return 0;
        Span<int> freq = stackalloc int[256];
        for (int i = 0; i < length; i++) freq[data[offset + i]]++;
        double h = 0;
        for (int i = 0; i < 256; i++)
        {
            if (freq[i] == 0) continue;
            double p = (double)freq[i] / length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    private static List<(int offset, string text)> ExtractStrings(byte[] data, int minLen, int maxOutput)
    {
        var result = new List<(int, string)>();
        int start = -1;
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            bool printable = b >= 0x20 && b < 0x7F;
            if (printable && start < 0) start = i;
            else if (!printable && start >= 0)
            {
                int len = i - start;
                if (len >= minLen)
                {
                    result.Add((start, System.Text.Encoding.ASCII.GetString(data, start, len)));
                    if (result.Count >= maxOutput) return result;
                }
                start = -1;
            }
        }
        if (start >= 0 && data.Length - start >= minLen)
            result.Add((start, System.Text.Encoding.ASCII.GetString(data, start, data.Length - start)));
        return result;
    }

    private static int IndexOfPattern(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }

    private static void HexDump(byte[] data, int offset, int length)
    {
        for (int i = 0; i < length; i += 16)
        {
            var sb = new StringBuilder();
            sb.Append($"{offset + i:X8}: ");
            int row = Math.Min(16, length - i);
            for (int j = 0; j < 16; j++)
            {
                if (j < row) sb.Append($"{data[offset + i + j]:x2}");
                else sb.Append("  ");
                if (j == 7) sb.Append(' ');
                sb.Append(' ');
            }
            sb.Append(' ');
            for (int j = 0; j < row; j++)
            {
                byte b = data[offset + i + j];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            Console.WriteLine(sb.ToString());
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else sb.Append($"\\x{(int)c:X2}");
        }
        return sb.ToString();
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

        string scratch = Path.Combine(Path.GetTempPath(), "efend-inspect");
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
        string scratch = Path.Combine(Path.GetTempPath(), "efend-bundles");
        bool keepBundles = false;
        string format = "png";          // png | bmp | tga
        string pngCompression = "none"; // none | fast | default
        int maxMemoryGb = 64;           // 内存预算上限（GB）
        bool noChunkBatching = false;   // 默认按 chunk 分批
        bool excludeMaterial = false;   // 排除材质贴图（T_ 前缀的 PBR 槽位贴图）
        bool classify = false;          // 按类型分目录输出
        double memWatchPercent = 0.9;   // RSS 超过 max-memory-gb × 此比例 触发阀门
        string diagLogPath = "oom-diagnostic.log";
        bool memWatchDisabled = false;
        long perBundleAllocTrapMb = 0;  // 单个 bundle 处理后 GC 分配增量 > 此 MB 就 copy 到 trap dir (0=禁用)
        string trapDir = "oom-trap-bundles";
        long perBundleAllocLimitMb = 512;  // 单个 bundle 线程分配硬上限 MB (默认 512，防 OOM)
        bool skipMissing = false;          // 热更模式：chunk 缺失时跳过而非报错

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
                case "--exclude-material": excludeMaterial = true; break;
                case "--classify": classify = true; break;
                case "--mem-watch-percent": memWatchPercent = double.Parse(RequireValue(args, ref i, a), CultureInfo.InvariantCulture); break;
                case "--diagnostic-log": diagLogPath = RequireValue(args, ref i, a); break;
                case "--no-mem-watch": memWatchDisabled = true; break;
                case "--trap-bundle-alloc-mb": perBundleAllocTrapMb = long.Parse(RequireValue(args, ref i, a)); break;
                case "--trap-dir": trapDir = RequireValue(args, ref i, a); break;
                case "--per-bundle-alloc-limit-mb": perBundleAllocLimitMb = long.Parse(RequireValue(args, ref i, a)); break;
                case "--skip-missing": skipMissing = true; break;
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
                        "none" => PngCompressionLevel.NoCompression,
                        _ => PngCompressionLevel.DefaultCompression,
                    },
                };
                ext = ".png";
                break;
            default:
                throw new ArgumentException($"Unknown --format: {format} (expected png | bmp | tga)");
        }

        Directory.CreateDirectory(outPath);
        Directory.CreateDirectory(scratch);
        if (perBundleAllocTrapMb > 0) Directory.CreateDirectory(trapDir);
        long perBundleAllocTrapBytes = perBundleAllocTrapMb * 1024L * 1024L;
        long trappedBundleCount = 0;

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
        long materialSkipped = 0;
        long totalBytes = 0;
        int stage1Extracted = 0;

        // 跨 batch 的文件名占位表，防止重名纹理互相覆盖
        var claimedNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        // ── 内存阀门 & 诊断采样 ──
        long memWatchThresholdBytes = (long)(maxMemoryBytes * memWatchPercent);
        var killSwitch = new CancellationTokenSource();
        var memSamples = new ConcurrentQueue<(DateTime ts, long rssBytes, long gcHeapBytes, int channelBacklog, long bundlesDone)>();
        const int MaxMemSamples = 600;  // 保留最近 ~2 分钟（200ms 采样）
        var recentBundles = new System.Collections.Concurrent.ConcurrentQueue<(DateTime ts, string name, long size, double decodeMs, int threadId)>();
        const int RecentBundleCap = 200;
        int currentChannelBacklog = 0;
        long lastTriggerRss = 0;
        string? triggeredBy = null;
        int memTripCount = 0;
        long perBundleAllocLimitBytes = perBundleAllocLimitMb * 1024L * 1024L;
        long bundleAllocBlocked = 0;

        void RecordBundle(string name, long size, double ms, int tid)
        {
            recentBundles.Enqueue((DateTime.UtcNow, name, size, ms, tid));
            while (recentBundles.Count > RecentBundleCap)
                recentBundles.TryDequeue(out _);
        }

        var memWatchCts = new CancellationTokenSource();
        var memWatchTask = Task.Run(async () =>
        {
            using var proc = Process.GetCurrentProcess();
            long lastRss = 0;
            int consecutiveJumps = 0;
            const long JUMP_BYTES = 2_000_000_000L;  // 单次采样涨 2GB 视为暴涨
            while (!memWatchCts.Token.IsCancellationRequested)
            {
                try
                {
                    proc.Refresh();
                    long rss = proc.WorkingSet64;
                    long gcHeap = GC.GetTotalMemory(forceFullCollection: false);
                    long done = Interlocked.Read(ref bundlesProcessed);
                    int backlog = Volatile.Read(ref currentChannelBacklog);

                    memSamples.Enqueue((DateTime.UtcNow, rss, gcHeap, backlog, done));
                    while (memSamples.Count > MaxMemSamples) memSamples.TryDequeue(out _);

                    if (memWatchDisabled) { lastRss = rss; }
                    else
                    {
                        // 触发条件 1：RSS 超过阈值
                        if (rss > memWatchThresholdBytes && triggeredBy == null)
                        {
                            triggeredBy = $"RSS exceeded threshold: {rss / 1e9:F2} GB > {memWatchThresholdBytes / 1e9:F2} GB (max={maxMemoryGb} GB × {memWatchPercent:P0})";
                            lastTriggerRss = rss;
                            Interlocked.Increment(ref memTripCount);
                            killSwitch.Cancel();
                        }
                        // 触发条件 2：单次采样涨幅 > JUMP_BYTES，连续 2 次（200ms 间隔→400ms 内涨 4GB）
                        else if (lastRss > 0 && rss - lastRss > JUMP_BYTES)
                        {
                            consecutiveJumps++;
                            if (consecutiveJumps >= 2 && triggeredBy == null)
                            {
                                triggeredBy = $"RSS surge detected: {(rss - lastRss) / 1e9:F2} GB per 200ms, sustained {consecutiveJumps} samples; current RSS={rss / 1e9:F2} GB";
                                lastTriggerRss = rss;
                                Interlocked.Increment(ref memTripCount);
                                killSwitch.Cancel();
                            }
                        }
                        else
                        {
                            consecutiveJumps = 0;
                        }
                        lastRss = rss;
                    }

                    await Task.Delay(200, memWatchCts.Token);
                }
                catch (TaskCanceledException) { break; }
                catch { /* keep watching */ }
            }
        });

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
                        if (killSwitch.IsCancellationRequested) break;
                        string baseName = Path.GetFileName(file.FileName);
                        string outBundle = Path.Combine(scratch, baseName);
                        try
                        {
                            using (var fs = new FileStream(outBundle, FileMode.Create, FileAccess.Write,
                                FileShare.None, 64 * 1024, FileOptions.SequentialScan))
                            {
                                loader.ExtractFile(bt, chunk, file, fs);
                            }
                        }
                        catch (FileNotFoundException) when (skipMissing)
                        {
                            // 热更模式：chunk 文件缺失，跳过此 bundle
                            Interlocked.Increment(ref bundlesFailed);
                            continue;
                        }
                        Interlocked.Increment(ref stage1Extracted);
                        Interlocked.Add(ref totalBytes, file.Length);
                        try
                        {
                            await bundleChannel.Writer.WriteAsync((outBundle, file.Length), killSwitch.Token);
                            Interlocked.Increment(ref currentChannelBacklog);
                        }
                        catch (OperationCanceledException) { break; }
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
                        SkipProcess = true,  // 不自动 ReadAssets，避免 AnimatorController 等构造时 OOM
                    };
                    // 将 per-bundle alloc limit 传入 SerializedFile 解析层，
                    // 在解析过程中超限就立即抛 IOException 中断（而不是等 LoadFiles 返回后才发现）
                    if (perBundleAllocLimitBytes > 0)
                        mgr.PerFileAllocBudgetBytes = perBundleAllocLimitBytes;

                    int localCount = 0;
                    await foreach (var (bundlePath, bundleSize) in bundleChannel.Reader.ReadAllAsync())
                    {
                        Interlocked.Decrement(ref currentChannelBacklog);
                        if (killSwitch.IsCancellationRequested) break;
                        var swBundle = Stopwatch.StartNew();
                        long allocBefore = (perBundleAllocTrapBytes > 0 || perBundleAllocLimitBytes > 0)
                            ? GC.GetAllocatedBytesForCurrentThread()
                            : 0;
                        try
                        {
                            mgr.LoadFiles(bundlePath);

                            long deltaLoad = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

                            // ── Per-bundle alloc 硬限制：LoadFiles 后检查线程分配量 ──
                            if (perBundleAllocLimitBytes > 0)
                            {
                                long allocDelta = deltaLoad;
                                if (allocDelta > perBundleAllocLimitBytes)
                                {
                                    Interlocked.Increment(ref bundleAllocBlocked);
                                    Console.Error.WriteLine($"  BLOCKED: {Path.GetFileName(bundlePath)} alloc={allocDelta / 1024 / 1024}MB > limit={perBundleAllocLimitMb}MB, skipping decode");
                                    // 强制 GC 释放该 bundle 的内存
                                    mgr.Clear();
                                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                                    continue;
                                }
                            }

                            foreach (var sf in mgr.assetsFileList)
                            {
                                // SkipProcess=true 时 Objects 为空，需要手动从 m_Objects 构造目标类型
                                // 只构造 Texture2D，跳过 AnimatorController 等可能 OOM 的类型
                                foreach (var objectInfo in sf.m_Objects)
                                {
                                    Interlocked.Increment(ref objectsScanned);

                                    if (objectInfo.classID != (int)AS.ClassIDType.Texture2D) continue;

                                    AS.Texture2D tex;
                                    try
                                    {
                                        var objectReader = new AS.ObjectReader(sf.reader, sf, objectInfo, mgr.Game);
                                        tex = new AS.Texture2D(objectReader);
                                    }
                                    catch (Exception)
                                    {
                                        continue;
                                    }
                                        string name = tex.m_Name ?? "";
                                        if (assetRx != null && !assetRx.IsMatch(name)) continue;

                                        // 排除材质贴图（T_ 前缀的 PBR 槽位贴图）
                                        if (excludeMaterial && IsMaterialTexture(name))
                                        {
                                            Interlocked.Increment(ref materialSkipped);
                                            continue;
                                        }

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

                                            // 按类型分目录
                                            string targetDir = classify
                                                ? Path.Combine(outPath, ClassifyImage(name))
                                                : outPath;

                                            // 原子占位：先到先得，重名自动加 PathID 后缀
                                            string outFile = Path.Combine(targetDir, safeName + ext);
                                            if (!claimedNames.TryAdd(outFile, 0))
                                            {
                                                outFile = Path.Combine(targetDir,
                                                    $"{safeName}_{tex.m_PathID:x}{ext}");
                                                int dup = 2;
                                                while (!claimedNames.TryAdd(outFile, 0))
                                                {
                                                    outFile = Path.Combine(targetDir,
                                                        $"{safeName}_{tex.m_PathID:x}_{dup}{ext}");
                                                    dup++;
                                                }
                                            }

                                            string? targetParent = Path.GetDirectoryName(outFile);
                                            if (!string.IsNullOrEmpty(targetParent))
                                                Directory.CreateDirectory(targetParent);

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

                            Interlocked.Increment(ref bundlesProcessed);
                        }
                        catch (AS.AllocBudgetExceededException abex)
                        {
                            // 解析过程中预算超限——从 LoadFiles 内部抛出，内存尚未完全爆炸
                            Interlocked.Increment(ref bundleAllocBlocked);
                            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
                            Console.Error.WriteLine($"  BLOCKED (early): {Path.GetFileName(bundlePath)} alloc={allocDelta / 1024 / 1024}MB > limit={perBundleAllocLimitMb}MB");
                            Console.Error.WriteLine($"    → triggered at: {abex.FileName}");
                            mgr.Clear();
                            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                            continue;
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref bundlesFailed);
                            Console.Error.WriteLine($"  bundle failed: {Path.GetFileName(bundlePath)}: {ex.Message}");
                        }
                        finally
                        {
                            mgr.Clear();
                            swBundle.Stop();
                            RecordBundle(Path.GetFileName(bundlePath), bundleSize, swBundle.Elapsed.TotalMilliseconds, Environment.CurrentManagedThreadId);

                            // ── Trap：单 bundle 分配增量超阈值 → 拷贝到 trap dir ──
                            if (perBundleAllocTrapBytes > 0)
                            {
                                long allocAfter = GC.GetAllocatedBytesForCurrentThread();
                                long delta = allocAfter - allocBefore;
                                if (delta > perBundleAllocTrapBytes)
                                {
                                    try
                                    {
                                        string trapName = $"alloc{delta / 1024 / 1024}MB_dur{(long)swBundle.Elapsed.TotalMilliseconds}ms_{Path.GetFileName(bundlePath)}";
                                        string trapPath = Path.Combine(trapDir, trapName);
                                        File.Copy(bundlePath, trapPath, overwrite: true);
                                        Interlocked.Increment(ref trappedBundleCount);
                                        Console.Error.WriteLine();
                                        Console.Error.WriteLine($"  TRAPPED: {Path.GetFileName(bundlePath)} alloc={delta / 1024 / 1024}MB dur={(long)swBundle.Elapsed.TotalMilliseconds}ms → {trapPath}");
                                    }
                                    catch (Exception copyEx)
                                    {
                                        Console.Error.WriteLine($"  TRAP copy failed: {copyEx.Message}");
                                    }
                                }
                            }

                            // 阀门触发或正常运行：决定是否保留 bundle
                            bool keepThisBundle = keepBundles || killSwitch.IsCancellationRequested;
                            if (!keepThisBundle)
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
            if (!keepBundles && !killSwitch.IsCancellationRequested)
            {
                try { foreach (var f in Directory.GetFiles(scratch)) File.Delete(f); } catch { }
            }
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            if (killSwitch.IsCancellationRequested)
            {
                // 阀门触发：停止后续 batch
                break;
            }
        }

        // Stop progress + memwatch, print final line
        progressCts.Cancel();
        memWatchCts.Cancel();
        try { await progressTask; } catch { }
        try { await memWatchTask; } catch { }
        if (progressEnabled)
        {
            PrintProgress(totalCandidates, stage1Extracted, bundlesProcessed, totalBytes);
            Console.Error.WriteLine();
        }

        // 如果触发了内存阀门，写诊断日志
        if (triggeredBy != null)
        {
            try
            {
                WriteOomDiagnostic(
                    diagLogPath, triggeredBy, lastTriggerRss, maxMemoryGb, memWatchPercent,
                    swTotal, bundlesProcessed, bundlesFailed, stage1Extracted, totalCandidates,
                    pngWritten, texturesMatched, texturesDecodeFailed,
                    threads, currentChannelBacklog,
                    memSamples, recentBundles);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"!! MEMORY WATCHDOG TRIPPED: {triggeredBy}");
                Console.Error.WriteLine($"!! Diagnostic written to: {Path.GetFullPath(diagLogPath)}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"!! Failed to write diagnostic log: {ex.Message}");
            }
        }

        swTotal.Stop();
        Console.WriteLine(
            $"  bundles processed       = {bundlesProcessed:#,0} ({bundlesFailed:#,0} failed)");
        Console.WriteLine(
            $"  objects scanned         = {objectsScanned:#,0}");
        Console.WriteLine(
            $"  Texture2D matched       = {texturesMatched:#,0}");
        if (excludeMaterial)
        {
            Console.WriteLine(
            $"  material textures skip  = {materialSkipped:#,0}");
        }
        Console.WriteLine(
            $"  images written          = {pngWritten:#,0}  ({outPath})");
        Console.WriteLine(
            $"  decode/save failed      = {texturesDecodeFailed:#,0}");
        Console.WriteLine(
            $"  max-memory-gb           = {maxMemoryGb}");
        if (perBundleAllocTrapMb > 0)
            Console.WriteLine(
            $"  trapped bundles         = {trappedBundleCount:#,0}  ({Path.GetFullPath(trapDir)})");
        if (perBundleAllocLimitMb > 0)
            Console.WriteLine(
            $"  blocked (alloc limit)   = {bundleAllocBlocked:#,0}  (limit={perBundleAllocLimitMb} MB/bundle)");
        Console.WriteLine(
            $"  total wall              = {swTotal.Elapsed.TotalSeconds:F2} s");
        return triggeredBy != null ? 3 : 0;
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
    /// 判断是否为非可视的引擎数据贴图（材质 PBR 槽位 + 地形/高度图/遮罩）。
    /// 这些是引擎运行时使用的数据纹理，不是给人看的图片。
    /// </summary>
    private static bool IsMaterialTexture(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        // T_ 前缀 = Unity 材质贴图约定（T_model_D/N/E/P/M/AO/BC/S/Mask 等）
        if (name.Length > 2 && (name[0] == 'T' || name[0] == 't') && name[1] == '_')
            return true;

        // Terrain 前缀 = 地形引擎数据（splat/height/index map，71k+ 文件）
        if (name.StartsWith("Terrain", StringComparison.OrdinalIgnoreCase))
            return true;

        var lower = name.ToLowerInvariant();
        var lowerSpan = lower.AsSpan();

        // h_ / m_ / l_ 前缀 = heightmap / metallic / LOD 数据图
        if (lower.Length >= 2 && lower[1] == '_' && (lower[0] == 'h' || lower[0] == 'm' || lower[0] == 'l'))
            return true;

        // LAYER 前缀 = 图层遮罩
        if (lower.StartsWith("layer"))
            return true;

        // mask_ 前缀 = 遮罩图
        if (lower.StartsWith("mask_") || lower.StartsWith("mask"))
            return true;

        // SplatIndexMap / etchlist 等地形辅助数据
        if (lower.StartsWith("splatindexmap") || lower.StartsWith("etchlist"))
            return true;

        // 无 T_ 前缀但带明确材质槽位全名
        return lowerSpan.EndsWith("_BaseMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_BumpMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_NormalMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_EmissionMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_MetallicGlossMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_MaskMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_OcclusionMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_DetailMap", StringComparison.OrdinalIgnoreCase)
            || lowerSpan.EndsWith("_SpecGlossMap", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按图片命名前缀分类到子目录。
    /// 基于 Endfield 实际命名规则：pic_ = 立绘, icon_ = 图标, item_ = 道具, 等。
    /// </summary>
    private static string ClassifyImage(string name)
    {
        if (string.IsNullOrEmpty(name)) return "other";
        var lower = name.ToLowerInvariant();

        if (lower.StartsWith("pic_")) return "character";
        if (lower.StartsWith("icon_round")) return "icon_round";
        if (lower.StartsWith("icon_")) return "icon";
        if (lower.StartsWith("item_topic")) return "item_topic";
        if (lower.StartsWith("item_potential")) return "item_potential";
        if (lower.StartsWith("item_")) return "item";
        if (lower.StartsWith("business_card")) return "business_card";
        if (lower.StartsWith("sns_")) return "sns";
        if (lower.StartsWith("bg_") || lower.StartsWith("background") || lower.StartsWith("activity_bg")) return "background";
        if (lower.StartsWith("logo")) return "logo";
        if (lower.StartsWith("loading")) return "loading";
        if (lower.StartsWith("tutorial")) return "tutorial";
        if (lower.StartsWith("cg_")) return "cg";
        if (lower.StartsWith("splash")) return "splash";
        if (lower.StartsWith("chr_")) return "chr_thumb";
        if (lower.StartsWith("title")) return "title";
        if (lower.StartsWith("tips")) return "tips";
        if (lower.StartsWith("achv_")) return "achievement";
        if (lower.StartsWith("wpn_")) return "weapon";
        if (lower.StartsWith("activity_")) return "activity";
        if (lower.StartsWith("prts_")) return "prts";
        if (lower.StartsWith("dung")) return "dungeon";
        if (lower.StartsWith("slu__dung")) return "dungeon";  // Slu__dung02_xxx 关卡截图
        if (lower.StartsWith("slu__map")) return "map";       // Slu__map02_xxx 地图截图
        if (lower.StartsWith("slu__ld")) return "level";      // Slu__ld_xxx 关卡截图
        if (lower.StartsWith("slu__")) return "snapshot";     // 其他 Slu__ 通用截图
        if (lower.StartsWith("dlg_")) return "dialog";
        if (lower.StartsWith("gacha")) return "gacha";
        if (lower.StartsWith("image_")) return "image";

        // 新增：UI 装饰类
        if (lower.StartsWith("deco_") || lower.StartsWith("deco-") ||
            lower.StartsWith("line_") || lower.StartsWith("line-")) return "decoration";
        if (lower.StartsWith("btn_") || lower.StartsWith("btn-")) return "button";
        if (lower.StartsWith("common_") || lower.StartsWith("common-")) return "common_ui";
        if (lower.StartsWith("uisprite")) return "ui_sprite";
        if (lower.StartsWith("emoji_") || lower.StartsWith("emoji-")) return "emoji";

        // 新增：系统/玩法类
        if (lower.StartsWith("guide_") || lower.StartsWith("guide-")) return "guide";
        if (lower.StartsWith("tech_") || lower.StartsWith("tech-")) return "tech";
        if (lower.StartsWith("eny_") || lower.StartsWith("eny-") ||
            lower.StartsWith("enemy_") || lower.StartsWith("enemy-")) return "enemy";
        if (lower.StartsWith("wiki_") || lower.StartsWith("wiki-")) return "wiki";
        if (lower.StartsWith("shop_") || lower.StartsWith("shop-") ||
            lower.StartsWith("monthlypass")) return "shop";
        if (lower.StartsWith("map_") || lower.StartsWith("map-")) return "map";
        if (lower.StartsWith("collection_") || lower.StartsWith("collection-")) return "collection";
        if (lower.StartsWith("document_") || lower.StartsWith("document-")) return "document";
        if (lower.StartsWith("seasonal_") || lower.StartsWith("seasonal-")) return "seasonal";

        // 新增：游戏系统类
        if (lower.StartsWith("textfactorycommonui")) return "factory_ui";
        if (lower.StartsWith("dwr_") || lower.StartsWith("dwr-")) return "dwr";  // 探索系统
        if (lower.StartsWith("facskill_") || lower.StartsWith("facskill-")) return "factory_skill";
        if (lower.StartsWith("aibark_") || lower.StartsWith("aibark-")) return "aibark";
        if (lower.StartsWith("reception_") || lower.StartsWith("reception-")) return "reception";
        if (lower.StartsWith("racing_") || lower.StartsWith("racing-")) return "racing";
        if (lower.StartsWith("remotecomm_") || lower.StartsWith("remotecomm-")) return "remotecomm";
        if (lower.StartsWith("achievement_") || lower.StartsWith("achievement-")) return "achievement";
        if (lower.StartsWith("potential_") || lower.StartsWith("potential-")) return "item_potential";
        if (lower.StartsWith("weapon_") || lower.StartsWith("weapon-")) return "weapon";
        if (lower.StartsWith("boss_") || lower.StartsWith("boss-")) return "boss";
        if (lower.StartsWith("snapshot_") || lower.StartsWith("snapshot-")) return "snapshot";
        if (lower.StartsWith("poster_") || lower.StartsWith("poster-")) return "poster";
        if (lower.StartsWith("adventure_") || lower.StartsWith("adventure-")) return "adventure";
        if (lower.StartsWith("mail_") || lower.StartsWith("mail-")) return "mail";
        if (lower.StartsWith("chapter_") || lower.StartsWith("chapter-")) return "chapter";
        if (lower.StartsWith("cover_") || lower.StartsWith("cover-")) return "cover";
        if (lower.StartsWith("reading_") || lower.StartsWith("reading-")) return "reading";
        if (lower.StartsWith("text_") || lower.StartsWith("text-")) return "text";
        if (lower.StartsWith("img_") || lower.StartsWith("img-")) return "image";
        if (lower.StartsWith("ui_") || lower.StartsWith("ui-")) return "ui";
        if (lower.StartsWith("prgs_") || lower.StartsWith("prgs-")) return "progress";
        if (lower.StartsWith("decal_") || lower.StartsWith("decal-")) return "decal";
        if (lower.StartsWith("map02") || lower.StartsWith("map03")) return "map";

        return "other";
    }

    /// <summary>
    /// 写 OOM 触发时的诊断快照：触发原因、计数器、最近 N 个 bundle 处理记录、内存采样曲线。
    /// </summary>
    private static void WriteOomDiagnostic(
        string logPath,
        string triggeredBy,
        long lastTriggerRss,
        int maxMemoryGb,
        double memWatchPercent,
        Stopwatch swTotal,
        long bundlesProcessed,
        long bundlesFailed,
        int stage1Extracted,
        long totalCandidates,
        long pngWritten,
        long texturesMatched,
        long texturesDecodeFailed,
        int threads,
        int currentChannelBacklog,
        ConcurrentQueue<(DateTime ts, long rssBytes, long gcHeapBytes, int channelBacklog, long bundlesDone)> memSamples,
        ConcurrentQueue<(DateTime ts, string name, long size, double decodeMs, int threadId)> recentBundles)
    {
        var sb = new StringBuilder();
        sb.AppendLine("════════════════════════════════════════════════════════════════");
        sb.AppendLine("  ENDFIELD-DUMP MEMORY WATCHDOG DIAGNOSTIC");
        sb.AppendLine("════════════════════════════════════════════════════════════════");
        sb.AppendLine($"Timestamp:       {DateTime.UtcNow:O}");
        sb.AppendLine($"Triggered by:    {triggeredBy}");
        sb.AppendLine($"Trigger RSS:     {lastTriggerRss / 1e9:F2} GB");
        sb.AppendLine($"Threshold:       max-memory-gb={maxMemoryGb} × percent={memWatchPercent:P0} = {maxMemoryGb * memWatchPercent:F2} GB");
        sb.AppendLine($"Wall elapsed:    {swTotal.Elapsed.TotalSeconds:F2} s");
        sb.AppendLine();
        sb.AppendLine("── Counters ──────────────────────────────────────────────────");
        sb.AppendLine($"  threads (cli)         = {threads}");
        sb.AppendLine($"  totalCandidates       = {totalCandidates:#,0}");
        sb.AppendLine($"  stage1 extracted      = {stage1Extracted:#,0}  ({(totalCandidates > 0 ? stage1Extracted * 100.0 / totalCandidates : 0):F1}%)");
        sb.AppendLine($"  bundlesProcessed      = {bundlesProcessed:#,0}  ({(totalCandidates > 0 ? bundlesProcessed * 100.0 / totalCandidates : 0):F1}%)");
        sb.AppendLine($"  bundlesFailed         = {bundlesFailed:#,0}");
        sb.AppendLine($"  channelBacklog (now)  = {currentChannelBacklog}");
        sb.AppendLine($"  texturesMatched       = {texturesMatched:#,0}");
        sb.AppendLine($"  texturesDecodeFailed  = {texturesDecodeFailed:#,0}");
        sb.AppendLine($"  pngWritten            = {pngWritten:#,0}");
        sb.AppendLine();

        // 内存采样（最后 60 个，每秒一次）
        var samples = memSamples.ToArray();
        int sampleTake = Math.Min(60, samples.Length);
        sb.AppendLine($"── Memory samples (last {sampleTake} of {samples.Length}, 1s interval) ──");
        sb.AppendLine($"  {"timestamp",-30}  {"RSS_GB",10}  {"GCheap_GB",10}  {"backlog",8}  {"bundlesDone",12}");
        foreach (var s in samples.Skip(Math.Max(0, samples.Length - sampleTake)))
        {
            sb.AppendLine($"  {s.ts:HH:mm:ss.fff,-30}  {s.rssBytes / 1e9,10:F3}  {s.gcHeapBytes / 1e9,10:F3}  {s.channelBacklog,8}  {s.bundlesDone,12:#,0}");
        }
        sb.AppendLine();

        // 最近处理的 bundle（按耗时倒序 top 30 + 按时间倒序 top 50）
        var bundles = recentBundles.ToArray();
        sb.AppendLine($"── Last {bundles.Length} processed bundles (top 30 SLOWEST) ──");
        sb.AppendLine($"  {"decode_ms",10}  {"size_MB",10}  {"tid",5}  {"timestamp",-25}  name");
        foreach (var b in bundles.OrderByDescending(x => x.decodeMs).Take(30))
        {
            sb.AppendLine($"  {b.decodeMs,10:F1}  {b.size / 1024.0 / 1024.0,10:F2}  {b.threadId,5}  {b.ts:HH:mm:ss.fff,-25}  {b.name}");
        }
        sb.AppendLine();

        sb.AppendLine($"── Last {Math.Min(50, bundles.Length)} processed bundles (by time, newest first) ──");
        sb.AppendLine($"  {"timestamp",-25}  {"decode_ms",10}  {"size_MB",10}  {"tid",5}  name");
        foreach (var b in bundles.OrderByDescending(x => x.ts).Take(50))
        {
            sb.AppendLine($"  {b.ts:HH:mm:ss.fff,-25}  {b.decodeMs,10:F1}  {b.size / 1024.0 / 1024.0,10:F2}  {b.threadId,5}  {b.name}");
        }
        sb.AppendLine();

        // 大小排行（top 30）
        sb.AppendLine($"── Last {bundles.Length} processed bundles (top 30 LARGEST) ──");
        sb.AppendLine($"  {"size_MB",10}  {"decode_ms",10}  {"tid",5}  {"timestamp",-25}  name");
        foreach (var b in bundles.OrderByDescending(x => x.size).Take(30))
        {
            sb.AppendLine($"  {b.size / 1024.0 / 1024.0,10:F2}  {b.decodeMs,10:F1}  {b.threadId,5}  {b.ts:HH:mm:ss.fff,-25}  {b.name}");
        }
        sb.AppendLine();

        sb.AppendLine("════════════════════════════════════════════════════════════════");
        File.WriteAllText(logPath, sb.ToString());
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
