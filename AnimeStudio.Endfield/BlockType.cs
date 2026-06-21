namespace AnimeStudio.Endfield;

public enum BlockType : byte
{
    None = 0,
    InitialAudio = 1,
    InitialBundle = 2,
    InitialExtendData = 3,
    BundleManifest = 4,
    IFixPatch = 5,
    AuditStreaming = 6,
    AuditDynamicStreaming = 7,
    AuditIV = 8,
    AuditAudio = 9,
    AuditVideo = 10,
    Bundle = 11,
    Audio = 12,
    Video = 13,
    IV = 14,
    Streaming = 15,
    DynamicStreaming = 16,
    Lua = 17,
    Table = 18,
    JsonData = 19,
    ExtendData = 20,
    HotfixAudio = 21,
    AudioChinese = 101,
    AudioEnglish = 102,
    AudioJapanese = 103,
    AudioKorean = 104,
    Raw = 255,
}

public enum FileTag : byte
{
    None = 0,
    Audit = 1,
}

public static class BlockTypes
{
    public static string Name(this BlockType bt) => bt switch
    {
        BlockType.None => "None",
        BlockType.InitialAudio => "InitAudio",
        BlockType.InitialBundle => "InitBundle",
        BlockType.InitialExtendData => "InitialExtendData",
        BlockType.BundleManifest => "BundleManifest",
        BlockType.IFixPatch => "IFixPatchOut",
        BlockType.AuditStreaming => "AuditStreaming",
        BlockType.AuditDynamicStreaming => "AuditDynamicStreaming",
        BlockType.AuditIV => "AuditIV",
        BlockType.AuditAudio => "AuditAudio",
        BlockType.AuditVideo => "AuditVideo",
        BlockType.Bundle => "Bundle",
        BlockType.Audio => "Audio",
        BlockType.Video => "Video",
        BlockType.IV => "IV",
        BlockType.Streaming => "Streaming",
        BlockType.DynamicStreaming => "DynamicStreaming",
        BlockType.Lua => "Lua",
        BlockType.Table => "Table",
        BlockType.JsonData => "JsonData",
        BlockType.ExtendData => "ExtendData",
        BlockType.HotfixAudio => "HotfixAudio",
        BlockType.AudioChinese => "AudioChinese",
        BlockType.AudioEnglish => "AudioEnglish",
        BlockType.AudioJapanese => "AudioJapanese",
        BlockType.AudioKorean => "AudioKorean",
        BlockType.Raw => "Raw",
        _ => "Raw",
    };

    public static BlockType FromByte(byte value) => value switch
    {
        0 => BlockType.None,
        1 => BlockType.InitialAudio,
        2 => BlockType.InitialBundle,
        3 => BlockType.InitialExtendData,
        4 => BlockType.BundleManifest,
        5 => BlockType.IFixPatch,
        6 => BlockType.AuditStreaming,
        7 => BlockType.AuditDynamicStreaming,
        8 => BlockType.AuditIV,
        9 => BlockType.AuditAudio,
        10 => BlockType.AuditVideo,
        11 => BlockType.Bundle,
        12 => BlockType.Audio,
        13 => BlockType.Video,
        14 => BlockType.IV,
        15 => BlockType.Streaming,
        16 => BlockType.DynamicStreaming,
        17 => BlockType.Lua,
        18 => BlockType.Table,
        19 => BlockType.JsonData,
        20 => BlockType.ExtendData,
        21 => BlockType.HotfixAudio,
        101 => BlockType.AudioChinese,
        102 => BlockType.AudioEnglish,
        103 => BlockType.AudioJapanese,
        104 => BlockType.AudioKorean,
        _ => BlockType.Raw,
    };

    public static FileTag FileTagFromByte(byte value) => value switch
    {
        0 => FileTag.None,
        1 => FileTag.Audit,
        _ => FileTag.None,
    };

    public static readonly IReadOnlyList<BlockType> AllDumpable = new BlockType[]
    {
        BlockType.InitialAudio,
        BlockType.InitialBundle,
        BlockType.InitialExtendData,
        BlockType.BundleManifest,
        BlockType.IFixPatch,
        BlockType.AuditStreaming,
        BlockType.AuditDynamicStreaming,
        BlockType.AuditIV,
        BlockType.AuditAudio,
        BlockType.AuditVideo,
        BlockType.Bundle,
        BlockType.Audio,
        BlockType.Video,
        BlockType.IV,
        BlockType.Streaming,
        BlockType.DynamicStreaming,
        BlockType.Lua,
        BlockType.Table,
        BlockType.JsonData,
        BlockType.ExtendData,
        BlockType.HotfixAudio,
        BlockType.AudioChinese,
        BlockType.AudioEnglish,
        BlockType.AudioJapanese,
        BlockType.AudioKorean,
    };
}
