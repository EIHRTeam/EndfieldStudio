# EndfieldStudio

Unity game asset extraction tool with VFS container decryption + UnityFS deobfuscation + asset decoding pipeline.

Built on top of [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio), adds outer VFS container decryption (ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper)) and an end-to-end resource extraction pipeline.

## Origin

| Module | Source | Description |
|---|---|---|
| AnimeStudio core | [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio) | Unity asset parsing, VFSAES deobfuscation, Texture2D decoding |
| `AnimeStudio.Endfield` library | Ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper) (Rust → C#) | Outer VFS container decryption: ChaCha20, CRC32, .blc/.chk parsing |
| `AnimeStudio.Endfield.Cli` (`endfield-dump`) | Original | CLI tool wiring Stage 1 + 2 + 3 pipeline |

## Features

- **Stage 1**: Outer VFS container decryption (ChaCha20 → .blc/.chk → file stream)
- **Stage 2**: Inner UnityFS deobfuscation (VFSAES + LZ4Inv)
- **Stage 3**: Asset decoding (Texture2D → PNG/BMP/TGA)
- **Name-based export**: Regex-match asset names, export only what you need
- **Memory-safe**: Chunk-batched processing + memory budget cap + LOH compaction GC, handles 250k+ bundles without OOM
- **Channel pipeline**: Stage 1 serial decryption → bounded channel backpressure → N-thread parallel Stage 2+3

## Architecture

```
┌─────────────── single-process dotnet ───────────────┐
│  Stage 1: Outer VFS (AnimeStudio.Endfield)          │
│      ChaCha20 → .blc/.chk → file stream              │
│        ↓ Channel<BundleEntry> (backpressure)         │
│  Stage 2: Inner UnityFS (AnimeStudio)                │
│      VFSAES + LZ4Inv → UnityFS                       │
│        ↓                                             │
│  Stage 3: Asset decode (Texture2D/Sprite)            │
│      Texture2DDecoder → ImageSharp → PNG             │
└─────────────────────────────────────────────────────┘
```

### Project Structure

```
AnimeStudio/
├── AnimeStudio.Endfield/              ← Outer VFS decryption library (ported from fluffy-dumper)
│   ├── Keys.cs                        ChaCha20 key / XXTEA key / UnityHashSecret
│   ├── BlockType.cs                   25 BlockType enum
│   ├── BlockHashes.cs                 25 VFS directory hashes (hardcoded lookup)
│   ├── ChaCha20.cs                    ChaCha20 streaming cipher
│   ├── VfsParser.cs                   .blc metadata parsing + CRC32 verification
│   ├── VfsLoader.cs                   .chk streaming extraction
│   ├── Types.cs                       BlockMainInfo / ChunkInfo / FileInfo
│   ├── Xxtea.cs                       XXTEA decryption
│   └── Processors/LuaProcessor.cs     Lua base64 + XXTEA post-processing
│
├── AnimeStudio.Endfield.Cli/          ← CLI tool (endfield-dump)
│   └── Program.cs                     list / dump / inspect / extract
│
├── AnimeStudio/                       ← Original AnimeStudio (Unity asset parsing)
│   └── AnimeStudio.csproj             + Kyaru.Texture2DDecoder.Linux 0.2.0
│
├── AnimeStudio.GUI/                   ← Original GUI (WPF)
└── AnimeStudio.CLI/                   ← Original CLI
```

## Quick Start

### Prerequisites

- .NET 9 SDK
- Linux x64 (Texture2D decoding via `Kyaru.Texture2DDecoder.Linux` NuGet)

### Build

```bash
cd AnimeStudio
dotnet build AnimeStudio.Endfield.Cli -c Release
```

### Install as global command

```bash
./install.sh
```

Or manually:

```bash
cat > ~/.local/bin/endfield-dump << 'EOF'
#!/usr/bin/env bash
exec dotnet "/path/to/AnimeStudio/AnimeStudio.Endfield.Cli/bin/Release/net9.0/AnimeStudio.Endfield.Cli.dll" "$@"
EOF
chmod +x ~/.local/bin/endfield-dump
```

## Usage

### List all BlockTypes

```bash
endfield-dump list --vfs ./StreamingAssets
```

Sample output:
```
[Bundle] version=20123154 codeVersion=4 chunks=31 files=257434
[Table] version=20123154 codeVersion=4 chunks=42 files=629
[Lua] version=20123154 codeVersion=4 chunks=1 files=1174
...
```

### Extract character art

```bash
endfield-dump extract \
    --vfs ./StreamingAssets \
    --out ./out \
    --asset-name '^pic_\d+_chr_\d+_[a-z]+$' \
    --threads 16 \
    --format png \
    --png-compression fast \
    --max-memory-gb 64
```

`--limit N` processes only the largest N bundles (sorted by bundle size descending) — useful for quick tests or targeting large assets:

```bash
# Process only the top 10,000 bundles (covers ~80% of data, includes all character art)
endfield-dump extract \
    --vfs ./StreamingAssets \
    --out ./out \
    --asset-name 'chr' \
    --limit 10000 \
    --threads 16 \
    --format png
```

### Dump raw files (Stage 1 to disk)

```bash
# dump Lua (auto base64 + XXTEA decryption)
endfield-dump dump --vfs ./StreamingAssets --out ./out --block Lua

# dump Table (raw bytes, SparkBuffer→JSON not yet implemented)
endfield-dump dump --vfs ./StreamingAssets --out ./out --block Table
```

### Inspect resource distribution

```bash
# See what object types are in the top 50 largest bundles
endfield-dump inspect --vfs ./StreamingAssets --limit 50
```

## Command Reference

| Command | Description |
|---|---|
| `list` | List all BlockTypes with chunk/file counts |
| `dump` | Stage 1 decrypt to disk (Lua auto post-processed, rest raw) |
| `inspect` | Parse bundle internal object type distribution (for research) |
| `extract` | Stage 1 + 2 + 3 full pipeline, regex-export images |

### extract Options

| Option | Default | Description |
|---|---|---|
| `--vfs <path>` | required | StreamingAssets directory |
| `--out <dir>` | required | Output directory |
| `--asset-name <regex>` | none | Filter by asset name regex (e.g. `^pic_\d+_chr_`) |
| `--bundle-name <regex>` | none | Filter by bundle filename regex |
| `--types <T>` | Texture2D | Object types to export (repeatable) |
| `--block <type>` | Bundle | BlockType to process (repeatable) |
| `--threads N` | min(CPU, 16) | Parallel thread count |
| `--limit N` | 0 (all) | Process only the largest N bundles |
| `--min-size N` | 0 | Skip files smaller than N bytes |
| `--format <f>` | png | Output format: png / bmp / tga |
| `--png-compression <c>` | fast | PNG compression: none / fast / default |
| `--max-memory-gb N` | 64 | Memory budget cap (GB) |
| `--scratch <dir>` | /dev/shm/efend-bundles | Intermediate scratch directory |
| `--keep-bundles` | false | Keep intermediate bundle files (debug) |
| `--no-chunk-batching` | false | Disable per-chunk batching (debug) |

## Performance

| Scenario | Time | Notes |
|---|---|---|
| Stage 1 full dump (257k bundles) | 30.4 s | RSS 268 MB, byte-identical to fluffy-dumper |
| 10,000 bundle extract (84 PNGs) | 57 s | 16 threads, 0 failed |
| Top 20 bundle extract (1736 TGAs) | 6.3 s | 8 threads, 71× cumulative speedup |
