# TransFlux Installer (Inno Setup)

Builds a **per-user** Windows installer (`TransFlux-Setup-1.0.0.1.exe`) that installs a working
TransFlux: a self-contained .NET app + bundled FFmpeg core + the Windows App Runtime prerequisite.

## Result

- Output: `installer/dist/TransFlux-Setup-<version>.exe` (currently **1.0.0.1**)
- Installs to: `%LOCALAPPDATA%\Programs\TransFlux` (no administrator required)
- Includes: `TransFlux.exe` (+ .NET runtime), `ffmpeg\ffmpeg.exe` + `ffprobe.exe` + `core-manifest.json`
  (installed-layout manifest, SHA-256 verified at runtime), and the Windows App Runtime installer run once.

## Build

```powershell
pwsh -File scripts/build-installer.ps1 -Version 1.0.0.1
```

Requires .NET 10 SDK and Inno Setup 6 (ISCC.exe). First run downloads FFmpeg (via
`scripts/fetch-ffmpeg.ps1`) and the Windows App Runtime installer.

## Why framework-dependent Windows App SDK?

A fully self-contained Windows App SDK (WinAppSDK) unpackaged build fails WinUI activation
(`0x80040111 ClassFactory cannot supply requested class`) unless the bootstrapper/identity is
initialized. The reliable non-MSIX layout is: **self-contained .NET** + **framework-dependent WinAppSDK**
+ the Windows App Runtime installed (bundled here as a quiet prerequisite). On a machine that already has
Windows App Runtime 2.6.4 the prerequisite is a fast no-op.

## Verify

```powershell
# silent install to a temp dir (per-user, no elevation)
.\installer\dist\TransFlux-Setup-1.0.0.1.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOICONS /DIR="D:\tmp\TransFluxInstalled"
# uninstall
& "D:\tmp\TransFluxInstalled\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```
