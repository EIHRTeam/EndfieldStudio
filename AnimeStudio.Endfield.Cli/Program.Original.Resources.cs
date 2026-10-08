using System.Text.Json;
using AS = AnimeStudio;

namespace AnimeStudio.Endfield.Cli;

internal static partial class Program
{
    internal readonly record struct OriginalMediaRange(long Offset, long Length);

    internal static IReadOnlyList<OriginalMediaRange> OriginalRedactions(long size, IEnumerable<OriginalMediaRange> ranges)
    {
        var merged = new List<OriginalMediaRange>();
        foreach (var range in ranges.OrderBy(range => range.Offset))
        {
            if (range.Offset < 0 || range.Length < 0 || range.Offset > size || range.Length > size - range.Offset)
                throw new InvalidDataException("Audio/video resource range lies outside the embedded stream");
            if (range.Length == 0) continue;
            if (merged.Count > 0 && range.Offset <= merged[^1].Offset + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = new(previous.Offset, Math.Max(previous.Offset + previous.Length, range.Offset + range.Length) - previous.Offset);
            }
            else merged.Add(range);
        }
        return merged;
    }

    internal static void CopyOriginalResource(Stream source, Stream destination, IReadOnlyList<OriginalMediaRange> redactions)
    {
        var ranges = OriginalRedactions(source.Length, redactions);
        source.Position = 0;
        byte[] zeroes = new byte[64 * 1024];
        foreach (var range in ranges)
        {
            CopyOriginalRange(source, destination, range.Offset - source.Position);
            long remaining = range.Length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, zeroes.Length);
                destination.Write(zeroes, 0, count);
                remaining -= count;
            }
            source.Position = range.Offset + range.Length;
        }
        CopyOriginalRange(source, destination, source.Length - source.Position);
    }

    private static void OriginalResources(AS.AssetsManager manager, OriginalSource source, OriginalRun run)
    {
        if (manager.ResourceFileReaders.Count == 0) return;
        // A .resS can contain both texture/model bytes and audio. Retain the
        // non-media bytes at their original offsets; never copy audio payloads.
        var excluded = new Dictionary<string, List<OriginalMediaRange>>(StringComparer.OrdinalIgnoreCase);
        void Exclude(string? path, long offset, long length)
        {
            if (string.IsNullOrEmpty(path)) return; // Inline media objects are excluded by OriginalBundle.
            string name = Path.GetFileName(path.Replace('\\', '/'));
            if (!excluded.TryGetValue(name, out var ranges)) excluded[name] = ranges = new();
            ranges.Add(new(offset, length));
        }
        foreach (var file in manager.assetsFileList)
        foreach (var info in file.m_Objects)
        {
            if (info.classID == (int)AS.ClassIDType.AudioClip)
            {
                var clip = new AS.AudioClip(new AS.ObjectReader(file.reader, file, info, manager.Game));
                Exclude(clip.m_Source, clip.m_Offset, clip.m_Size);
            }
            else if (info.classID == (int)AS.ClassIDType.VideoClip)
            {
                var clip = new AS.VideoClip(new AS.ObjectReader(file.reader, file, info, manager.Game));
                Exclude(clip.m_ExternalResources.m_Source, clip.m_ExternalResources.m_Offset, clip.m_ExternalResources.m_Size);
            }
        }
        foreach (var resource in manager.ResourceFileReaders.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (IsOriginalMediaPath(resource.Key)) { run.Skipped++; continue; }
            string name = OriginalRelativePath(resource.Key);
            string identity = source.Identity + "/resource/" + name;
            Stream stream = resource.Value.BaseStream;
            var ranges = OriginalRedactions(stream.Length,
                excluded.GetValueOrDefault(Path.GetFileName(name)) ?? Enumerable.Empty<OriginalMediaRange>());
            string basis = "_resources/" + source.Block + "/" + OriginalRelativePath(source.File.FileName) + "/" + name;
            string written = run.Output.Write(basis, identity, target => CopyOriginalResource(stream, target, ranges));
            var metadata = new { source = source.Identity, bundle_path = source.File.FileName,
                resource_path = name, output_path = written, source_size = stream.Length,
                path_source = "embedded_resource", audio_video_redacted = ranges.Count > 0,
                redacted_ranges = ranges.Select(range => new { offset = range.Offset, length = range.Length }),
                note = ranges.Count > 0 ? "Audio/video ranges are zero-filled; other bytes and all offsets are preserved." : null };
            string metadataPath = run.Output.Write(basis + ".resource.metadata.json", identity,
                target => JsonSerializer.Serialize(target, metadata, OriginalJson));
            run.Record(new { status = "exported", source = identity, block = source.Block.ToString(),
                source_path = source.File.FileName, type = "ResourceStream", resource_path = name,
                path_source = "embedded_resource", output_path = written, metadata_path = metadataPath,
                audio_video_redacted = ranges.Count > 0 });
            run.Written++;
        }
    }
}
