# EndfieldStudio

Unity game asset extraction tool with VFS container decryption + UnityFS deobfuscation + asset decoding pipeline.

Built on top of [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio), adds outer VFS container decryption (ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper)) and an end-to-end resource extraction pipeline.

## Origin

| Module | Source | Description |
|---|---|---|
| AnimeStudio core | [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio) | Unity asset parsing, VFSAES deobfuscation, Texture2D decoding |
| `AnimeStudio.Endfield` library | Ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper) (Rust → C#) | Outer VFS container decryption: ChaCha20, CRC32, .blc/.chk parsing, SparkBuffer, AKPK, USM |
| `AnimeStudio.Endfield.Cli` (`endfield-dump`) | Original | CLI tool wiring Stage 1 + 2 + 3 pipeline |

## Features

### Asset extraction
- **Stage 1**: Outer VFS container decryption (ChaCha20 → .blc/.chk → file stream)
- **Stage 2**: Inner UnityFS deobfuscation (VFSAES + LZ4Inv)
- **Stage 3**: Asset decoding (Texture2D → PNG/BMP/TGA)
- **Image classification** (`--classify`): auto-categorize into 51 subdirs (character/ui/icon/equipment/...)
- **Material filtering** (`--exclude-material`): skip PBR slot textures (T_ prefixed)
- **Name-based export**: Regex-match asset names

### Table / Data
- **SparkBuffer → JSON**: Decode binary Table blocks (E1 pipeline), byte-identical to fluffy-dumper output

### Lua
- **XXTEA + base64**: Auto-decrypt Lua scripts in Lua block

### Audio
- **AKPK extraction**: Wwise .pck container → individual WEM streams with `AudioDialog` path mapping
- **WEM → WAV** (via vgmstream-cli, stream copy from PCM)
- **WEM → MP3** (vgmstream stdout → ffmpeg libmp3lame pipe, no intermediate WAV on disk)
- **MP3 quality control**: CBR (`--mp3-bitrate`) or VBR preset (`--mp3-quality best|high|medium|low|minimum|0-9`)
- **Multi-language**: chinese / english / japanese / korean / all

### Video
- **USM demuxer** (native C#, ported from fluffy-dumper Rust): CRI USM → MPEG-2 video + HCA/ADX/AIX audio
- **USM → MP4** (via ffmpeg stream copy, no re-encoding)

### Performance / Reliability
- **Memory-safe**: Chunk-batched processing + memory budget cap + LOH compaction GC, handles 250k+ bundles without OOM
- **Channel pipeline**: Stage 1 serial decryption → bounded channel backpressure → N-thread parallel Stage 2+3
- **Concurrent-safe filename atomic claiming**: prevents same-named texture overwrites across bundles
- **Bounds-checked binary parsing**: hardened against malicious / corrupted data (OOB reads, OOM from untrusted counts)

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

For audio:                          For video:
WEM → vgmstream-cli → WAV          USM → C# demuxer → MPEG-2 + HCA
              ↓ stdout pipe                       ↓ /dev/shm temp files
        ffmpeg libmp3lame                  ffmpeg -c copy
              ↓                                   ↓
            .mp3                                .mp4
```

### Project Structure

```
AnimeStudio/
├── AnimeStudio.Endfield/              ← Outer VFS decryption library (ported from fluffy-dumper)
│   ├── Keys.cs                        ChaCha20 / XXTEA / UnityHashSecret keys
│   ├── BlockType.cs                   25 BlockType enum
│   ├── BlockHashes.cs                 25 VFS directory hashes
│   ├── ChaCha20.cs                    ChaCha20 streaming cipher
│   ├── VfsParser.cs                   .blc metadata parsing + CRC32
│   ├── VfsLoader.cs                   .chk streaming extraction
│   ├── Xxtea.cs                       XXTEA decryption
│   └── Processors/
│       ├── LuaProcessor.cs            Lua base64 + XXTEA
│       ├── SparkBuffer.cs             Binary Table → JSON (full parser)
│       ├── AkpkCrypto.cs              AKPK ChaCha20 obfuscation
│       ├── AkpkPackage.cs             Wwise PCK container parser
│       ├── AudioMap.cs                AudioDialog.bytes → WEM path mapping
│       └── UsmDemuxer.cs              CRI USM container demuxer
│
├── AnimeStudio.Endfield.Cli/          ← CLI tool (endfield-dump)
│   └── Program.cs                     list / dump / inspect / extract / audio / video
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
- For audio: `vgmstream-cli` (auto-detected from `fluffy-dumper/vgmstream/bin/linux/`)
- For MP3 audio / MP4 video: `ffmpeg` (any version with libmp3lame; auto-detected from PATH)

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
exec dotnet "/path/to/AnimeStudio/AnimeStudio.Endfield.Cli/bin/Release/net9.0/endfield-dump.dll" "$@"
EOF
chmod +x ~/.local/bin/endfield-dump
```

## Usage

### List all BlockTypes

```bash
endfield-dump list --vfs ./StreamingAssets
```

### Extract character art (with classification)

```bash
endfield-dump extract \
    --vfs ./StreamingAssets \
    --out ./out \
    --exclude-material \
    --classify \
    --threads 16 \
    --format png \
    --png-compression fast \
    --max-memory-gb 64
```

`--classify` organizes output into category subdirs (character/ui/icon/equipment/...); `--exclude-material` skips T_ prefixed PBR material textures.

### Extract specific texture pattern

```bash
endfield-dump extract \
    --vfs ./StreamingAssets \
    --out ./out \
    --asset-name '^pic_\d+_chr_\d+_[a-z]+$' \
    --threads 16 \
    --format png
```

### Dump Table (SparkBuffer → JSON)

```bash
endfield-dump dump --vfs ./StreamingAssets --out ./out --block Table
# Output: ./out/Table/<TableName>.json (629 JSON files)
```

### Dump Lua (auto-decrypted)

```bash
endfield-dump dump --vfs ./StreamingAssets --out ./out --block Lua
```

### Extract audio (WEM / WAV / MP3)

```bash
# Just WEM (fastest, ~6s for Chinese voice, 1.2 GB)
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language chinese --format wem --block voice --threads 16

# WAV (PCM 16-bit, ~23s, 12 GB)
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language chinese --format wav --block voice --threads 16

# MP3 high quality VBR (~3min, ~2.7 GB)
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language chinese --format mp3 --mp3-quality high --block voice --threads 16

# All languages, all audio blocks (music + sfx + voice + audit)
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language all --format mp3 --block all --threads 16
```

Output paths come from `AudioDialog.bytes` mapping (e.g. `voice/chinese/v1d1/characters/chr_0027_tangtang/chr_xxx_combat.mp3`); unmapped WEMs go to `unmapped/<lang>/<id>.{wem,wav,mp3}`.

### Convert videos (USM → MP4)

```bash
# All video blocks (Video + AuditVideo), 464 USMs → 464 MP4s, ~23s
endfield-dump video --vfs ./StreamingAssets --out ./video \
    --format mp4 --block all --threads 16
```

ffmpeg stream copy preserves original MPEG-2 video + HCA/ADX audio; no re-encoding.

### Inspect resource distribution

```bash
endfield-dump inspect --vfs ./StreamingAssets --limit 50
```

## Command Reference

| Command | Description |
|---|---|
| `list` | List all BlockTypes with chunk/file counts |
| `dump` | Stage 1 decrypt to disk (Lua auto post-processed, Table→JSON, USM→raw, rest raw) |
| `inspect` | Parse bundle internal object type distribution (for research) |
| `extract` | Stage 1 + 2 + 3 full pipeline, regex-export images |
| `audio` | Extract Wwise audio (WEM / WAV / MP3) with AudioDialog path mapping |
| `video` | Extract videos (USM / MP4) via native demuxer + ffmpeg |

### extract Options

| Option | Default | Description |
|---|---|---|
| `--vfs <path>` | required | StreamingAssets directory |
| `--out <dir>` | required | Output directory |
| `--asset-name <regex>` | none | Filter by asset name regex |
| `--bundle-name <regex>` | none | Filter by bundle filename regex |
| `--types <T>` | Texture2D | Object types to export (repeatable) |
| `--block <type>` | Bundle | BlockType to process (repeatable) |
| `--threads N` | min(CPU, 16) | Parallel thread count |
| `--limit N` | 0 (all) | Process only the largest N bundles |
| `--min-size N` | 0 | Skip files smaller than N bytes |
| `--format <f>` | png | Output format: png / bmp / tga |
| `--png-compression <c>` | fast | PNG compression: none / fast / default |
| `--max-memory-gb N` | 64 | Memory budget cap (GB) |
| `--exclude-material` | false | Skip T_ prefix PBR slot textures |
| `--classify` | false | Auto-categorize images into 51 subdirs |
| `--scratch <dir>` | /dev/shm/efend-bundles | Intermediate scratch directory |
| `--keep-bundles` | false | Keep intermediate bundle files (debug) |
| `--no-chunk-batching` | false | Disable per-chunk batching (debug) |

### audio Options

| Option | Default | Description |
|---|---|---|
| `--vfs <path>` | required | StreamingAssets directory |
| `--out <dir>` | required | Output directory |
| `--language <l>` | all | chinese / english / japanese / korean / all |
| `--format <f>` | wem | wem / wav / mp3 (wav/mp3 require vgmstream-cli) |
| `--block <b>` | all | all / audio / initialaudio / auditaudio / voice |
| `--vgmstream <path>` | auto | Path to vgmstream-cli (auto-detected) |
| `--ffmpeg <path>` | auto | Path to ffmpeg (auto-detected, for MP3) |
| `--mp3-bitrate <kbps>` | 192 | CBR mode bitrate (8-320) |
| `--mp3-quality <q>` | none | VBR mode: best/high/medium/low/minimum or 0-9 |
| `--threads N` | CPU | Parallel decode threads |

### video Options

| Option | Default | Description |
|---|---|---|
| `--vfs <path>` | required | StreamingAssets directory |
| `--out <dir>` | required | Output directory |
| `--format <f>` | mp4 | mp4 (USM→MP4 via ffmpeg) / usm (raw) |
| `--block <b>` | all | all / video / auditvideo |
| `--ffmpeg <path>` | auto | Path to ffmpeg (auto-detected) |
| `--threads N` | CPU | Parallel conversion threads |

## Performance

Measured on Ryzen 16-core, 64GB RAM, NVMe + tmpfs (/dev/shm) scratch.

| Scenario | Count | Time | Output Size | Notes |
|---|---|---|---|---|
| Stage 1 full dump (Bundle) | 257,434 | 30.4 s | — | RSS 268 MB, byte-identical to fluffy-dumper |
| Image extract full pipeline (no filter) | 257k bundles | ~9 min | ~58 GB | 16 threads, ~25k PNGs |
| Image extract with classify + exclude-material | 257k bundles | ~9 min | ~50 GB | 51 category dirs |
| Table → JSON | 629 | <1 s | — | byte-identical to fluffy-dumper |
| Lua decrypt | 1,174 | <1 s | — | |
| Audio WEM (Chinese voice) | 26,583 | 5.8 s | 1.2 GB | 16 threads |
| Audio WAV (Chinese voice) | 26,583 | 22.7 s | 12 GB | PCM 16-bit 48kHz |
| Audio MP3 CBR 128 (Chinese voice) | 26,583 | 2m 44s | 1.8 GB | 85% size reduction vs WAV |
| Video USM → MP4 (all) | 464 | 22.7 s | 4.9 GB | ffmpeg stream copy |

### Optimizations over upstream AnimeStudio.CLI

| Aspect | AnimeStudio.CLI (upstream) | EndfieldStudio | Speedup |
|---|---|---|---|
| Pipeline | Serial `foreach` over bundles | Channel-based producer/consumer, N-thread Stage 2+3 | ~16× (CPU bound) |
| PNG compression | Always Level6 (slow) | `--png-compression none/fast/default`, default fast | ~10× PNG write |
| Memory | LOH never compacted | Per-200-bundle LOH GC + memory budget cap | OOM-safe at 250k+ |
| Decryption scratch | Disk temp files | /dev/shm tmpfs | Zero disk IO mid-pipeline |
| Audio backend | None | vgmstream-cli + ffmpeg pipe | New capability |
| Video backend | None | Native C# USM demuxer + ffmpeg copy | New capability |

End-to-end image extract: ~70× faster than upstream (524s vs ~10 hours estimated).

## Known limitations

- **AnimationClip export**: parsing works, but Endfield uses ACL-compressed buffers (`0xac11ac11` magic) for actual keyframe data. AnimeStudio only reads the raw bytes; full ACL decompression is not implemented. Exported metadata-only JSON is not useful without keyframes.
- **Mesh export**: not implemented.
- **Windows builds**: untested (uses Linux-specific Texture2DDecoder and /dev/shm paths).
