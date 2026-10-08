using System.Buffers.Binary;
using System.Text;
using AnimeStudio.Endfield;
using AnimeStudio.Endfield.Processors;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// One encrypted bank at a nonzero PCK offset, plus one ordinary streamed WEM.
// Their payloads are deliberately distinct so offset aliasing cannot pass.
byte[] bankWem = Encoding.ASCII.GetBytes("RIFF\x08\x00\x00\x00WAVEtest");
byte[] streamWem = Encoding.ASCII.GetBytes("RIFF\x08\x00\x00\x00WAVEdata");
byte[] pck = new byte[272];
void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(pck.AsSpan(at), value);
void Tag(int at, string value) => Encoding.ASCII.GetBytes(value).CopyTo(pck, at);
Tag(0, "AKPK"); U32(4, 64); U32(8, 1);
U32(12, 0); U32(16, 24); U32(20, 24);
// Bank table: id, block size, length, block offset, language.
U32(24, 1); U32(28, 42); U32(32, 1); U32(36, 52); U32(40, 128); U32(44, 0);
U32(48, 1); U32(52, 99); U32(56, 1); U32(60, 16); U32(64, 256); U32(68, 0);
Tag(128, "BKHD"); U32(132, 0);
Tag(136, "DIDX"); U32(140, 12); U32(144, 1234); U32(148, 0); U32(152, 16);
Tag(156, "DATA"); U32(160, 16); bankWem.CopyTo(pck, 164);
streamWem.CopyTo(pck, 256);
AkpkCrypto.DecryptVfs(pck, 128, 52, 42);
var package = AkpkPackage.Parse(pck);
Check(package.Entries.Count == 2, "Both embedded and streamed WEMs must be present");
var embedded = package.Entries.Single(entry => entry.Id == 1234);
Check(embedded.Offset == 164, "Embedded WEM offset must be absolute, without adding bank start twice");
Check(package.GetWemData(embedded).SequenceEqual(bankWem), "Embedded WEM bytes must match exactly");
var streamed = package.Entries.Single(entry => entry.Id == 99);
Check(streamed.Offset == 256, "Streamed WEM absolute offsets must remain unchanged");
Check(package.GetWemData(streamed).SequenceEqual(streamWem), "Streamed WEM bytes must match exactly");
Console.WriteLine("PASS: nonzero bank start, exact embedded WEM, unchanged streamed WEM");
byte[] plugin = Encoding.ASCII.GetBytes("PLUG\x08\x00\x00\x00hashdata");
plugin.CopyTo(package.RawData, 256);
Check(AkpkPackage.IsPluginData(package.GetWemData(streamed)), "PLUG is recognized without WEM decryption");
Check(package.GetWemData(streamed).SequenceEqual(plugin), "Raw plugin media must remain byte-for-byte intact");
Check(!AkpkPackage.IsPluginData(Encoding.ASCII.GetBytes("PLUG\x08\x00\x00\x00short")), "Truncated PLUG must not pass");
Check(!AkpkPackage.IsPluginData(Encoding.ASCII.GetBytes("JUNK\x08\x00\x00\x00hashdata")), "Unknown media must not pass as PLUG");
Console.WriteLine("PASS: PLUG preservation, invalid/truncated/unknown header rejection");

NamedAudioRegression.Run();
OriginalManifestRegression.Run();
OriginalExportRegression.Run();
OriginalTextureLuaRegression.Run();
OriginalResourceRegression.Run();
VfsBlockRegression.Run();
ShortResourceRegression.Run();

