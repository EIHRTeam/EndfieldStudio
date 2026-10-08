using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace AnimeStudio.Endfield.Cli;

/// <summary>
/// Reads the game's decrypted BundleManifest (outer Brotli, slotted asset table,
/// 48-byte bundle records, individually Brotli-compressed UTF-16 asset paths).
/// Container ownership is resolved separately; this index only proves paths
/// belonging to a bundle and never guesses between equally named source files.
/// </summary>
internal sealed class OriginalAssetManifest
{
    private const int MaxManifestBytes = 256 * 1024 * 1024;
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);
    private readonly Dictionary<string, IReadOnlyList<string>> paths;

    private OriginalAssetManifest(Dictionary<string, IReadOnlyList<string>> paths) => this.paths = paths;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> PathsByBundle => paths;

    public static OriginalAssetManifest Load(string path)
    {
        using var source = File.OpenRead(path);
        return Load(source);
    }

    public static OriginalAssetManifest Load(Stream source)
    {
        var reader = new ManifestReader(Decompress(source, MaxManifestBytes));
        return new OriginalAssetManifest(reader.Read());
    }

    public IReadOnlyList<string> GetAssetPaths(string bundleName) =>
        paths.TryGetValue(NormalizeBundleName(bundleName), out var found) ? found : Array.Empty<string>();

    public bool TryResolve(string bundleName, string objectName, out string originalPath)
    {
        originalPath = "";
        if (string.IsNullOrEmpty(objectName)) return false;
        string? match = null;
        foreach (string path in GetAssetPaths(bundleName))
        {
            if (path.Contains("##", StringComparison.Ordinal)) continue;
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), objectName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null && !string.Equals(match, path, StringComparison.Ordinal)) return false;
            match = path;
        }
        if (match is null) return false;
        originalPath = match;
        return true;
    }

    public bool TryResolveSubAsset(string bundleName, string objectName, out string ownerPath, out string subAssetName)
    {
        ownerPath = "";
        subAssetName = "";
        if (string.IsNullOrEmpty(objectName)) return false;
        string? match = null;
        int delimiter = -1;
        foreach (string path in GetAssetPaths(bundleName))
        {
            int split = path.IndexOf("##", StringComparison.Ordinal);
            if (split < 1 || !string.Equals(path[(split + 2)..], objectName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null && !string.Equals(match, path, StringComparison.Ordinal)) return false;
            match = path;
            delimiter = split;
        }
        if (match is null) return false;
        ownerPath = match[..delimiter];
        subAssetName = match[(delimiter + 2)..];
        return true;
    }

    private static string NormalizeBundleName(string value)
    {
        value = value.Replace('\\', '/');
        const string prefix = "Data/Bundles/Windows/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    private static byte[] Decompress(Stream source, int maximumBytes)
    {
        using var brotli = new BrotliStream(source, CompressionMode.Decompress, leaveOpen: true);
        using var target = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = brotli.Read(buffer)) != 0)
            {
                if (target.Length + read > maximumBytes)
                    throw new InvalidDataException("BundleManifest exceeds the supported decompressed size");
                target.Write(buffer, 0, read);
            }
            return target.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed class ManifestReader(byte[] data)
    {
        private readonly record struct Section(int Start, int End);

        private void RequireRange(long offset, long length, int end)
        {
            if (offset < 0 || length < 0 || offset + length > end || end > data.Length)
                throw new InvalidDataException("BundleManifest contains a truncated or out-of-range record");
        }

        private int Number(int offset, int end)
        {
            uint value = Word(offset, end);
            if (value > int.MaxValue) throw new InvalidDataException("BundleManifest contains an oversized index");
            return (int)value;
        }

        private uint Word(int offset, int end)
        {
            RequireRange(offset, 4, end);
            return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        }

        private string String(ref int cursor, int end, bool characters = false)
        {
            int count = Number(cursor, end);
            long size = (long)count * (characters ? 2 : 1);
            RequireRange(cursor + 4L, size, end);
            string value = Decode(data.AsSpan(cursor + 4, (int)size));
            cursor += 4 + (int)size;
            return value;
        }

        private static string Decode(ReadOnlySpan<byte> value)
        {
            try { return Utf16.GetString(value); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("BundleManifest contains invalid UTF-16", ex); }
        }

        private Section ReadSection(ref int cursor)
        {
            int size = Number(cursor, data.Length);
            RequireRange(cursor + 4L, size, data.Length);
            var result = new Section(cursor + 4, cursor + 4 + size);
            cursor = result.End;
            return result;
        }

        private int RelativeOffset(Section section, int relative)
        {
            RequireRange(section.Start + (long)relative, 4, section.End);
            return section.Start + relative;
        }

        private static string VirtualPath(string value, bool bundle = false)
        {
            value = value.Replace('\\', '/');
            if (value.Length == 0 || value.Any(c => c == ':' || char.IsControl(c)) ||
                value.Split('/').Any(part => part is "" or "." or "..") ||
                (bundle && !value.EndsWith(".ab", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("BundleManifest contains an invalid relative asset path");
            return value;
        }

        public Dictionary<string, IReadOnlyList<string>> Read()
        {
            if (Word(0, data.Length) != 0xFF11FF11)
                throw new InvalidDataException("BundleManifest start marker mismatch");
            int cursor = 4;
            _ = String(ref cursor, data.Length, characters: true);
            if (Word(cursor, data.Length) != 0xF1F2F3F4)
                throw new InvalidDataException("BundleManifest second marker mismatch");
            cursor += 4;
            _ = String(ref cursor, data.Length, characters: true);
            _ = String(ref cursor, data.Length, characters: true);
            Section assets = ReadSection(ref cursor);
            _ = ReadSection(ref cursor); // Bundle dependency information is not a source-path table.
            Section bundles = ReadSection(ref cursor);
            Section strings = ReadSection(ref cursor);
            if (data.Length - cursor != 4)
                throw new InvalidDataException("BundleManifest trailing layout changed");

            int bundleCount = Number(bundles.Start, bundles.End);
            if (bundles.End - bundles.Start != 4L + bundleCount * 48L)
                throw new InvalidDataException("BundleManifest bundle record layout changed");
            var bundleNames = new string[bundleCount];
            for (int index = 0; index < bundleCount; index++)
            {
                int row = bundles.Start + 4 + index * 48;
                if (Number(row, bundles.End) != index)
                    throw new InvalidDataException("BundleManifest bundle indexes are not contiguous");
                int stringCursor = RelativeOffset(strings, Number(row + 4, bundles.End));
                bundleNames[index] = VirtualPath(String(ref stringCursor, strings.End), bundle: true);
            }

            int count = Number(assets.Start, assets.End);
            if (count == 0) throw new InvalidDataException("BundleManifest contains no asset records");
            RequireRange(assets.Start + 4L, count * 8L, assets.End);
            int slotsEnd = assets.Start + 4 + count * 8;
            if (assets.End - slotsEnd != count * 24L)
                throw new InvalidDataException("BundleManifest asset record layout changed");
            var ranges = new List<(int Offset, int Count)>();
            long total = 0;
            for (int at = assets.Start + 4; at < slotsEnd; at += 8)
            {
                int relative = Number(at, assets.End);
                int length = Number(at + 4, assets.End);
                if (length == 0) continue;
                int absolute = RelativeOffset(assets, relative);
                if (absolute < slotsEnd) throw new InvalidDataException("BundleManifest slot points into slot metadata");
                RequireRange(absolute, length * 24L, assets.End);
                total += length;
                ranges.Add((absolute, length));
            }
            if (total != count) throw new InvalidDataException("BundleManifest slot counts do not cover the asset table");
            cursor = slotsEnd;
            foreach (var range in ranges.OrderBy(range => range.Offset))
            {
                if (range.Offset != cursor) throw new InvalidDataException("BundleManifest slots overlap or leave gaps");
                cursor += range.Count * 24;
            }
            if (cursor != assets.End) throw new InvalidDataException("BundleManifest asset table is incomplete");

            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int at = slotsEnd; at < assets.End; at += 24)
            {
                int pathAt = RelativeOffset(strings, Number(at + 8, assets.End));
                int bundleIndex = Number(at + 12, assets.End);
                if (bundleIndex >= bundleCount) throw new InvalidDataException("BundleManifest asset references an unknown bundle");
                int size = Number(pathAt, strings.End);
                RequireRange(pathAt + 4L, size, strings.End);
                using var compressedPath = new MemoryStream(data, pathAt + 4, size, writable: false);
                string assetPath = VirtualPath(Decode(Decompress(compressedPath, 1024 * 1024)));
                if (!result.TryGetValue(bundleNames[bundleIndex], out var values))
                    result[bundleNames[bundleIndex]] = values = new HashSet<string>(StringComparer.Ordinal);
                values.Add(assetPath);
            }
            return result.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray()),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
