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
        store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

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

        scheduler.Start();

        var presets = new PresetService(store);
        try { presets.GetAllAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Preset seed failed: " + ex); }

        _scheduler = scheduler;
        _store = store;
        Services = new AppServices(probe, scheduler, capabilities, planner, presets, settingsService, engineCapabilities, options);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { scheduler.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { store.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        };
    }
}