// Optional bounded read-only validation against installed real bank packages.
if (args.Length == 2 && args[0] == "--vfs")
{
    var loader = new VfsLoader(args[1], Keys.ChaCha20Key);
    var info = loader.LoadBlockInfo(BlockType.Audio);
    int tested = 0;
    foreach (var chunk in info.Chunks)
    foreach (var file in chunk.Files)
    {
        if (!file.FileName.EndsWith("_banks.pck", StringComparison.OrdinalIgnoreCase))
        {
            Check(!file.UseEncrypt, "Bounded real stream validation requires unencrypted outer VFS");
            using var stream = File.OpenRead(Path.Combine(loader.VfsPath, BlockHashes.GetDirName(BlockType.Audio), chunk.FileName()));
            byte[] head = new byte[8];
            stream.Position = file.Offset;
            stream.ReadExactly(head);
            int headerLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)) + 8);
            Check(headerLength <= 16 * 1024 * 1024, "Unexpectedly large PCK header");
            byte[] prefix = new byte[headerLength];
            stream.Position = file.Offset;
            stream.ReadExactly(prefix);
            if (Encoding.ASCII.GetString(prefix, 0, 4) == ":)xD")
            {
                AkpkCrypto.DecryptVfs(prefix, 12, headerLength - 12, (uint)(headerLength - 8));
                Encoding.ASCII.GetBytes("AKPK").CopyTo(prefix, 0);
                BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(8), 1);
            }
            uint langSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12));
            uint bankSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(16));
            uint soundSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(20));
            int bankTable = 24 + (langSize + bankSize + soundSize + 16 < headerLength - 8 ? 4 : 0) + (int)langSize;
            uint bankCount = bankSize > 0 ? BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(bankTable)) : 0;
            Console.WriteLine($"INDEX {file.FileName}: bank count={bankCount}, bank sector={bankSize}");
            if (bankCount > 0)
            {
                int entrySize = ((int)bankSize - 4) / (int)bankCount;
                long totalBankBytes = 0;
                for (int index = 0; index < bankCount; index++)
                {
                    int pos = bankTable + 4 + index * entrySize;
                    uint bankId = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(pos));
                    uint blockSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(pos + 4));
                    ulong bankLength = entrySize == 24 ? BinaryPrimitives.ReadUInt64LittleEndian(prefix.AsSpan(pos + 8)) : BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(pos + 8));
                    uint bankOffset = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(pos + (entrySize == 24 ? 16 : 12)));
                    totalBankBytes += (long)bankLength;
                    Console.WriteLine($"BANK id={bankId} offset={(ulong)bankOffset * Math.Max(blockSize,1)} length={bankLength}");
                }
                Console.WriteLine($"BANK_BYTES {totalBankBytes}");
                BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(bankTable), 0);
            }
            var streamPackage = AkpkPackage.Parse(prefix);
            int pluginCount = 0;
            foreach (var entry in streamPackage.Entries)
            {
                Check(entry.Size >= 16 && entry.Offset + entry.Size <= (ulong)file.Length,
                      $"Invalid streamed range {entry.Id}");
                byte[] header = new byte[16];
                stream.Position = file.Offset + (long)entry.Offset;
                stream.ReadExactly(header);
                string magic = Encoding.ASCII.GetString(header, 0, 4);
                if (magic is not ("RIFF" or "RIFX" or "PLUG"))
                {
                    AkpkCrypto.DecryptWem(header, (uint)entry.Id);
                    magic = Encoding.ASCII.GetString(header, 0, 4);
                }
                Check(magic is "RIFF" or "RIFX" or "PLUG",
                      $"Unknown streamed format {file.FileName}/{entry.Id}: {Convert.ToHexString(header)}");
                if (magic == "PLUG")
                {
                    Check(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) == entry.Size - 8,
                          $"Invalid PLUG extent {entry.Id}");
                    pluginCount++;
                }
            }
            Console.WriteLine($"PASS: {file.FileName}, {streamPackage.Entries.Count} stream headers, {pluginCount} PLUG containers");
            continue;
        }
        Console.WriteLine($"BANK_SOURCE {Path.Combine(loader.VfsPath, BlockHashes.GetDirName(BlockType.Audio), chunk.FileName())}");
        var parsed = AkpkPackage.Parse(loader.ExtractFileToBytes(BlockType.Audio, chunk, file));
        foreach (var entry in parsed.Entries)
        {
            byte[] wem = parsed.GetWemData(entry);
            Check(wem.Length >= 12, $"Short WEM {file.FileName}/{entry.Id}");
            string magic = Encoding.ASCII.GetString(wem, 0, 4);
            Check((magic is "RIFF" or "RIFX") || AkpkPackage.IsPluginData(wem),
                  $"Unknown media format {file.FileName}/{entry.Id}: {magic}");
            if (AkpkPackage.IsPluginData(wem))
                Check(wem.SequenceEqual(parsed.RawData.AsSpan((int)entry.Offset, (int)entry.Size).ToArray()),
                      $"Plugin bytes were modified: {entry.Id}");
            else
                Check(Encoding.ASCII.GetString(wem, 8, 4) == "WAVE", $"Invalid WAVE form {entry.Id}");
            if (AkpkPackage.IsPluginData(wem))
                Console.WriteLine($"PLUGIN_SHA256 {entry.Id} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wem)).ToLowerInvariant()}");
            tested++;
        }
        Console.WriteLine($"PASS: {file.FileName}, {parsed.Entries.Count} WEMs, {file.Length} bytes read");
    }
    Check(tested > 0, "Real bank validation must exercise embedded WEMs");
    Console.WriteLine($"PASS: {tested} real embedded media entries, no offsets or format errors");
}
