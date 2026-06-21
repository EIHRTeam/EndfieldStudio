namespace AnimeStudio.Endfield;

public static class BlockHashes
{
    public static readonly IReadOnlyDictionary<BlockType, string> Map =
        new Dictionary<BlockType, string>
        {
            [BlockType.InitialAudio] = "07A1BB91",
            [BlockType.InitialBundle] = "0CE8FA57",
            [BlockType.InitialExtendData] = "3C9D9D2D",
            [BlockType.BundleManifest] = "1CDDBF1F",
            [BlockType.IFixPatch] = "DAFE52C9",
            [BlockType.AuditStreaming] = "6432320A",
            [BlockType.AuditDynamicStreaming] = "B9358E30",
            [BlockType.AuditIV] = "06223FE2",
            [BlockType.AuditAudio] = "1EBAF5C6",
            [BlockType.AuditVideo] = "2E6CE44D",
            [BlockType.Bundle] = "7064D8E2",
            [BlockType.Audio] = "24ED34CF",
            [BlockType.Video] = "55FC21C6",
            [BlockType.IV] = "A63D7E6A",
            [BlockType.Streaming] = "C3442D43",
            [BlockType.DynamicStreaming] = "23D53F5D",
            [BlockType.Lua] = "19E3AE45",
            [BlockType.Table] = "42A8FCA6",
            [BlockType.JsonData] = "775A31D1",
            [BlockType.ExtendData] = "D6E622F7",
            [BlockType.HotfixAudio] = "F151B649",
            [BlockType.AudioChinese] = "E1E7D7CE",
            [BlockType.AudioEnglish] = "A31457D0",
            [BlockType.AudioJapanese] = "F668D4EE",
            [BlockType.AudioKorean] = "E9D31017",
        };

    public static string GetDirName(BlockType bt)
    {
        if (Map.TryGetValue(bt, out var name))
            return name;
        throw new ArgumentException($"No directory hash registered for block type {bt}");
    }
}
