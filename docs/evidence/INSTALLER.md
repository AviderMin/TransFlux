# Inno Setup installer — build & verification evidence

Date: 2026-11-01. Installer version: **1.0.0.2** (packaging flow modeled on SurgeLink).

## Built artifact

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
