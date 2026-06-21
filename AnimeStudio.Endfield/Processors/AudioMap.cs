using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// 音频路径映射。端口 fluffy-dumper/audio/map.rs。
/// AudioDialog 表里的 path → 拼成 voice/&lt;lang&gt;/&lt;path&gt; → FNV-1a 64-bit 哈希。
/// WEM 的 entry.id 的 hex 如果匹配此哈希，就能还原出人类可读路径。
/// </summary>
public sealed class AudioMap
{
    private const ulong FnvOffset = 0xcbf29ce484222325UL;
    private const ulong FnvPrime = 0x100000001b3UL;

    private readonly Dictionary<string, string> _entries = new();

    public int Count => _entries.Count;

    public enum Language
    {
        Chinese,
        English,
        Japanese,
        Korean,
    }

    /// <summary>
    /// 从 AudioDialog JSON 构建映射。data 是 SparkBuffer.Parse 输出的 JSON 字符串。
    /// </summary>
    public static AudioMap FromAudioDialog(string jsonData, Language language)
    {
        var map = new AudioMap();
        string langLower = LanguageToLower(language);

        using var doc = JsonDocument.Parse(jsonData);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Value.TryGetProperty("path", out var pathEl)) continue;
            string? path = pathEl.GetString();
            if (string.IsNullOrEmpty(path)) continue;

            string hash = PathToHash(path, langLower);
            string fullPath = MakeVoicePath(path, langLower);
            map._entries[hash] = fullPath;
        }

        return map;
    }

    public string? GetPath(string hash)
    {
        return _entries.TryGetValue(hash, out var path) ? path : null;
    }

    public static string MakeVoicePath(string path, string language)
    {
        return $"voice/{language}/{path.Replace('\\', '/')}".ToLowerInvariant();
    }

    public static string PathToHash(string path, string language)
    {
        string fullPath = MakeVoicePath(path, language);
        return $"{Fnv1A64(Encoding.UTF8.GetBytes(fullPath)):x}";
    }

    public static string LanguageToLower(Language lang) => lang switch
    {
        Language.Chinese => "chinese",
        Language.English => "english",
        Language.Japanese => "japanese",
        Language.Korean => "korean",
        _ => "chinese",
    };

    public static string LanguageName(Language lang) => lang switch
    {
        Language.Chinese => "Chinese",
        Language.English => "English",
        Language.Japanese => "Japanese",
        Language.Korean => "Korean",
        _ => "Chinese",
    };

    public static Language[] AllLanguages() =>
        new[] { Language.Chinese, Language.English, Language.Japanese, Language.Korean };

    /// <summary>
    /// FNV-1a 64-bit 哈希（offset basis 0xcbf29ce484222325, prime 0x100000001b3）。
    /// </summary>
    private static ulong Fnv1A64(byte[] data)
    {
        ulong hash = FnvOffset;
        foreach (byte b in data)
        {
            hash = unchecked(hash * FnvPrime);
            hash ^= b;
        }
        return hash;
    }
}
