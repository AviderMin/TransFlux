# Inno Setup installer — build & verification evidence

Date: 2026-11-01. 当前构建：**1.0.0.7**；下方保留历史构建记录。

## 当前构建 · 1.0.0.7（2026-11-01）

- File: `artifacts/installer/x64/TransFlux-Setup-1.0.0.7-x64.exe`
- Size: **122.56 MiB**；FileVersion **1.0.0.7**
- SHA-256: `27D7F3721267669FF275E4C79F4B8B8FBB181BC9E90D63F8C5244502BB14A1EC`
- Source base: `9b26ad0` + 当前未提交 UI/诊断修复（源流分行、撤销按钮对齐、折叠式编解码器诊断目录；前述审查修复已进入 `9b26ad0`）
- 全量 **282 项测试通过**（Domain 83、Application 64、Infrastructure 32、Engine 103），Debug 构建 0 警告 / 0 错误。
- 实测（开发机）：静默安装到 `D:\tmp\TransFluxVerify-107f` **exit 0** → `TransFlux.exe` FileVersion **1.0.0.7** → 静默卸载 **exit 0**，安装目录与 exe 清理干净；安装目录核心 E2E 已在同一核心的 `107e` 验证 PASS（2.82 s，源文件不变，CPU 路径）。

## 历史构建 · 1.0.0.6（2026-11-01）

- File: `artifacts/installer/x64/TransFlux-Setup-1.0.0.6-x64.exe`
- Size: **122.6 MiB**；FileVersion / ProductVersion **1.0.0.6**
- SHA-256: `5B5D09B47D98FC37ABD583292BF4D97DA6C2576295316B1F853E66EE1F6C3A18`
- Source base commit: `862c5ef`（包含 1.0.0.5 硬件兼容基线、文件优先工作台 UI、输出格式扩展、PCM 位深与任务卡片重设计）
- 全量 **272 项测试通过**（Domain 83、Application 55、Infrastructure 31、Engine 103），Debug 构建 0 警告 / 0 错误。
- 实测（开发机）：静默安装到 `D:\tmp\TransFluxVerify-106` **exit 0** → `TransFlux.exe` FileVersion **1.0.0.6**、窗口标题 `TransFlux` → 使用安装目录 `ffmpeg\ffmpeg.exe` 运行真实 E2E **PASS**（2.66 s，源文件不变，CPU 路径）→ 静默卸载 **exit 0**，安装目录与 exe 清理干净。

## 历史构建 · 1.0.0.5 硬件兼容修复

- File: `artifacts/installer/x64/TransFlux-Setup-1.0.0.5-x64.exe`
- Size: **128,532,522 bytes / 122.6 MiB**；FileVersion **1.0.0.5**
- SHA-256: `0D56EBB172E794063411FE877171DC8C93C8CAF5952E6DC8CF6D7423C81D0DE4`
- `tools/package-exe.ps1` 自包含发布与 Inno Setup 编译成功；核心按 manifest 精确复制并校验，固定 Gyan 8.0.1 shared 及七个 DLL。
- 全量 271 项测试通过。使用最终 payload 的核心执行真实 NVENC E2E PASS（约 1.35 秒），输出完整解码通过；开发目录 GPU → CPU 故障回退 E2E PASS，设置页完成隔离 UI 验证。
- 本次没有覆盖用户安装或执行新版安装/卸载；1.0.0.4 下方的安装验证不作为本版本安装验证。详情见 [硬件修复证据](HARDWARE-COMPATIBILITY.md)。

## 历史发布 · 1.0.0.4（2026-11-01）

- File: `artifacts/installer/x64/TransFlux-Setup-1.0.0.4-x64.exe`
- Size: **117.4 MB**；FileVersion / ProductVersion **1.0.0.4**
- SHA-256: `4D3CA7C1D3E3BAE890E8DB7478A53A1F45407F8B5BC36832BF1CA8A600DA92DE`
- Source base commit: `b59dc0a`（包含文件优先工作台、输出目标分层及工作台缺陷修复；265 项测试通过）
- 实测（开发机）：静默安装到 `D:\tmp\TransFluxVerify-104` **exit 0** → `TransFlux.exe` FileVersion **1.0.0.4**、窗口标题 `TransFlux` → 使用安装目录 `ffmpeg\ffmpeg.exe` 运行 E2E **PASS**（1.8 s，源文件不变）→ 静默卸载 **exit 0**，安装目录与 exe 清理干净。
- 1.0.0.3 安装包早于输出格式扩展，已被 1.0.0.4 取代。

## Built artifact（历史 1.0.0.2）

