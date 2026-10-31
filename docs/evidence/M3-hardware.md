# M3 · Hardware capability probing (executed where possible)

Date: 2026-10-31. Machine: Windows 11 x64, NVIDIA GeForce RTX 4060 Laptop GPU (driver 32.0.15.9186),
12th Gen Intel Core i7-12650H.

## Real DXGI enumeration (`DxgiAdapterEnumerator`, executed in Infrastructure tests)

- 2× `NVIDIA GeForce RTX 4060 Laptop GPU` (vendor 0x10DE, device 0x28A0, ~8.34 GB dedicated, non-software)
- 1× `Microsoft Basic Render Driver` (vendor 0x1414 / 0x008C, software = true)

The machine exposes **no Intel iGPU adapter through DXGI** (and no AMD GPU), so the capability service
honestly reports QSV/AMF as “no matching adapter” rather than guessing from a name table.

## Core encoder catalogue (pinned build)

`h264_nvenc`, `h264_qsv`, `h264_amf`, `libx264`, `libx265`, `aac`, `flac` are all compiled in
(see `tools/ffmpeg/core-manifest.json`).

## Capability model

`CapabilityService` only marks a hardware candidate **Available** after (a) a matching DXGI adapter is
present and (b) `FfmpegEngineCapabilities.ValidateEncoderAsync` completes a bounded trial encode. Results
are cached by `coreHash + osBuild + probeSchemaVersion + adapterLuid + driverVersion`. CPU `libx264` is
always the baseline.

## Not executed

- A real NVENC trial-to-transcode on this GPU was not signed off as a performance/quality result; only CPU
  libx264 is used in the recorded E2E run. Per docs/06/09 the GPU backends remain “unverified” until a real
  trial + measured report exists, and can be disabled in release without being mislabelled as supported.
