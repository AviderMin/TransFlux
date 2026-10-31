# TransFlux · Implementation Baseline (frozen for the coding team)

This is the working contract for parallel implementation. It complements `/docs`
(which is authoritative). Do not change the framework, the module dependency
direction, or the frozen contracts in `TransFlux.Domain` / `TransFlux.Application.Contracts`
without the Lead's agreement.

## Locked toolchain (M0)

| Item | Value |
|---|---|
| .NET SDK | 10.0.401 (`global.json`) |
| Target (App/Engine/Hardware) | `net10.0-windows10.0.22621.0` |
| Target (Domain/Application/Infrastructure) | `net10.0` |
| Reactor | `Microsoft.UI.Reactor` 0.1.0-preview.16 |
| Windows App SDK | 2.6.4 |
| Media core | BtbN GPL shared build (ffmpeg N-128227), pinned in `tools/ffmpeg/core-manifest.json` |

The full solution already builds. Keep it building after every change
(`dotnet build TransFlux.sln -c Debug`).

## Module dependency direction (docs/04)

```
App -> Application -> Domain
App -> Engine.FFmpeg -> Application, Domain
App -> Hardware.Windows -> Application, Domain
App -> Infrastructure -> Application, Domain
Engine/Hardware/Infrastructure must NOT reference App.
Domain references no UI/Reactor/SQLite/Process.
```

## Frozen contracts

- `TransFlux.Domain` — models, value types, rules (`MediaCompatibility`, `OutputNaming`, `BuiltInPresets`), `JobStateMachine`.
- `TransFlux.Application.Contracts` — every interface and DTO used across modules:
  `IEngineCapabilities, IMediaProbe, ICapabilityService, IExecutionPlanner, IMediaRunner,
  IOutputVerifier, IJobService, IPresetService, ISettingsService, IJobStore, IFileSystemService,
  IOutputPathService, IOutputCommitter, IClock`, plus `Result<T>`, `AttemptContext`,
  `EngineProgress`, `RunResult`, `VerificationResult`, etc. **Read these files first.**

Argument placeholders: the planner emits `ExecutionCandidate.Arguments`; the runner replaces
`PlanPlaceholders.Input` / `PlanPlaceholders.Output` with the absolute input path / attempt temp path.
Never interpolate user text into filters.

## Required public concrete types (names are a contract)

### TransFlux.Engine.FFmpeg
- `FfmpegCorePaths(string FfmpegPath, string FfprobePath, string CoreHash, string Version)`
- `FfmpegCoreLocator.Locate(string? explicitDirectory = null) : FfmpegCorePaths` — resolves the exe
  pair from `core-manifest.json` near the app/base dir, verifies SHA-256, throws `CoreIntegrityException` on mismatch.
- `FfmpegProbe : IMediaProbe` — ctor `(FfmpegCorePaths core)`
- `FfmpegPlanner : IExecutionPlanner` — ctor `(FfmpegCorePaths core)`
- `FfmpegRunner : IMediaRunner` — ctor `(FfmpegCorePaths core)`
- `FfmpegVerifier : IOutputVerifier` — ctor `(FfmpegCorePaths core)`
- `FfmpegEngineCapabilities : IEngineCapabilities` — ctor `(FfmpegCorePaths core)`

### TransFlux.Infrastructure
- `SqliteJobStore : IJobStore, IAsyncDisposable` — ctor `(string databasePath)`
- `WindowsFileSystemService : IFileSystemService` — parameterless
- `SystemClock : IClock` — parameterless
- `OutputPathService : IOutputPathService` — ctor `(IFileSystemService fs, IClock clock)`
- `OutputCommitter : IOutputCommitter` — ctor `(IJobStore store, IFileSystemService fs, IClock clock)`
- `PresetService : IPresetService` — ctor `(IJobStore store)`
- `SettingsService : ISettingsService` — ctor `(IJobStore store, IClock clock)`

### TransFlux.Hardware.Windows
- `DxgiAdapterEnumerator.Enumerate() : IReadOnlyList<AdapterInfo>` (static)
- `CapabilityService : ICapabilityService` — ctor `(IEngineCapabilities engine, IJobStore store, IClock clock)`

### TransFlux.Application (services — owned by appcore)
- `JobScheduler : IJobService, IAsyncDisposable` — ctor
  `(IJobStore store, IExecutionPlanner planner, IMediaProbe probe, ICapabilityService caps,
    IMediaRunner runner, IOutputVerifier verifier, IOutputPathService outputPaths,
    IOutputCommitter committer, IFileSystemService fs, IClock clock, SchedulerOptions options)`
  Must expose `Start()` (begins the dispatch loop) and implement the full `IJobService`.
- `RecoveryService.RunAsync(IJobStore store, IOutputCommitter committer, CancellationToken)` — mark
  non-terminal jobs `Interrupted`, reconcile open commit journals.

### TransFlux.App (owned by ui; Lead owns entry + host)
- Lead owns `src/TransFlux.App/App.cs` and `src/TransFlux.App/Hosting/**` (composition root).
- UI owns `Components/**, State/**, Interop/**, Theme/**, Resources/**`.
- UI components take `TransFlux.Application.Contracts` interfaces via constructor parameters; the host supplies them.

## Threading / lifecycle rules (docs/04)

- Render is pure: no I/O, no process start, no DB. Use hooks/effects for async work with cancellation.
- All background events projected to UI dispatcher. High-frequency progress coalesced to <=4 Hz.
- One active attempt per job; conditional store updates; monotonic progress sequence.
- Cancel must terminate the process tree; never publish partial output; commit boundary is atomic.

## Definition of done for a module

1. `dotnet build` clean for the owning project. 2. Meaningful tests pass (real core for engine).
3. Error/cancel paths handled; no dangling processes/subscriptions. 4. No `NotImplementedException`/dead buttons.
5. Report exact commands + results + unverified items back to the Lead.