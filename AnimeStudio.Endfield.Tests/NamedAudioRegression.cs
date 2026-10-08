using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;
using CliProgram = AnimeStudio.Endfield.Cli.Program;

internal static class NamedAudioRegression
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "endfield-named-audio-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string characterPath = "Characters/chr_0038_purrche/CN_001";
            const string numericPath = "Story/evt_2026/000123";
            const string otherPath = "Characters/chr_9999_other/CN_001";
            var map = AudioMap.FromAudioDialogMultiLanguage(
                "{\"character\":{\"path\":\"" + characterPath + "\"},\"numeric\":{\"path\":\"" + numericPath +
                "\"},\"other\":{\"path\":\"" + otherPath + "\"}}", AudioMap.AllLanguages());
            byte[] wem = Encoding.ASCII.GetBytes("RIFF\x08\x00\x00\x00WAVEtest");
            byte[] plug = Encoding.ASCII.GetBytes("PLUG\x08\x00\x00\x00hashdata");
            int count = 0;

            // Every language and voice/shared/hotfix group shares this exact writer.
            // Mapped IDs and path components containing digits must survive the policy.
            foreach (var language in AudioMap.AllLanguages())
            foreach (var block in new[] { BlockType.AudioChinese, BlockType.AudioEnglish, BlockType.AudioJapanese,
                BlockType.AudioKorean, BlockType.Audio, BlockType.InitialAudio, BlockType.AuditAudio, BlockType.HotfixAudio })
            {
                string lang = AudioMap.LanguageToLower(language);
                var package = Package(wem, Id(characterPath, lang));
                var args = Args(root, ++count, skipUnmapped: true);
                var result = CliProgram.ExtractAudioPackage(package, map, args, "fixture.pck", block, "wem", null, null);
                Check(result.Success == 1 && result.Skipped == 0 && result.Errors == 0 && result.Unmapped == 0,
                    "Mapped character must be exported for every language/block");
                string path = Path.Combine(args.OutPath!, AudioMap.MakeVoicePath(characterPath, lang) + ".wem");
                Check(File.ReadAllBytes(path).SequenceEqual(wem), "Named character bytes must be unchanged");
            }

            var numericArgs = Args(root, ++count, skipUnmapped: true);
            var numericResult = CliProgram.ExtractAudioPackage(Package(wem, Id(numericPath, "english")), map,
                numericArgs, "fixture.pck", BlockType.HotfixAudio, "wem", null, null);
            Check(numericResult.Success == 1 && File.Exists(Path.Combine(numericArgs.OutPath!,
                AudioMap.MakeVoicePath(numericPath, "english") + ".wem")),
                "A numeric leaf under a resolved story path is traceable and must survive");

            var unmappedArgs = Args(root, ++count, skipUnmapped: true);
            var unmapped = Package(wem, 1234);
            unmapped.Entries[0].Offset = ulong.MaxValue; // Any payload access would fail.
            var unmappedResult = CliProgram.ExtractAudioPackage(unmapped, map, unmappedArgs,
                "fixture.pck", BlockType.Audio, "wav", "/nonexistent/vgmstream-regression", null);
            Check(unmappedResult.Skipped == 1 && unmappedResult.Success == 0 && unmappedResult.Errors == 0,
                "Unmapped data must be skipped before payload access and decoder invocation");
            NoFiles(unmappedArgs.OutPath!);

            var noMapArgs = Args(root, ++count, skipUnmapped: true);
            var noMapResult = CliProgram.ExtractAudioPackage(unmapped, new AudioMap(), noMapArgs,
                "fixture.pck", BlockType.InitialAudio, "wav", "/nonexistent/vgmstream-regression", null);
            Check(noMapResult.Skipped == 1 && noMapResult.Errors == 0, "Missing mapping must not restore numeric fallback");
            NoFiles(noMapArgs.OutPath!);

            var filteredArgs = Args(root, ++count, skipUnmapped: false);
            filteredArgs.AudioFilter = new Regex(@"^voice/chinese/characters/chr_0038_purrche/cn_001$", RegexOptions.IgnoreCase);
            var unrelated = Package(wem, Id(otherPath, "chinese"));
            unrelated.Entries[0].Offset = ulong.MaxValue;
            var unrelatedResult = CliProgram.ExtractAudioPackage(unrelated, map, filteredArgs,
                "fixture.pck", BlockType.HotfixAudio, "wav", "/nonexistent/vgmstream-regression", null);
            Check(unrelatedResult.Skipped == 1 && unrelatedResult.Errors == 0, "Exact archive filter must skip unrelated payload before reads");
            var filteredUnknown = CliProgram.ExtractAudioPackage(unmapped, map, filteredArgs,
                "fixture.pck", BlockType.Audio, "wav", "/nonexistent/vgmstream-regression", null);
            Check(filteredUnknown.Skipped == 1 && filteredUnknown.Errors == 0, "Exact filter must always reject unknown IDs");
            var selected = CliProgram.ExtractAudioPackage(Package(wem, Id(characterPath, "chinese")), map,
                filteredArgs, "fixture.pck", BlockType.AudioChinese, "wem", null, null);
            Check(selected.Success == 1 && selected.Errors == 0, "Exact mapped archive selection must remain exportable");

            // A plugin can itself have a mapped ID: the policy must still prevent writes.
            var pluginArgs = Args(root, ++count, skipUnmapped: true);
            var pluginResult = CliProgram.ExtractAudioPackage(Package(plug, Id(characterPath, "chinese")), map,
                pluginArgs, "fixture.pck", BlockType.AudioChinese, "wav", "/nonexistent/vgmstream-regression", null);
            Check(pluginResult.Skipped == 1 && pluginResult.Success == 0 && pluginResult.Errors == 0 && pluginResult.Plugins == 0,
                "Mapped PLUG must not be written or decoded under skip-unmapped");
            NoFiles(pluginArgs.OutPath!);

            var legacyArgs = Args(root, ++count, skipUnmapped: false);
            var legacyWem = CliProgram.ExtractAudioPackage(Package(wem, 1234), map, legacyArgs,
                "fixture.pck", BlockType.Audio, "wem", null, null);
            Check(legacyWem.Success == 1 && legacyWem.Unmapped == 1 &&
                File.ReadAllBytes(Path.Combine(legacyArgs.OutPath!, "unmapped/1234.wem")).SequenceEqual(wem),
                "Omitting flag must preserve CLI numeric fallback");
            var legacyPlugin = CliProgram.ExtractAudioPackage(Package(plug, 5678), map, legacyArgs,
                "fixture.pck", BlockType.Audio, "wav", "/nonexistent/vgmstream-regression", null);
            Check(legacyPlugin.Success == 1 && legacyPlugin.Plugins == 1 &&
                File.ReadAllBytes(Path.Combine(legacyArgs.OutPath!, "plugins/Audio/fixture/5678.plug")).SequenceEqual(plug),
                "Omitting flag must preserve raw plugin compatibility without decoding");

            Console.WriteLine($"PASS: named-audio policy ({count} fixtures): all languages/blocks, numeric mapped names, skipped corrupt/unmapped payloads, exact archive filter, PLUG exclusion, legacy compatibility");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static CliProgram.AudioArgs Args(string root, int fixture, bool skipUnmapped) => new()
    {
        OutPath = Path.Combine(root, fixture.ToString()), SkipUnmapped = skipUnmapped, Threads = 1,
    };

    private static AkpkPackage Package(byte[] payload, ulong id)
    {
        byte[] data = new byte[24 + payload.Length];
        Encoding.ASCII.GetBytes("AKPK").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        payload.CopyTo(data, 24);
        var result = AkpkPackage.Parse(data);
        result.Entries.Add(new() { Id = id, Offset = 24, Size = (ulong)payload.Length });
        return result;
    }

    private static ulong Id(string path, string language) => Convert.ToUInt64(AudioMap.PathToHash(path, language), 16);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void NoFiles(string path) => Check(!Directory.Exists(path) || !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any(),
        "Skipped entries must not create any output files");
}
