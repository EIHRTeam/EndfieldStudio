using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace AnimeStudio.Endfield.Processors;

/// <summary>
/// CRI USM 容器解复用器。从 USM 文件中提取 MPEG-2 视频流和音频流。
/// 移植自 fluffy-dumper/usm/src/demuxer.rs。
/// </summary>
public static class UsmDemuxer
{
    private static readonly byte[] CRID = { 0x43, 0x52, 0x49, 0x44 };
    private static readonly byte[] SFV  = { 0x40, 0x53, 0x46, 0x56 };
    private static readonly byte[] SFA  = { 0x40, 0x53, 0x46, 0x41 };
    private static readonly byte[] ALP  = { 0x40, 0x41, 0x4C, 0x50 };
    private static readonly byte[] SBT  = { 0x40, 0x53, 0x42, 0x54 };
    private static readonly byte[] CUE  = { 0x40, 0x43, 0x55, 0x45 };
    private static readonly byte[] UTF  = { 0x40, 0x55, 0x54, 0x46 };

    private static readonly byte[] HEADER_END   = Encoding.ASCII.GetBytes("#HEADER END     ===============\0");
    private static readonly byte[] METADATA_END = Encoding.ASCII.GetBytes("#METADATA END   ===============\0");
    private static readonly byte[] CONTENTS_END = Encoding.ASCII.GetBytes("#CONTENTS END   ===============\0");

    public sealed class DemuxedStreams
    {
        public byte[] Video = Array.Empty<byte>();
        public byte[]? Audio;
        public string AudioExtension = "";
    }

    public static DemuxedStreams Demux(byte[] data)
    {
        int fileSize = data.Length;
        int offset = FindPattern(data, CRID, 0);
        if (offset < 0)
            throw new InvalidDataException("USM CRID marker not found");

        var videoStreams = new Dictionary<uint, List<byte>>();
        var audioStreams = new Dictionary<uint, List<byte>>();

        while (offset + 8 <= fileSize)
        {
            // 读取 4 字节 block magic
            if (!IsKnownBlock(data, offset))
                break;

            // big-endian u32 block_size
            uint blockSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4));

            bool isVideo = MatchesAt(data, offset, SFV);
            bool isAudio = MatchesAt(data, offset, SFA);

            if ((isVideo || isAudio) && offset + 0xE <= fileSize)
            {
                // big-endian u16 header_size
                ushort headerSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 8));
                ushort footerSize = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 0xA));
                byte streamId = isAudio ? data[offset + 0xC] : (byte)0;

                if (blockSize > headerSize + footerSize)
                {
                    int payloadSize = (int)(blockSize - headerSize - footerSize);
                    int payloadStart = offset + 8 + headerSize;
                    int payloadEnd = payloadStart + payloadSize;

                    if (payloadEnd <= fileSize)
                    {
                        var payload = new ArraySegment<byte>(data, payloadStart, payloadSize);
                        if (isVideo)
                        {
                            uint key = BitConverter.ToUInt32(SFV, 0);
                            if (!videoStreams.TryGetValue(key, out var list))
                            {
                                list = new List<byte>();
                                videoStreams[key] = list;
                            }
                            list.AddRange(payload);
                        }
                        else
                        {
                            // audio key combines block magic + stream id
                            uint key = (uint)streamId | BitConverter.ToUInt32(SFA, 0);
                            if (!audioStreams.TryGetValue(key, out var list))
                            {
                                list = new List<byte>();
                                audioStreams[key] = list;
                            }
                            list.AddRange(payload);
                        }
                    }
                }
            }

            offset += 8 + (int)blockSize;
        }

        // 取第一个视频流
        if (videoStreams.Count == 0)
            throw new InvalidDataException("USM: no video stream found");

        byte[] video = StripMarkers(videoStreams.Values.First().ToArray());

        // 取第一个音频流（可选）
        byte[]? audio = null;
        string audioExt = "";
        if (audioStreams.Count > 0)
        {
            byte[] rawAudio = StripMarkers(audioStreams.Values.First().ToArray());
            audioExt = DetectAudioExtension(rawAudio);
            audio = rawAudio;
        }

        return new DemuxedStreams
        {
            Video = video,
            Audio = audio,
            AudioExtension = audioExt,
        };
    }

    private static bool IsKnownBlock(byte[] data, int offset)
    {
        return MatchesAt(data, offset, CRID) ||
               MatchesAt(data, offset, SFV) ||
               MatchesAt(data, offset, SFA) ||
               MatchesAt(data, offset, ALP) ||
               MatchesAt(data, offset, SBT) ||
               MatchesAt(data, offset, CUE) ||
               MatchesAt(data, offset, UTF);
    }

    private static bool MatchesAt(byte[] data, int offset, byte[] pattern)
    {
        if (offset + pattern.Length > data.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
        {
            if (data[offset + i] != pattern[i]) return false;
        }
        return true;
    }

    private static int FindPattern(byte[] data, byte[] pattern, int start)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length) return -1;
        int limit = data.Length - pattern.Length;
        for (int i = start; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    /// <summary>
    /// 剥离 CRI 的 #HEADER END / #METADATA END / #CONTENTS END 标记。
    /// </summary>
    private static byte[] StripMarkers(byte[] data)
    {
        int headerEndPos = FindPattern(data, HEADER_END, 0);
        int metadataEndPos = FindPattern(data, METADATA_END, 0);

        int headerSize = (headerEndPos, metadataEndPos) switch
        {
            (int h, int m) when h >= 0 && m >= 0 && m > h => m + METADATA_END.Length,
            (int h, int m) when h >= 0 && m >= 0 => h + HEADER_END.Length,
            (int h, _) when h >= 0 => h + HEADER_END.Length,
            (_, int m) when m >= 0 => m + METADATA_END.Length,
            _ => 0,
        };

        int start = headerSize > 0 && headerSize <= data.Length ? headerSize : 0;
        int end = data.Length;

        int contentsEndPos = FindPattern(data, CONTENTS_END, start);
        if (contentsEndPos >= 0)
            end = contentsEndPos;

        int length = end - start;
        if (length <= 0) return Array.Empty<byte>();
        if (start == 0 && length == data.Length) return data;

        byte[] result = new byte[length];
        Array.Copy(data, start, result, 0, length);
        return result;
    }

    private static string DetectAudioExtension(byte[] data)
    {
        if (data.Length < 4) return ".bin";

        if (data[0] == (byte)'A' && data[1] == (byte)'I' &&
            data[2] == (byte)'X' && data[3] == (byte)'F')
            return ".aix";
        if (data[0] == 0x80)
            return ".adx";
        if (data[0] == (byte)'H' && data[1] == (byte)'C' &&
            data[2] == (byte)'A' && data[3] == 0x00)
            return ".hca";
        return ".bin";
    }
}