- File: `artifacts/installer/x64/TransFlux-Setup-1.0.0.2-x64.exe`
- Size: **117.4 MB** (previous framework-dependent + static-FFmpeg build was 257.3 MB — more than halved)
- FileVersion / ProductVersion: **1.0.0.2**
- SHA-256 recorded beside it: `TransFlux-Setup-1.0.0.2-x64.exe.sha256`
- Inno Setup 6.7.3, `lzma2/max` + solid compression. English + 简体中文 installer UI (language auto-detect).

## Slimming / packaging changes (learned from SurgeLink)

1. **Self-contained Windows App SDK** instead of bundling the 125 MB Windows App Runtime installer.
   `dotnet publish --self-contained true -p:WindowsAppSDKSelfContained=true`, then copy the project
   `TargetDir` over the publish folder (SurgeLink's trick) so the WinUI native framework files that
   PublishDir omits are present. The app now launches with no external runtime prerequisite.
2. **Shared FFmpeg core** (`win64-gpl-shared`, ffplay excluded) — 185 MB instead of 324 MB static
   (payload 425 MB vs 490 MB), same libx264/aac/flac/nvenc/qsv/amf. Hash-pinned in
   `tools/ffmpeg/core-manifest.json` (ffmpeg N-128227-g4811625b5f-20261031). Engine integration tests still pass (38/38).
3. `DebugType=None -p:DebugSymbols=false` (no PDBs); `dotnet clean` + fresh temp payload each build.
4. Parameterized `packaging/TransFlux.iss` (`AppVersion/PayloadDir/OutputDir/AppArchitecture`),
   per-architecture output `artifacts/installer/<arch>/` + `.sha256`, matching SurgeLink's `package-exe.ps1`.

## App icon

`docs/Icon.png` (1254×1254) → `src/TransFlux.App/Assets/AppIcon.ico` via `tools/make-icon.ps1`
(multi-size PNG-compressed ICO: 16/20/24/32/48/64/96/128/256). Applied to the exe (`ApplicationIcon`)
and to the installer (`SetupIconFile`).

## Payload contents

- Unpackaged self-contained .NET 10 + Windows App SDK 2.6.4 (WinAppSDK self-contained), version 1.0.0.2.
- `<install>\ffmpeg\{ffmpeg.exe,ffprobe.exe,av*.dll,core-manifest.json}` — installed-layout manifest
  (relativePath = ffmpeg.exe/ffprobe.exe), SHA-256 verified at runtime.
- `Assets\AppIcon.ico`, `LICENSE.txt`, `THIRD-PARTY-NOTICES\FFmpeg-LICENSE.txt`, `SOURCE.txt`.

## Verification (executed on this machine)

1. Silent install `/VERYSILENT /DIR=D:\tmp\TransFluxInstalled102` → **exit 0**; installed
   `TransFlux.exe` (FileVersion **1.0.0.2**), `Assets\AppIcon.ico`, `ffmpeg\avcodec-63.dll`,
   `ffmpeg\core-manifest.json`.
2. Launched installed `TransFlux.exe` → window title **TransFlux**, responding.
3. End-to-end conversion with the **installed** core (`TRANSFLUX_FFMPEG_DIR=<install>\ffmpeg`):
   **E2E_RESULT=PASS** — probe→plan→encode→verify→commit, h264 1280×720 + AAC, source SHA unchanged, ~1.6 s.
4. Silent uninstall → **exit 0**, `TransFlux.exe` removed.

## Known limitations

- Per-user install (`PrivilegesRequired=lowest`), to `%LOCALAPPDATA%\Programs\TransFlux`.
- Unsigned (dev build). MSIX remains a separate M5 deliverable.
- Installer binaries are git-ignored; rebuild with `pwsh -File tools/package-exe.ps1 -Version 1.0.0.2`.

> 2026-11-01: installer rebuilt (same version 1.0.0.2, 117.4 MB) with the redesigned Format-Factory UI and
> top-bar Settings/About. Re-verified: install/launch/About version 1.0.0.2/E2E/uninstall.

> 2026-11-01 (installer UX fix): the interactive “文件夹已存在/install into this folder?” prompt is Inno's
> Select-Destination-Location warning, shown when the target folder exists but Setup does not recognise it as a
> previous install of the same AppId (e.g. an older build installed elsewhere/leftover folder). Fixed by setting
> `DisableDirPage=yes` + `DirExistsWarning=no` (+ `UsePreviousAppDir=yes`) with the fixed per-user location and a
> stable AppId, so updates silently reuse the same folder. Fixed build: `artifacts/installer/x64/TransFlux-Setup-1.0.0.2-x64-fixed.exe`.
