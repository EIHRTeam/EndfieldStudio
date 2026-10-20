# EndfieldStudio

Unity game asset extraction tool with VFS container decryption + UnityFS deobfuscation + asset decoding pipeline.

Built on top of [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio), adds outer VFS container decryption (ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper)) and an end-to-end resource extraction pipeline.

## Origin

| Module                                       | Source                                                                             | Description                                                                                |
| -------------------------------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| AnimeStudio core                             | [Escartem/AnimeStudio](https://github.com/Escartem/AnimeStudio)                    | Unity asset parsing, VFSAES deobfuscation, Texture2D decoding                              |
| `AnimeStudio.Endfield` library               | Ported from [fluffy-dumper](https://github.com/Escartem/fluffy-dumper) (Rust → C#) | Outer VFS container decryption: ChaCha20, CRC32, .blc/.chk parsing, SparkBuffer, AKPK, USM |
| `AnimeStudio.Endfield.Cli` (`endfield-dump`) | Original                                                                           | CLI tool wiring Stage 1 + 2 + 3 pipeline                                                   |

## Features

### Asset extraction

- **Stage 1**: Outer VFS container decryption (ChaCha20 → .blc/.chk → file stream)
- **Stage 2**: Inner UnityFS deobfuscation (VFSAES + LZ4Inv)
- **Stage 3**: Asset decoding (Texture2D → PNG/BMP/TGA)
- **Image classification** (`--classify`): auto-categorize into 51 subdirs (character/ui/icon/equipment/...)
- **Material filtering** (`--exclude-material`): skip PBR slot textures (T\_ prefixed)
- **Name-based export**: Regex-match asset names
- **Original-path export** (`original`): Resolve source paths from VFS, AssetBundle containers, and BundleManifest; decode textures, meshes, and text assets with a JSONL index
- **Lean output by default**: PNG / OBJ / bytes plus Material/Mesh JSON details; raw objects, per-object metadata, and embedded resource streams are opt-in

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
- **Mapped-path filtering**: Skip unmapped entries or select operator paths before reading/decoding media; preserve Wwise PLUG containers separately during unfiltered exports

### Video

- **USM demuxer** (native C#, ported from fluffy-dumper Rust): CRI USM → MPEG-2 video + HCA/ADX/AIX audio
- **USM → MP4** (via ffmpeg stream copy, no re-encoding)

### Performance / Reliability

- **Memory-safe**: Chunk-batched processing + memory budget cap + LOH compaction GC, handles 250k+ bundles without OOM
- **Channel pipeline**: Stage 1 serial decryption → bounded channel backpressure → N-thread parallel Stage 2+3
- **Concurrent-safe filename atomic claiming**: prevents same-named texture overwrites across bundles
- **Bounds-checked binary parsing**: hardened against malicious / corrupted data (OOB reads, OOM from untrusted counts)
- **VFS index compatibility**: Supports codeVersion 4 and game 1.6.x codeVersion 5 indexes with 8-byte chunk/file tags

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
EndfieldStudio/
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
│   ├── Program.cs                     list / dump / inspect / extract / audio / video
│   ├── Program.Audio.cs               Audio selection and media output
│   ├── Program.Original.cs            Original-path export and object decoding
│   ├── Program.Original.Resources.cs  Embedded streams with media range redaction
│   └── OriginalAssetManifest.cs       BundleManifest source-path mapping
│
├── AnimeStudio.Endfield.Tests/        ← Executable regression suite
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
- Linux x64 or Windows x64 (Texture2D decoding via `Kyaru.Texture2DDecoder` NuGet, auto-selects platform native lib)
- For audio (WAV/MP3): `vgmstream-cli` — see below
- For MP3 audio / MP4 video: `ffmpeg` (any version with libmp3lame; auto-detected from PATH)

### Installing vgmstream-cli (for audio extraction)

Audio extraction (WAV/MP3) requires `vgmstream-cli`. Three options:

**Option A: From fluffy-dumper (recommended, known-working version)**

```bash
git clone https://github.com/Escartem/fluffy-dumper.git
cd fluffy-dumper
# Build per fluffy-dumper README, then:
# Linux:   vgmstream-cli at fluffy-dumper/vgmstream/bin/linux/vgmstream-cli
# Windows: vgmstream-cli at fluffy-dumper\vgmstream\bin\windows\vgmstream-cli.exe
```

Place the `fluffy-dumper/` directory next to the `EndfieldStudio/` directory (auto-detected), or pass explicitly:

```bash
# Linux
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --vgmstream ./fluffy-dumper/vgmstream/bin/linux/vgmstream-cli \
    --language chinese --format mp3 --threads 16
```

```powershell
# Windows
dotnet endfield-dump.dll audio --vfs .\StreamingAssets --out .\audio `
    --vgmstream .\fluffy-dumper\vgmstream\bin\windows\vgmstream-cli.exe `
    --language chinese --format mp3 --threads 16
```

**Option B: System install**

```bash
# Ubuntu/Debian
sudo apt install vgmstream  # may be older version
# Or build from source: https://vgmstream.org/
```

```powershell
# Windows: Download from https://vgmstream.org/ and add to PATH
# Or: winget install vgmstream
```

After installing, `vgmstream-cli` is auto-detected from PATH, so no `--vgmstream` flag is needed:

```bash
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language chinese --format wav --threads 16
```

**Option C: Skip audio decoding**

```bash
# Output raw .wem files (no vgmstream needed)
endfield-dump audio --vfs ./StreamingAssets --out ./audio \
    --language chinese --format wem --threads 16
```

### Build

**Linux:**

```bash
cd EndfieldStudio
dotnet build AnimeStudio.Endfield.Cli/AnimeStudio.Endfield.Cli.csproj -c Release
# Output: AnimeStudio.Endfield.Cli/bin/Release/net9.0/endfield-dump.dll
```

**Windows (PowerShell):**

```powershell
cd EndfieldStudio
dotnet build AnimeStudio.Endfield.Cli\AnimeStudio.Endfield.Cli.csproj -c Release
# Output: AnimeStudio.Endfield.Cli\bin\Release\net9.0\endfield-dump.dll
```

`endfield-dump` targets .NET 9. The original Windows GUI and CLI retain both
`net8.0-windows` and `net9.0-windows` targets used by `build.ps1`.

### Install as global command

**Linux:**

```bash
./install.sh
```

Or manually:

```bash
cat > ~/.local/bin/endfield-dump << 'EOF'
#!/usr/bin/env bash
exec dotnet "/path/to/EndfieldStudio/AnimeStudio.Endfield.Cli/bin/Release/net9.0/endfield-dump.dll" "$@"
EOF
chmod +x ~/.local/bin/endfield-dump
```

**Windows (PowerShell, as Admin):**

```powershell
# Create a wrapper batch file in a PATH directory
@'
@echo off
dotnet "C:\path\to\EndfieldStudio\AnimeStudio.Endfield.Cli\bin\Release\net9.0\endfield-dump.dll" %*
'@ | Set-Content "$env:ProgramFiles\endfield-dump\endfield-dump.cmd"
```

Or add the DLL directory to PATH and run directly:

```powershell
$env:PATH += ";C:\path\to\EndfieldStudio\AnimeStudio.Endfield.Cli\bin\Release\net9.0"
dotnet endfield-dump.dll extract --vfs .\StreamingAssets --out .\out --exclude-material --classify --threads 16
```

## Usage

> **Linux** examples use `endfield-dump` (if installed) or `dotnet endfield-dump.dll`.
> **Windows** examples use `dotnet endfield-dump.dll` (adjust path as needed).

`--vfs` takes the directory containing `VFS/`, usually
`<game>/Endfield_Data/StreamingAssets`, rather than the game installation root.

### List all BlockTypes

```bash
# Linux
endfield-dump list --vfs ./StreamingAssets
```

```powershell
# Windows
dotnet endfield-dump.dll list --vfs .\StreamingAssets
```

For machine-readable file/chunk metadata:

```bash
endfield-dump list --vfs ./StreamingAssets --block Bundle --json > bundle-index.json
```

`list --json` reads block indexes only and emits schema 1 JSON in stable order,
without version numbers or encrypted headers. All selected blocks must exist;
specify `--block` to avoid requesting absent optional blocks. `--json` and
`--list-files` cannot be combined.

### Extract character art (with classification)

```bash
# Linux
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

```powershell
# Windows
dotnet endfield-dump.dll extract `
    --vfs .\StreamingAssets `
    --out .\out `
    --exclude-material `
    --classify `
    --threads 16 `
    --format png `
    --png-compression fast `
    --max-memory-gb 64
```

`--classify` organizes output into category subdirs (character/ui/icon/equipment/...); `--exclude-material` skips T\_ prefixed PBR material textures.

### Extract specific texture pattern

```bash
endfield-dump extract \
    --vfs ./StreamingAssets \
    --out ./out \
    --asset-name '^pic_\d+_chr_\d+_[a-z]+$' \
    --threads 16 \
    --format png
```

### Export assets under original paths

```bash
# Small sample: the first two Bundle sources in source-path order.
endfield-dump original --vfs ./StreamingAssets --out ./original-sample \
    --block Bundle --limit 2 --threads 1

# Full selection of available supported blocks, using lean output.
endfield-dump original --vfs ./StreamingAssets --out ./original --threads 4

# Export one exact source and add its serialized object bytes.
endfield-dump original --vfs ./StreamingAssets --out ./original-raw \
    --source Bundle/Data/Bundles/Windows/main/3b7bfa16963dba2ba14f1976.ab --raw
```

Bundle names vary by game version; use `list --list-files` or `list --json` to
find sources available in your installation. Repeat `--source` for more than one
exact `Block/source_path` identity. Identities are case-sensitive.

For Unity bundles, Texture2D becomes PNG, renderable Mesh becomes OBJ, and
TextAsset becomes bytes. The converted extension is appended to the source path
(for example, `texture.tga.png`). Material texture slots and Mesh auxiliary data,
including skin/bind-pose fields, are written to `.Material.json` / `.Mesh.json`.
Subassets use `.subassets/<Type>/...`; objects without a resolved path use
`_unmapped/...`. Other supported VFS files retain their source paths, and only
`.lua` files receive Lua decryption/normalization.

Each processed object is recorded in `_index.jsonl`. Object rows include the source bundle,
serialized file, path ID, class ID, source offset/size, output paths, and status.
`source_offset` is within the decompressed serialized file, not the outer bundle
or chunk. `indexed` means the object has no separate decoded file. Valid empty
textures and non-renderable mesh representations may be `raw_only`; this status
does not imply a `.raw` file was written. Failures are recorded as `decode_error`
or `error`, and make the run return a nonzero exit code.

| Mode | Additional output | Material/Mesh details |
| ---- | ----------------- | --------------------- |
| Default | Decoded files and `_index.jsonl` | Separate `.Material.json` / `.Mesh.json`, linked by `details_path` |
| `--raw` | Exact serialized `.raw` objects | Same details JSON as default, still linked by `details_path` |
| `--metadata` | Per-object `.metadata.json` | Stored in metadata's `details` field |
| `--resources` | Embedded streams under `_resources/`, with resource metadata | Same as default unless `--metadata` is also set |
| `--full` | Raw objects, metadata, and resource streams | Stored in metadata's `details` field |

Flags can be combined. Shared resource streams preserve non-media bytes and
offsets; known audio/video ranges are zero-filled and described in resource
metadata. Audio/video blocks, media objects, and classified Table/JsonData blocks
are excluded from `original`; use their dedicated commands.

Each run replaces `_index.jsonl` and leaves previously exported files in place.
Use a fresh output directory for a different selection or option combination.

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

Output paths come from `AudioDialog.bytes` mapping (e.g. `voice/chinese/v1d1/characters/chr_0027_tangtang/chr_xxx_combat.mp3`); unmapped WEMs go to `unmapped/<id>.{wem,wav,mp3}`.

```bash
# Only media with a resolved AudioDialog path.
endfield-dump audio --vfs ./StreamingAssets --out ./named-audio \
    --language chinese --format wem --skip-unmapped

# Filter mapped operator paths before reading or decoding their WEM payloads.
endfield-dump audio --vfs ./StreamingAssets --out ./operator-audio \
    --language chinese --block voice --operator-only --format wem

# Use an explicit hot-update layer, with the base installation as map fallback.
endfield-dump audio --vfs ./Endfield_HotUpdate/Endfield_Data/StreamingAssets \
    --base-vfs ./Endfield/Endfield_Data/StreamingAssets --out ./hotfix-audio \
    --language chinese --block hotfixaudio --skip-unmapped
```

`--audio-filter <regex>` matches resolved paths case-insensitively and also skips
unmapped entries. `--operator-only` uses the `Characters/chr_\d+` pattern.
Valid PLUG plugin containers are preserved byte-for-byte as
`plugins/<Block>/<PCK basename>/<id>.plug` during unfiltered exports; path filters
and `--skip-unmapped` exclude them instead of passing them to an audio decoder.

`nonvoice` selects Audio, InitialAudio, AuditAudio, and HotfixAudio; HotfixAudio can
still contain voice content. `hotfixaudio` selects the requested language blocks
plus HotfixAudio. A sibling `<game>_HotUpdate` directory is auto-detected when
starting from the base game without `--base-vfs`. An explicit `--base-vfs` only
supplies AudioDialog when it is absent in the primary layer; it does not merge
audio packages across layers.

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

# Skip the ten largest bundles and record matching objects/references as JSON.
endfield-dump inspect --vfs ./StreamingAssets --skip 10 --limit 2 \
    --names 'M_ui_.*' --manifest ./inspect-matches.json
```

`inspect` sorts candidates by descending file size before applying `--skip` and
`--limit` (defaults: 0 and 1). `--names` is a case-sensitive regex. The manifest
is a JSON array of matching named objects, container paths, and material
references; use `--names` with `--manifest` to collect matches.

## Command Reference

| Command   | Description                                                                      |
| --------- | -------------------------------------------------------------------------------- |
| `list`    | List all BlockTypes with chunk/file counts                                       |
| `dump`    | Stage 1 decrypt to disk (Lua auto post-processed, Table→JSON, USM→raw, rest raw) |
| `inspect` | Parse bundle internal object type distribution (for research)                    |
| `extract` | Stage 1 + 2 + 3 full pipeline, regex-export images                               |
| `original` | Export supported non-media assets under source paths, with a JSONL index       |
| `audio`   | Extract Wwise audio (WEM / WAV / MP3) with AudioDialog path mapping              |
| `video`   | Extract videos (USM / MP4) via native demuxer + ffmpeg                           |

### extract Options

| Option                  | Default                | Description                            |
| ----------------------- | ---------------------- | -------------------------------------- |
| `--vfs <path>`          | required               | StreamingAssets directory              |
| `--out <dir>`           | required               | Output directory                       |
| `--asset-name <regex>`  | none                   | Filter by asset name regex             |
| `--bundle-name <regex>` | none                   | Filter by bundle filename regex        |
| `--types <T>`           | Texture2D              | Object types to export (repeatable)    |
| `--block <type>`        | Bundle                 | BlockType to process (repeatable)      |
| `--threads N`           | min(CPU, 16)           | Parallel thread count                  |
| `--limit N`             | 0 (all)                | Process only the largest N bundles     |
| `--min-size N`          | 0                      | Skip files smaller than N bytes        |
| `--format <f>`          | png                    | Output format: png / bmp / tga         |
| `--png-compression <c>` | fast                   | PNG compression: none / fast / default |
| `--max-memory-gb N`     | 64                     | Memory budget cap (GB)                 |
| `--exclude-material`    | false                  | Skip T\_ prefix PBR slot textures      |
| `--classify`            | false                  | Auto-categorize images into 51 subdirs |
| `--scratch <dir>`       | /dev/shm/efend-bundles | Intermediate scratch directory         |
| `--keep-bundles`        | false                  | Keep intermediate bundle files (debug) |
| `--no-chunk-batching`   | false                  | Disable per-chunk batching (debug)     |

### original Options

| Option | Default | Description |
| ------ | ------- | ----------- |
| `--vfs <path>` | required | StreamingAssets directory |
| `--out <dir>` | required | Output directory; `_index.jsonl` is replaced each run |
| `--block <type>` | all supported, present blocks | Repeatable; an explicitly requested missing block is an error |
| `--source <Block/source_path>` | none | Repeatable exact source identity; cannot combine with `--bundle-name` or `--limit` |
| `--bundle-name <regex>` | none | Case-insensitive source-path filter; requires explicit Bundle and/or InitialBundle blocks |
| `--limit N` | all | Positive source count in identity order; requires explicit Bundle and/or InitialBundle blocks |
| `--threads N` | min(CPU, 4) | Stage 1 workers; further reduced according to the memory setting |
| `--max-memory-gb N` | 4 | 0.25-128 GiB setting for worker/parser budgets; not a hard process RSS limit |
| `--scratch <dir>` | system temporary directory | Parent of a unique temporary export directory |
| `--raw` | false | Add serialized object bytes while retaining decoded output and details |
| `--metadata` | false | Add per-object metadata; Material/Mesh details move into metadata |
| `--resources` | false | Add embedded resource streams and their metadata, with media range redaction |
| `--full` | false | Enable `--raw`, `--metadata`, and `--resources` together |

### audio Options

| Option                 | Default  | Description                                     |
| ---------------------- | -------- | ----------------------------------------------- |
| `--vfs <path>`         | required | StreamingAssets directory                       |
| `--out <dir>`          | required | Output directory                                |
| `--language <l>`       | all      | chinese / english / japanese / korean / all     |
| `--format <f>`         | wem      | wem / wav / mp3 (wav/mp3 require vgmstream-cli) |
| `--block <b>`          | all      | all / audio / initialaudio / auditaudio / voice / hotfixaudio / nonvoice |
| `--vgmstream <path>`   | auto     | Path to vgmstream-cli (auto-detected)           |
| `--ffmpeg <path>`      | auto     | Path to ffmpeg (auto-detected, for MP3)         |
| `--mp3-bitrate <kbps>` | 192      | CBR mode bitrate (8-320)                        |
| `--mp3-quality <q>`    | none     | VBR mode: best/high/medium/low/minimum or 0-9   |
| `--threads N`          | CPU      | Parallel decode threads                         |
| `--skip-unmapped`      | false    | Only export resolved AudioDialog paths; skip PLUG |
| `--audio-filter <regex>` | none  | Filter mapped paths case-insensitively; skip unmapped entries and PLUG |
| `--operator-only`      | false    | Shorthand path filter for `Characters/chr_\d+` |
| `--base-vfs <path>`    | none     | Base StreamingAssets fallback for a missing AudioDialog table |

### video Options

| Option            | Default  | Description                          |
| ----------------- | -------- | ------------------------------------ |
| `--vfs <path>`    | required | StreamingAssets directory            |
| `--out <dir>`     | required | Output directory                     |
| `--format <f>`    | mp4      | mp4 (USM→MP4 via ffmpeg) / usm (raw) |
| `--block <b>`     | all      | all / video / auditvideo             |
| `--ffmpeg <path>` | auto     | Path to ffmpeg (auto-detected)       |
| `--threads N`     | CPU      | Parallel conversion threads          |

## Validation

Run the regression console program explicitly:

```bash
dotnet run --project AnimeStudio.Endfield.Tests/AnimeStudio.Endfield.Tests.csproj -c Release
```

Coverage includes embedded WEM offsets, PLUG preservation, named-audio policy,
BundleManifest paths, original export output/options, OBJ geometry, resource
redaction, short resource streams, VFS decompression bounds, and v4/v5 indexes.

For additional read-only audio validation against installed data:

```bash
dotnet run --project AnimeStudio.Endfield.Tests/AnimeStudio.Endfield.Tests.csproj \
    -c Release -- --vfs ./StreamingAssets
```

This optional check expects an Audio block with embedded bank media and
unencrypted outer VFS entries for streamed-package header checks.

### Small original-export smoke test

The following two real sources were checked against an installed codeVersion 5
snapshot on 2026-10-20. Choose equivalent sources if your game version has
different bundle names.

```bash
dotnet AnimeStudio.Endfield.Cli/bin/Release/net9.0/endfield-dump.dll original \
    --vfs ./StreamingAssets --out ./smoke-default --block Bundle \
    --source Bundle/Data/Bundles/Windows/main/3b7bfa16963dba2ba14f1976.ab \
    --source Bundle/Data/Bundles/Windows/main/f3a8ef9d8191bb91623cd9ff.ab \
    --threads 1 --max-memory-gb 1
```

Repeat with a fresh `--out ./smoke-raw` directory and `--raw` to check additive
raw output. Both runs exported 2 bundles / 4 objects with zero failures:

- `M_ui_2d_regionmap_mist`: Material details contained 13 texture slots and 124 float properties.
- `S_fx_pian_01`: OBJ contained 4 vertices and 2 triangles with valid vertex indices.
- Material/Mesh details and the OBJ were byte-identical between default and raw modes; raw lengths matched indexed source sizes.

This checks a small installed-data sample, not a complete asset export or Windows
runtime validation. Set `ENDFIELD_DEBUG=1` to include full exception stacks for
errors caught by the CLI's top-level handler.

## Performance

Measured on Ryzen 16-core, 64GB RAM, NVMe + tmpfs (/dev/shm) scratch.

| Scenario                                       | Count        | Time    | Output Size | Notes                                       |
| ---------------------------------------------- | ------------ | ------- | ----------- | ------------------------------------------- |
| Stage 1 full dump (Bundle)                     | 257,434      | 30.4 s  | —           | RSS 268 MB, byte-identical to fluffy-dumper |
| Image extract full pipeline (no filter)        | 257k bundles | \~9 min | \~58 GB     | 16 threads, \~25k PNGs                      |
| Image extract with classify + exclude-material | 257k bundles | \~9 min | \~50 GB     | 51 category dirs                            |
| Table → JSON                                   | 629          | <1 s    | —           | byte-identical to fluffy-dumper             |
| Lua decrypt                                    | 1,174        | <1 s    | —           | <br />                                      |
| Audio WEM (Chinese voice)                      | 26,583       | 5.8 s   | 1.2 GB      | 16 threads                                  |
| Audio WAV (Chinese voice)                      | 26,583       | 22.7 s  | 12 GB       | PCM 16-bit 48kHz                            |
| Audio MP3 CBR 128 (Chinese voice)              | 26,583       | 2m 44s  | 1.8 GB      | 85% size reduction vs WAV                   |
| Video USM → MP4 (all)                          | 464          | 22.7 s  | 4.9 GB      | ffmpeg stream copy                          |

### Optimizations over upstream AnimeStudio.CLI

| Aspect             | AnimeStudio.CLI (upstream)    | EndfieldStudio                                      | Speedup                   |
| ------------------ | ----------------------------- | --------------------------------------------------- | ------------------------- |
| Pipeline           | Serial `foreach` over bundles | Channel-based producer/consumer, N-thread Stage 2+3 | \~16× (CPU bound)         |
| PNG compression    | Always Level6 (slow)          | `--png-compression none/fast/default`, default fast | \~10× PNG write           |
| Memory             | LOH never compacted           | Per-200-bundle LOH GC + memory budget cap           | OOM-safe at 250k+         |
| Decryption scratch | Disk temp files               | /dev/shm tmpfs                                      | Zero disk IO mid-pipeline |
| Audio backend      | None                          | vgmstream-cli + ffmpeg pipe                         | New capability            |
| Video backend      | None                          | Native C# USM demuxer + ffmpeg copy                 | New capability            |

End-to-end image extract: \~70× faster than upstream (524s vs \~10 hours estimated).

## Known limitations

- **AnimationClip export**: parsing works, but Endfield uses ACL-compressed buffers (`0xac11ac11` magic) for actual keyframe data. AnimeStudio only reads the raw bytes; full ACL decompression is not implemented. Exported metadata-only JSON is not useful without keyframes.
- **Mesh export**: `original` exports renderable geometry as OBJ and preserves auxiliary mesh data in JSON. It does not reconstruct a complete FBX, rigged scene, or Unity project; valid empty/baked collision representations are recorded separately.
- **Windows builds**: supported (Texture2DDecoder.Windows + Ooz.dll are included). Audio/video pipelines need Windows builds of vgmstream-cli and ffmpeg. macOS untested.

