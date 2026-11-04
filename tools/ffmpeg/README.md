# Media core (FFmpeg/ffprobe)

TransFlux invokes a pinned, trusted `ffmpeg.exe`/`ffprobe.exe` pair as separate processes.
Binaries are **not** committed (see `.gitignore`); fetch them with `scripts/fetch-ffmpeg.ps1`.

Current compatibility baseline: **Gyan FFmpeg 8.0.1 full_build-shared**, GPLv3.
It passed real NVENC conversion on RTX 4060 Laptop / NVIDIA 591.86.
The former nightly required a newer NVENC API. See the hardware/verification summary in [DEVELOPMENT-SUMMARY.md](../../docs/DEVELOPMENT-SUMMARY.md).
The manifest pins the archive, executable and DLL hashes; packaging copies only declared files.
Core identity includes runtime DLL hashes.

- Exact identity, hashes and license notes: [`core-manifest.json`](core-manifest.json).
- Default candidate is a **GPL** build including `libx264`/`libx265`; distribution obligations
  are described in [DEVELOPMENT-SUMMARY.md](../../docs/DEVELOPMENT-SUMMARY.md). This is an engineering baseline, not a legal opinion.
- The application resolves the core through its own tooling locator; it never uses `PATH` and never
  downloads a core at runtime.
