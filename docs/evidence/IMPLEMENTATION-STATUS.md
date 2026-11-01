# TransFlux · Implementation status (this build)

Current UX update (2026-11-01): the file-first card workbench replaces the earlier catalogue/table/dialog UI.
Output targets now separate media form, container and codec; see [output format matrix and evidence](OUTPUT-FORMATS.md).
See [UX redesign evidence](UX-workbench-redesign.md) and [current UX specification](../03-ui-ux.md).
Counts below are current for this build; linked screenshot records may show earlier UI where explicitly noted.

Date: 2026-11-01 · Branch: master · Status: **P0 vertical slice implemented**: M0/M1 complete, M2/M4 implemented and tested, file-first workbench and layered output targets; M3 hardware only H.264 partially verified; unsigned Inno Setup 1.0.0.6 built and verified, signed MSIX pending. This is a real build, not a design document.

## What works (executed)

| Area | Status | Evidence |
|---|---|---|
| Solution builds (Domain, Application, Engine.FFmpeg, Hardware.Windows, Infrastructure, App, 4 test projects) | ✅ 0 warnings / 0 errors | `dotnet build TransFlux.slnx -c Debug` |
| Unit + integration tests | ✅ **272 passed / 0 failed** | Domain 83, Application 55, Infrastructure 31, Engine 103 (build 0 warnings / 0 errors) |
| Real Reactor native window renders | ✅ | `docs/evidence/M4-ui-runtime.md`, `ui-tree.txt`, `screenshots/convert.png` |
| Real end-to-end conversion (probe→plan→encode→verify→commit) | ✅ PASS, source unchanged | `docs/evidence/M1-E2E-runtime.md` |
| Pinned GPL FFmpeg core (libx264/NVENC/QSV/AMF/AAC/FLAC) | ✅ hash-pinned | `tools/ffmpeg/core-manifest.json` |
| CPU H.264/AAC MP4+MKV, audio extract (M4A/FLAC/WAV), remux whitelist | ✅ Engine tests | Engine integration tests |
| Queue, state machine, cancel-at-commit-boundary, retry, one auto-fallback | ✅ Application tests | Application tests |
| Layered output targets: 4 video containers + 7 audio containers, 49 video + 10 audio combinations | ✅ real encode/decode of all 59 combinations | `docs/evidence/OUTPUT-FORMATS.md` |
| SQLite persistence, migrations, commit journal, crash reconciliation | ✅ Infrastructure tests | Infrastructure tests |
| DXGI adapter enumeration + capability trials + cache | ✅ real DXGI | `docs/evidence/M3-hardware.md` |
| Reactor UI: file-first task-card workbench, right-side output inspector, top-bar settings/about/history, execution bar | ✅ compiles + real-window checked | `docs/evidence/UX-workbench-redesign.md`, `docs/evidence/OUTPUT-FORMATS.md` |

## Architecture as implemented (docs/04 respected)

```
TransFlux.App (Reactor UI + Hosting composition root)  -> Application -> Domain
  -> Engine.FFmpeg (probe/plan/run/verify/core)  -> Application, Domain
  -> Hardware.Windows (DXGI + capability)        -> Application, Domain
  -> Infrastructure (SQLite/commit/fs/settings)  -> Application, Domain
```

Every requirement in docs/02 flows through `TransFlux.Application.Contracts`; the UI never touches SQLite, FFmpeg or processes directly.

## Deliberate decisions / minor deviations (recorded, not silent)

1. **App TFM is `net10.0-windows10.0.26100.0` with `TargetPlatformMinVersion=10.0.22621.0`** (docs S2 example wrote 22621). The official Reactor template targets 26100; the product minimum OS remains Windows 11 22H2 (22621).
2. **Color classification heuristic**: plain 8-bit yuv420p/nv12 with no HDR side data and no explicit non-BT.709 tags is treated as SDR BT.709; everything else (HDR10/DV/10-bit/wide-gamut/BT.601/conflicting) is blocked from transcode, allowed only for compatible remux (docs/05 S06).
3. **PresetService / SettingsService live in `TransFlux.Infrastructure`** (store-backed); the scheduler remains in `TransFlux.Application`.
4. **Package identity**: packaged `dotnet run` via `winapp 0.7.1` loose-layout currently fails for this project (missing `AppX\Package.appxmanifest` staging); a `dotnet publish` folder layout builds. Unpackaged launch is verified. Proper signed MSIX (M5) remains.
5. Capability probing gates hardware on a real DXGI adapter **and** a successful trial encode; on this box no Intel/AMD adapter is exposed, so QSV/AMF are reported unavailable (not faked).

## Known limitations / not yet verified (honest)

- Real NVENC/QSV/AMF trial-to-transcode not signed off as supported/performance claims; CPU libx264 is the recorded path, although the machine has an RTX 4060 Laptop GPU.
- HDR tone-mapping, subtitle burn-in, target-size/two-pass, run-time pause/resume — all P1, intentionally not implemented. H.265/AV1/VP9 CPU output was added with the layered output targets; hardware paths for them remain unsupported/unverified.
- Accessibility (Narrator/DPI/high-contrast), visual theming, drag-drop implemented but need human/image-capable review.
- Signed MSIX install/upgrade/offline (M0-06/M5) not completed.
- Performance budgets (docs/06) remain targets; E2E wall time ~1.6–1.8 s for a 5 s 720p clip on CPU was observed, not a formal benchmark.

## How to build / run / test

```powershell
pwsh -File scripts/fetch-ffmpeg.ps1                                            # fetch + verify pinned core (once)
dotnet build TransFlux.slnx -c Debug                                          # build everything
dotnet run --project src/TransFlux.App/TransFlux.App.csproj -c Debug -p:WindowsPackageType=None   # UI
dotnet test TransFlux.slnx -c Debug                                           # all tests
dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Debug         # real end-to-end
```

## Next minimal tasks (priority order)

1. Signed MSIX packaging and clean-VM offline install/upgrade (M0-06/M5).
2. Human visual + Narrator + 100/150/200% DPI/high-contrast/theme review of the current workbench.
3. Real NVENC trial + measured performance/quality report on the RTX 4060 Laptop GPU; keep GPU backends explicitly unverified until signed off.
4. Fix any P0 gaps found in a human walkthrough; record docs/06 performance budgets against a controlled benchmark.

Completed 2026-11-01: Inno Setup **1.0.0.6** built from commit `862c5ef`, install/launch/E2E/uninstall verified — see [INSTALLER.md](INSTALLER.md).
