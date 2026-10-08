using AS = AnimeStudio;

internal static class ShortResourceRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        // Real bundle 10e052b8d982d60f57048812 contains a 16-byte zeroed resS.
        // Type detection used to seek to byte 32 unconditionally for Brotli.
        // Cover all magic probe boundaries, not only that single fixture.
        foreach (int size in Enumerable.Range(0, 49))
        foreach (byte fill in new byte[] { 0, 0xAA })
        {
            byte[] data = Enumerable.Repeat(fill, size).ToArray();
            using var reader = new AS.FileReader("fixture.resS", new MemoryStream(data));
            Check(reader.FileType == AS.FileType.ResourceFile,
                $"Unrecognized {size}-byte resource must remain an opaque resource");
            Check(reader.Position == 0 && reader.Length == size,
                "Detection must leave short resource bytes and starting offset unchanged");
            Check(reader.ReadBytes(size).SequenceEqual(data), "Resource payload must remain exact after format probes");
        }
        using var gzip = new AS.FileReader("fixture.gz", new MemoryStream(new byte[] { 0x1F, 0x8B }));
        Check(gzip.FileType == AS.FileType.GZipFile, "Short valid format signatures must remain detectable");
        byte[] brotliHeader = new byte[38];
        System.Text.Encoding.ASCII.GetBytes("brotli").CopyTo(brotliHeader, 32);
        using var brotli = new AS.FileReader("fixture.br", new MemoryStream(brotliHeader));
        Check(brotli.FileType == AS.FileType.BrotliFile, "Brotli detection must still read offset 32 when present");
        Console.WriteLine("PASS: 0..48-byte resources preserve exact bytes and offsets; bounded gzip/Brotli format probes");
    }
}
