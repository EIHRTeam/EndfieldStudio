using CliProgram = AnimeStudio.Endfield.Cli.Program;

internal static class OriginalResourceRegression
{
    public static void Run()
    {
        byte[] bytes = Enumerable.Range(1, 24).Select(value => (byte)value).ToArray();
        using var source = new MemoryStream(bytes);
        using var target = new MemoryStream();
        CliProgram.CopyOriginalResource(source, target,
            [new(12, 3), new(2, 5), new(5, 5), new(15, 2)]);
        byte[] expected = (byte[])bytes.Clone();
        Array.Clear(expected, 2, 8);
        Array.Clear(expected, 12, 5);
        if (!target.ToArray().SequenceEqual(expected))
            throw new InvalidOperationException("Shared resource redaction changed non-audio bytes or offsets");
        using var exact = new MemoryStream();
        CliProgram.CopyOriginalResource(source, exact, []);
        if (!exact.ToArray().SequenceEqual(bytes))
            throw new InvalidOperationException("Non-audio resource stream was not preserved exactly");
        foreach (var invalid in new CliProgram.OriginalMediaRange[] { new(-1, 1), new(23, 2), new(0, -1), new(long.MaxValue, 2) })
        {
            try { CliProgram.OriginalRedactions(bytes.Length, [invalid]); }
            catch (InvalidDataException) { continue; }
            throw new InvalidOperationException("Invalid audio range was accepted");
        }
        Console.WriteLine("PASS: shared resource streams preserve offsets and non-audio bytes, redact overlapping media ranges, reject invalid extents");
    }
}
