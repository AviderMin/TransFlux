using TransFlux.App.State;
using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Engine.FFmpeg.Capabilities;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;
using TransFlux.Hardware.Windows;
using TransFlux.Infrastructure;

namespace TransFlux.App.Hosting;

/// <summary>
/// Composition root (docs/04): builds concrete engine/infrastructure/hardware services,
/// runs crash recovery, starts the scheduler, and exposes the UI-facing <see cref="AppServices"/>.
/// Pages never touch SQLite, FFmpeg, or processes directly.
/// </summary>
public static class TransFluxAppHost
{
    public static AppServices? Services { get; private set; }

    private static JobScheduler? _scheduler;
    private static SqliteJobStore? _store;

    public static void Initialize()
    {
        if (Services is not null) return;

        var dbPath = AppDataPaths.GetDatabasePath();
        var store = new SqliteJobStore(dbPath);
        try
        {
            // A corrupt or locked state.db must never prevent the window from opening: move the
            // unusable file aside, restore the .bak backup when present, and continue.
            store.InitializeWithRecoveryAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Last-resort fallback: a fresh, uniquely named temp database keeps the workbench usable
            // even if a previous fallback file is itself corrupt or locked. If that also fails we
            // fail with one clear exception instead of an unhandled secondary crash.
            System.Diagnostics.Debug.WriteLine("TransFlux database recovery failed: " + ex);
            try
            {
                var fallbackDirectory = Path.Combine(Path.GetTempPath(), "TransFlux");
                Directory.CreateDirectory(fallbackDirectory);
                store = new SqliteJobStore(Path.Combine(fallbackDirectory, $"state-{Guid.NewGuid():N}.db"));
                store.InitializeWithRecoveryAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception fallbackEx)
            {
                throw new InvalidOperationException(
                    "TransFlux could not initialize its state database or a temporary fallback database.",
                    new AggregateException(ex, fallbackEx));
            }
        }

        var clock = new SystemClock();
        var fs = new WindowsFileSystemService();
        var settingsService = new SettingsService(store, clock);
        var settings = settingsService.GetAsync(CancellationToken.None).GetAwaiter().GetResult();

        var options = new SchedulerOptions
        {
            MaxConcurrentTranscodes = Math.Clamp(settings.MaxConcurrentTranscodes, 1, 4),
            AllowAutomaticFallback = settings.AllowAutomaticFallback,
            FullVerification = settings.FullVerification,
        };

        IMediaProbe probe;
        IExecutionPlanner planner;
        IMediaRunner runner;
        IOutputVerifier verifier;
        IEngineCapabilities engineCapabilities;
        ICapabilityService capabilities;

        try
        {
            var core = FfmpegCoreLocator.Locate();
            engineCapabilities = new FfmpegEngineCapabilities(core);
            probe = new FfmpegProbe(core);
            planner = new FfmpegPlanner(core);
            runner = new FfmpegRunner(core);
            verifier = new FfmpegVerifier(core);
            capabilities = new CapabilityService(engineCapabilities, store, clock);
        }
        catch (Exception ex)
        {
            // Keep the workbench usable when the core is missing; starts will surface CoreMissing.
            System.Diagnostics.Debug.WriteLine("TransFlux media core unavailable: " + ex);
            engineCapabilities = new MissingCoreCapabilities();
            probe = new MissingProbe();
            planner = new MissingPlanner();
            runner = new MissingRunner();
            verifier = new MissingVerifier();
            capabilities = new MissingCapabilityService();
        }

        var outputPaths = new OutputPathService(fs, clock);
        var committer = new OutputCommitter(store, fs, clock);
        var scheduler = new JobScheduler(
            store, planner, probe, capabilities, runner, verifier,
            outputPaths, committer, fs, clock, options);

        try
        {
            RecoveryService.RunAsync(store, committer, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("TransFlux recovery failed: " + ex);
        }

        // Terminal-only staging sweep after recovery has closed/reconciled journals. It is
        // best-effort by design and must never prevent startup; NeedsAttention staging is kept.
        try
        {
            OrphanCleanupService.RunAsync(store, outputPaths, clock, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("TransFlux orphan cleanup failed: " + ex);
        }

        // Recovered work is visible before the user explicitly continues the queue.
        scheduler.PauseQueueAsync(CancellationToken.None).GetAwaiter().GetResult();
        scheduler.Start();

        var presets = new PresetService(store);
        try { presets.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Preset seed failed: " + ex); }

        _scheduler = scheduler;
        _store = store;
        CoreIdentity coreIdentity;
        try
        {
            coreIdentity = engineCapabilities.GetIdentity();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("TransFlux core identity unavailable: " + ex);
            coreIdentity = new CoreIdentity { CoreHash = string.Empty, FfmpegVersion = "(unavailable)", FfmpegPath = string.Empty, FfprobePath = string.Empty };
        }
        Services = new AppServices(probe, scheduler, capabilities, planner, presets, settingsService, engineCapabilities, coreIdentity, options, new HistoryService(store), new WorkspacePersistence(store));

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { scheduler.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { store.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        };
    }
}
