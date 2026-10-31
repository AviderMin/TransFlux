# TransFlux Installer (Inno Setup)

Builds a per-user Windows installer under `artifacts/installer/`.

- Output: `artifacts/installer/TransFlux-Setup-1.0.0.1.exe`
- Installs to `%LOCALAPPDATA%\Programs\TransFlux` (no admin required).
- Bundles `TransFlux.exe` (+ .NET runtime), `ffmpeg\ffmpeg.exe`/`ffprobe.exe`/`core-manifest.json`, and the
  Windows App Runtime installer (run once, quiet).

Build: `pwsh -File scripts/build-installer.ps1 -Version 1.0.0.1` (needs .NET 10 SDK + Inno Setup 6).

Unpackaged WinUI needs framework-dependent WinAppSDK + installed Windows App Runtime (bundled as prerequisite);
a fully self-contained WinAppSDK unpackaged build fails activation with 0x80040111.

