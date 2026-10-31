# Inno Setup installer — build & verification evidence

Date: 2026-10-31. Installer version: **1.0.0.1**.

## Built artifact

- File: `installer/dist/TransFlux-Setup-1.0.0.1.exe`
- Size: **257.3 MB**
- FileVersion / ProductVersion: **1.0.0.1**
- FileDescription: `TransFlux 1.0.0.1 Setup`
- Inno Setup 6.7.3, `lzma2/max` + solid compression.

## Payload

- Unpackaged build: self-contained .NET 10 + framework-dependent Windows App SDK 2.6.4
  (`dotnet publish -r win-x64 --self-contained true -p:WindowsPackageType=None`), version 1.0.0.1.
- Bundled media core at `<install>\ffmpeg\{ffmpeg.exe,ffprobe.exe,core-manifest.json}`
  (installed-layout manifest with `relativePath=ffmpeg.exe`; SHA-256 matching the pinned GPL build).
- Bundled `WindowsAppRuntimeInstall-x64.exe` run once (`--quiet`) as a prerequisite for clean machines.

## Verification (executed on this machine)

1. Silent install `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR=D:\tmp\TransFluxInstalled`
   → **exit 0**; installed `TransFlux.exe`, `ffmpeg\ffmpeg.exe`, `ffmpeg\core-manifest.json`, `unins000.exe`.
2. Launched installed `TransFlux.exe` → process running, main window title **TransFlux**, Responding.
3. End-to-end conversion using the **installed** core (`TRANSFLUX_FFMPEG_DIR=<install>\ffmpeg`):
   **E2E_RESULT=PASS** — probe→plan→encode→verify→commit, output h264 1280x720+AAC, source SHA unchanged, ~2.4 s.
4. Silent uninstall `unins000.exe /VERYSILENT` → **exit 0**, `TransFlux.exe` removed.

## Notes / limitations

- Per-user install (`PrivilegesRequired=lowest`), matching docs/08 “do not run as administrator by default”.
- The Windows App Runtime prerequisite is installed by the setup each run (fast no-op if already present).
- This is an EXE installer alternative to the docs' MSIX route; MSIX remains an M5 deliverable.
- Installer binaries are git-ignored; rebuild with `scripts/build-installer.ps1`.
