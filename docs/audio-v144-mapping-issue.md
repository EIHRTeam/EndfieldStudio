# v1.4.4 音频提取映射问题分析

## 状态

**已解决。** v1.4.4 的语言语音 PCK 分布在热更新层（HotUpdate overlay），
不在基础安装包中。使用 HotUpdate VFS 作为主源即可正常提取。

## 根因

v1.4.4 的音频数据分两层存储：

| 层 | VFS Block | WEM ID 格式 | 内容 | 路径映射 |
|---|---|---|---|---|
| **基础安装包** (StreamingAssets) | Audio | 32位 media ID | SFX/Music/UI 共享音频 | 无（AudioDialog 不包含） |
| **热更新层** (HotUpdate) | AudioChinese/English/Japanese/Korean | FNV-1a 64 hash | **干员语音+剧情对话** | ✅ 正常映射 |

基础安装包的 Audio block 只有共享音频（55,433 个 WEM），用 32 位 Wwise media ID 索引。
这些音频不在 AudioDialog 表中，没有人类可读路径——这是正常的，它们通过 Wwise Event 系统触发。

语言的语音 PCK（AudioChinese 等）在热更新 overlay 中，使用传统的 FNV-1a 64 hash 格式，
能被 AudioDialog 正确映射到 `voice/<lang>/<path>` 路径。

## 验证结果（2026-07-16）

### 从 HotUpdate overlay 提取

```
Building multi-language audio hash map...
  Found 113732 audio entries (4 languages)
  Extracting from AudioChinese...
    Found 2 PCK files
    Done: processed 30058 entries (3266 unmapped, 0 errors)
  Extracting from AudioEnglish...
    Found 2 PCK files
    Done: processed 30039 entries (3266 unmapped, 0 errors)
  ...
Complete: extracted 120572 files (13467 unmapped, 0 errors)
```

| 指标 | 数值 |
|------|------|
| 成功映射提取（voice/） | **107,105 files** |
| 未映射（unmapped/） | 13,467 files（SFX/Music，正常） |
| lizhiyan (chr_0032) | **660 files**（正确路径） |
| 提取错误 | 0 |

### 从基础安装包提取（不完整）

```
Complete: extracted 55832 files (55832 unmapped, 0 errors)
```

全部 55,832 个 WEM unmapped——因为这些是共享音频，不在 AudioDialog 中。

## 已完成的修复

### 1. EndfieldStudio CLI — 自动检测 HotUpdate overlay

[Program.cs](../AnimeStudio.Endfield.Cli/Program.cs) `RunAudioPipeline` 方法：

自动检测游戏目录旁边的 `<game>_HotUpdate` overlay。如果存在，自动切换：
- 主 VFS → HotUpdate overlay
- Fallback VFS → 基础安装包

用户无需手动指定 `--base-vfs`，CLI 自动处理。

### 2. EndfieldStudio CLI — dump 命令热更缺失提醒

[Program.cs](../AnimeStudio.Endfield.Cli/Program.cs) `WarnIfHotUpdateMissing` 方法：

`dump` 命令执行前检测 HotUpdate overlay 是否存在。如果缺失，输出提醒：

```
⚠️  HotUpdate overlay not detected!
   Expected at: /path/to/game_HotUpdate/Endfield_Data/StreamingAssets
   Language voice PCKs (AudioChinese/English/Japanese/Korean) are distributed
   via hot-update. Without it, audio extraction will only get shared SFX/Music,
   missing ALL operator voices and story dialog audio.
   Run the EIHR game downloader to fetch hot-update data first:
     eihr hotupdate --path "/path/to/game"
```

### 3. endfield-api pipeline — 音频提取优先使用 HotUpdate VFS

相邻 `endfield-api` 项目的 `app/pipeline.py`：

4 处音频提取调用全部修改：
- `_extract_audio`（全量解包）
- `run_audio`（独立音频提取 API）
- `run_hotupdate_extract`（热更新全解）
- `run_auto_update`（自动更新）

逻辑：检测 `settings.hotupdate_vfs_path` 是否存在，存在则用 `--vfs <hotupdate> --base-vfs <streaming_assets>`，
否则只用 `--vfs <streaming_assets>` 并输出警告日志。

### 4. 其他修复（仍然有效）

- BNK 解密（`AkpkCrypto.DecryptVfs`）
- HotfixAudio block 支持（`BlockType.HotfixAudio`）
- 多语言 hash map（`AudioMap.FromAudioDialogMultiLanguage`）

## 使用方式

### 正确的音频提取流程

1. 先下载热更新数据：
   ```bash
   eihr hotupdate --path /path/to/endfield
   ```

2. 然后提取音频（CLI 自动检测 HotUpdate overlay）：
   ```bash
   dotnet endfield-dump.dll audio --vfs /path/to/endfield/Endfield_Data/StreamingAssets --out output_dir
   ```

### 手动指定 VFS 源

```bash
# 主源 = HotUpdate，fallback = 基础包
dotnet endfield-dump.dll audio \
  --vfs /path/to/endfield_HotUpdate/Endfield_Data/StreamingAssets \
  --base-vfs /path/to/endfield/Endfield_Data/StreamingAssets \
  --out output_dir
```

## 关键文件

| 文件 | 说明 |
|------|------|
| [Program.cs](../AnimeStudio.Endfield.Cli/Program.cs) | CLI 主程序，含 HotUpdate 自动检测 + dump 提醒 |
| [AudioMap.cs](../AnimeStudio.Endfield/Processors/AudioMap.cs) | FNV-1a 64 哈希映射，含多语言支持 |
| [AkpkPackage.cs](../AnimeStudio.Endfield/Processors/AkpkPackage.cs) | PCK/BNK 解析 |
| `endfield-api/app/pipeline.py` | API 管线，4 处音频提取使用 HotUpdate VFS |
