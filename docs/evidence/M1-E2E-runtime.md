# M1 · CPU vertical slice — end-to-end (executed)

Date: 2026-10-31. Status: **PASS** on the development machine, real components and the pinned core.

## What was exercised

A Lead-owned harness (`tools/TransFlux.E2E`) composes the **real** stack exactly as the app does:
`FfmpegCoreLocator` → `FfmpegProbe` / `FfmpegPlanner` / `FfmpegRunner` / `FfmpegVerifier` /
`FfmpegEngineCapabilities`, `CapabilityService`, `SqliteJobStore`, `OutputPathService`,
`OutputCommitter`, `JobScheduler`, `RecoveryService`. It generates an SDR BT.709 sample with the
pinned ffmpeg, probes it, enqueues a real VideoTranscode (MP4 H.264 + AAC) job, and watches it to a
terminal state.

## Command

```
dotnet run --project tools/TransFlux.E2E/TransFlux.E2E.csproj -c Debug
```

## Output

```text
work=C:\Users\AviderMin\AppData\Local\Temp\transflux-e2e-ed123c7e
core ffmpeg=...\tools\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe version=N-128212-g17a4a474f4-20261030 hash=C16F1190C3CF
sample bytes=4595865 sha=de58c5093432
probed streams=2 color=SdrBt709 durUs=5000000
enqueued job=257915ea-c440-4bb9-898f-aca595f5d146
[207ms] state=Queued
[233ms] state=Preflighting
[1180ms] state=Running
[2231ms] state=Verifying
[3052ms] state=Committing
[3092ms] state=Succeeded
FINAL state=Succeeded outputs=1 elapsed=3094ms
output probe ok=True v=1 a=1 1280x720 codec=h264
source unchanged=True
E2E_RESULT=PASS
```

## Assertions verified

- Source fingerprint (SHA-256) is **unchanged** before/after conversion.
- The job traversed the real state machine: Queued → Preflighting → Running → Verifying → Committing → Succeeded.
- Exactly one `OutputRecord` persisted; the committed file exists and re-probes as 1 video (h264 1280x720) + 1 audio.
- Verification must pass before commit (the job could not reach Succeeded otherwise).

## Notes / not covered here

- This proves the CPU/H.264 path. Hardware codec arg-sets are unit/integration-covered but a real
  NVENC/QSV trial-to-encode is exercised by `CapabilityService` (see M3) and the UI, not asserted in this harness.
- Trim/VFR/rotation/SAR and multi-audio/video-remux correctness are covered by the Engine integration tests
  (38 passing) and Domain compatibility tests.
