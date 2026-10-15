using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using AnimeStudio.Endfield;

/// Game 1.6.x block indexes (codeVersion 5) widen chunk/file tags to 8 bytes and
/// append a 10-byte trailer; codeVersion 4 indexes keep their i32 tags.
internal static class VfsIndexV5Regression
{
    public static void Run()
    {
        foreach (int codeVersion in new[] { 4, 5 })
        {
            var info = VfsParser.Parse(Build(codeVersion), verifyCrc: true);
            Check(info.CodeVersion == codeVersion, $"v{codeVersion}: code version");
            Check(info.Chunks.Count == 2, $"v{codeVersion}: chunk count");
            var files = info.Chunks.SelectMany(chunk => chunk.Files).ToArray();
            Check(files.Select(f => f.FileName).SequenceEqual(new[] { "Data/TableCfg/A.bytes", "Data/TableCfg/B.bytes", "Data/C.bytes" }),
                $"v{codeVersion}: file names must stay aligned");
            Check(files[1].UseEncrypt && files[1].IvSeed == 0x2DD82914 && files[2].Length == 7,
                $"v{codeVersion}: fields after the tag must stay aligned");
            Check(files[0].FileTag == FileTag.Audit && files[1].FileTag == FileTag.None,
                $"v{codeVersion}: tags");
        }
        Console.WriteLine("PASS: VFS block index v4 i32 tags and v5 8-byte tags + trailer");
    }

    private static byte[] Build(int codeVersion)
    {
        var o = new List<byte>();
        void I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); o.AddRange(b); }
        void I64(long v) { var b = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(b, v); o.AddRange(b); }
        void Str(string s) { var b = Encoding.UTF8.GetBytes(s); o.Add((byte)b.Length); o.Add(0); o.AddRange(b); }
        void Tag(long v) { if (codeVersion >= 5) I64(v); else I32((int)v); }
        I32(codeVersion); I32(0x0198A899); Str("Table"); I64(-1499682750); I32(3); I64(300); o.Add(0x12);
        I32(2);
        (string name, bool enc, long len, long tag)[][] chunks =
        {
            new[] { ("Data/TableCfg/A.bytes", false, 5L, 1L), ("Data/TableCfg/B.bytes", true, 6L, 0x00209C855B11B951L) },
            new[] { ("Data/C.bytes", false, 7L, 0L) },
        };
        foreach (var chunk in chunks)
        {
            o.AddRange(new byte[32]); I64(chunk.Sum(f => f.len)); o.Add(0x12);
            Tag(codeVersion >= 5 ? 0x00209C855B11B951L : 0); I32(chunk.Length);
            foreach (var f in chunk)
            {
                Str(f.name); I64(7); o.AddRange(new byte[32]); I64(0); I64(f.len); o.Add(0x12);
                o.Add(f.enc ? (byte)1 : (byte)0);
                if (f.enc) I64(0x2DD82914);
                Tag(codeVersion >= 5 ? f.tag : (f.tag == 1 ? 1 : 0));
            }
        }
        if (codeVersion >= 5) { I64(0x6ABD30D8); o.Add(0); o.Add(0); }
        var data = o.ToArray();
        var crc = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(crc, unchecked((int)Crc32.HashToUInt32(data)));
        return data.Concat(crc).ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
