# M0 · Technical baseline evidence

Date: 2026-10-31 (Asia/Shanghai). Status: **Executed on the development machine** (not a clean VM).

## Toolchain (verified)

| Item | Value |
|---|---|
| OS | Windows 11 (build 10.0.28000), x64 |
| CPU | 12th Gen Intel Core i7-12650H |
| GPU | NVIDIA GeForce RTX 4060 Laptop GPU (driver 32.0.15.9186); Intel iGPU present |
| .NET SDK | 10.0.401 (runtimes 10.0.8 / 10.0.12 / 8.0.31) |
| Windows App Runtime | 2.6.4 installed |
| Developer Mode | Enabled |

## Reactor (M0-02/03)

- Template pack `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates::0.0.7-alpha` installed;
  `dotnet new reactor` available.
- Smoke app scaffolded from the official template, `dotnet build -c Debug` succeeded (0 warnings, 0 errors).
- Smoke app launched **packaged** via `dotnet run` (winapp 0.7.1 loose-layout registration):
  process alive with a main window (`MainWindowTitle = SmokeApp`). Confirms Reactor renders a real
  native WinUI 3 window on this machine. (Unpackaged direct `.exe` start of a packaged app fails with
  CLR 0xE0434352, as expected — packaged identity is required.)
- Screenshot captured at build time (`D:\tmp\smoke-shot.png`, 1707x1067). _The executing model cannot
  view images; visual sign-off of the Reactor UI is left to a human reviewer / the UI milestone._

## Media core (M0-04)

- Downloaded BtbN `win64-gpl` static build; extracted to `tools/ffmpeg/ffmpeg-master-latest-win64-gpl/`.
- `ffmpeg -version`: **N-128212-g17a4a474f4-20261030**, `--enable-gpl --enable-version3`, includes
  `libx264`, `libx265`, `h264_nvenc`, `h264_qsv`, `h264_amf`, `aac`, `flac`, `libvmaf`.
- Exact SHA-256 pinned in [`tools/ffmpeg/core-manifest.json`](../../tools/ffmpeg/core-manifest.json).
- License: **GPLv3** baseline candidate (docs/08); distribution obligations tracked; not a legal opinion.

## Solution skeleton (executed)

- `TransFlux.sln` with projects: Domain, Application, Engine.FFmpeg, Hardware.Windows, Infrastructure,
  App, and four test projects. Full solution builds:

```
dotnet build TransFlux.sln -c Debug
# -> 0 warnings, 0 errors; TransFlux.dll (WinExe, win-x64) produced
```

## Not yet verified (explicit)

- Clean-VM offline MSIX install (M0-06) — pending packaging milestone.
- GPU trial-run capability and full-path hardware encode — pending M3 (machine has NVIDIA + Intel; no AMD).
- All performance numbers remain targets (docs/06), not measured.
