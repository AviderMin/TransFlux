# Media core (FFmpeg/ffprobe)

TransFlux invokes a pinned, trusted `ffmpeg.exe`/`ffprobe.exe` pair as separate processes.
Binaries are **not** committed (see `.gitignore`); fetch them with `scripts/fetch-ffmpeg.ps1`.

- Exact identity, hashes and license notes: [`core-manifest.json`](core-manifest.json).
- Default candidate is a **GPL** build including `libx264`/`libx265`; distribution obligations
  are described in `docs/08-security-release.md`. This is an engineering baseline, not a legal opinion.
- The application resolves the core through its own tooling locator; it never uses `PATH` and never
  downloads a core at runtime.
