using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AnimeStudio.Endfield.Tests")]

namespace AnimeStudio.Endfield.Cli;

internal static partial class Program
{
    internal readonly record struct AudioPackageResult(int Success, int Errors, int Unmapped, int Skipped, int Plugins);

    // Shared by all audio block groups; selection precedes payload reads/decoding.
    internal static AudioPackageResult ExtractAudioPackage(
        AkpkPackage package, AudioMap audioMap, AudioArgs parsed,
        string pckName, BlockType bt, string outFmt, string? vgmstream, string? ffmpeg)
    {
        var opts = new ParallelOptions
        {
            MaxDegreeOfParallelism = parsed.Threads > 0 ? parsed.Threads : Environment.ProcessorCount,
        };
        var audioFilter = parsed.AudioFilter;
        int localSuccess = 0;
        int localErrors = 0;
        int localUnmapped = 0;
        int localSkipped = 0;
        int localPlugins = 0;

        Parallel.ForEach(package.Entries, opts, entry =>
        {
            try
            {
                string hash = $"{entry.Id:x}";
                string outPath;
                string outExt = "." + outFmt;  // .wem | .wav | .mp3

                // 用 FNV-1a 64 hash 匹配（多语言 hash map 已包含所有语言的路径）
                string? mappedPath = audioMap.GetPath(hash);

                if (mappedPath is string mp)
                {
                    // 正则过滤：如果不匹配则跳过
                    if (audioFilter != null && !audioFilter.IsMatch(mp))
                    {
                        Interlocked.Increment(ref localSkipped);
                        return;
                    }
                    string pathWithExt = Path.ChangeExtension(mp, outExt);
                    outPath = Path.Combine(parsed.OutPath!, pathWithExt);
                }
                else
                {
                    // 启用路径过滤时，未映射条目不可能证明属于目标集合。
                    // 直接跳过，避免 --operator-only / 档案语音精确导出时
                    // 把整个 PCK 的 unmapped 音频写入输出目录。
                    if (audioFilter != null || parsed.SkipUnmapped)
                    {
                        Interlocked.Increment(ref localSkipped);
                        return;
                    }

                    // 未映射的文件：导出到 unmapped/<id>.<ext>
                    Interlocked.Increment(ref localUnmapped);
                    string unmappedPath = Path.Combine(
                        parsed.OutPath!, "unmapped", $"{entry.Id}{outExt}");
                    outPath = unmappedPath;
                }

                // Resolve the requested paths before touching WEM payloads.
                // An unrelated or unmapped entry must not consume decoding work
                // or make a filtered character export fail.
                byte[] wemData = package.GetWemData(entry);
                if (wemData.Length < 4)
                {
                    Interlocked.Increment(ref localErrors);
                    return;
                }

                // PLUG entries contain Wwise plugin state rather than PCM/WEM.
                // Preserve them byte-for-byte during an unfiltered resource export;
                // passing them to vgmstream or decrypting them as WEM destroys them.
                if (AkpkPackage.IsPluginData(wemData))
                {
                    if (audioFilter != null || parsed.SkipUnmapped)
                    {
                        Interlocked.Increment(ref localSkipped);
                        return;
                    }
                    string pluginPath = Path.Combine(parsed.OutPath!, "plugins", bt.Name(),
                        Path.GetFileNameWithoutExtension(pckName), $"{entry.Id}.plug");
                    Directory.CreateDirectory(Path.GetDirectoryName(pluginPath)!);
                    File.WriteAllBytes(pluginPath, wemData);
                    Interlocked.Increment(ref localPlugins);
                    Interlocked.Increment(ref localSuccess);
                    return;
                }

                // 校验 RIFF/RIFX 头；未知格式继续视为失败。
                if (wemData[0] != (byte)'R' || wemData[1] != (byte)'I' ||
                    wemData[2] != (byte)'F' ||
                    (wemData[3] != (byte)'F' && wemData[3] != (byte)'X'))
                {
                    Interlocked.Increment(ref localErrors);
                    return;
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
        return new(localSuccess, localErrors, localUnmapped, localSkipped, localPlugins);
    }
}
