# TransFlux Installer (Inno Setup)

All installer files live in this folder (`artifacts/installer`): the Inno script, staging payload,
and the produced setup executable.

## Artifact

- **`TransFlux-Setup-1.0.0.1.exe`** — 257 MB, version **1.0.0.1** (Inno Setup 6, per-user install).
- Installs to `%LOCALAPPDATA%\Programs\TransFlux` (no administrator required).
- Bundles `TransFlux.exe` (+ self-contained .NET runtime), `ffmpeg\ffmpeg.exe`/`ffprobe.exe`/`core-manifest.json`,
  and the Windows App Runtime installer (run once, quiet).

## Build

```powershell
pwsh -File scripts/build-installer.ps1 -Version 1.0.0.1
```

Requires .NET 10 SDK and Inno Setup 6 (ISCC.exe). The script publishes the app into `stage/app`,
stages FFmpeg + the runtime prerequisite, then runs `ISCC.exe transflux.iss` to produce the setup here.

## Layout

- `transflux.iss` — Inno Setup script (`OutputDir=.` → output in this folder).
- `stage/app/` — published unpackaged app (self-contained .NET, framework-dependent WinAppSDK).
- `stage/prereq/` — Windows App Runtime installer.
- `stage/appicon.ico` — setup icon.
- `TransFlux-Setup-1.0.0.1.exe` — built installer.

## Verify

```powershell
.\artifacts\installer\TransFlux-Setup-1.0.0.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR="D:\tmp\TransFluxInstalled"
& "D:\tmp\TransFluxInstalled\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

> A fully self-contained Windows App SDK unpackaged build fails WinUI activation (`0x80040111`); the reliable
> non-MSIX layout is self-contained .NET + framework-dependent WinAppSDK + installed Windows App Runtime
> (bundled as a quiet prerequisite).
