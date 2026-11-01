using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Engine.FFmpeg.Capabilities;
using TransFlux.Engine.FFmpeg.Core;
using TransFlux.Engine.FFmpeg.Planner;
using TransFlux.Engine.FFmpeg.Probe;
using TransFlux.Engine.FFmpeg.Runner;
using TransFlux.Engine.FFmpeg.Verifier;
using TransFlux.Hardware.Windows;
using TransFlux.Infrastructure;

var work = Path.Combine(Path.GetTempPath(), "transflux-e2e-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(work);
var outDir = Path.Combine(work, "out");
Directory.CreateDirectory(outDir);
var sample = Path.Combine(work, "sample.mp4");
var dbPath = Path.Combine(work, "state.db");

Console.WriteLine("work=" + work);

var core = FfmpegCoreLocator.Locate();
Console.WriteLine("core ffmpeg=" + core.FfmpegPath + " version=" + core.Version + " hash=" + core.CoreHash[..12]);

// 1) Generate a deterministic SDR BT.709 sample with the pinned core.
Run(core.FfmpegPath, new[]
{
    "-hide_banner","-y","-f","lavfi","-i","testsrc2=size=1280x720:rate=30:duration=5",
    "-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=5",
    "-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p",
    "-colorspace","bt709","-color_primaries","bt709","-color_trc","bt709",
    "-c:a","aac","-b:a","128k", sample
});
var srcHashBefore = Sha(sample);
Console.WriteLine("sample bytes=" + new FileInfo(sample).Length + " sha=" + srcHashBefore[..12]);

// 2) Compose the real stack.
var store = new SqliteJobStore(dbPath);
await store.InitializeAsync(default);
var clock = new SystemClock();
var fs = new WindowsFileSystemService();
var settings = new SettingsService(store, clock);
var options = new SchedulerOptions { MaxConcurrentTranscodes = 1, AllowAutomaticFallback = true };
var engineCaps = new FfmpegEngineCapabilities(core);
var probe = new FfmpegProbe(core);
var planner = new FfmpegPlanner(core);
var runner = new FfmpegRunner(core);
var verifier = new FfmpegVerifier(core);
var caps = new CapabilityService(engineCaps, store, clock);
var hardwareSmoke = args.Contains("--hardware");
var fallbackSmoke = args.Contains("--fallback-smoke");
IMediaRunner effectiveRunner = runner;
if (hardwareSmoke || fallbackSmoke)
{
    var detected = await caps.RefreshAsync(default);
    foreach (var c in detected.Candidates)
        Console.WriteLine($"hardware={c.EncoderName} adapter={c.DeviceId} index={c.Direct3DAdapterIndex} available={c.Available} {c.UnavailableReason} {c.DiagnosticDetail}");
    if (!detected.HasAvailableGpuEncoder) throw new Exception("Hardware smoke requires a verified GPU encoder.");
    if (fallbackSmoke) effectiveRunner = new InvalidLevelRunner(runner);
}
var outputPaths = new OutputPathService(fs, clock);
var committer = new OutputCommitter(store, fs, clock);
var scheduler = new JobScheduler(store, planner, probe, caps, effectiveRunner, verifier, outputPaths, committer, fs, clock, options);
await RecoveryService.RunAsync(store, committer, default);
scheduler.Start();

// 3) Probe source and build a request selecting primary video + audio.
var probeRes = await probe.ProbeAsync(new SourceReference { AbsolutePath = sample }, default);
if (!probeRes.IsSuccess) { Console.Error.WriteLine("PROBE FAILED: " + probeRes.Failure?.TechnicalDetail); return 2; }
var snapshot = probeRes.Value!;
Console.WriteLine($"probed streams={snapshot.Streams.Count} color={snapshot.ColorClassification} durUs={snapshot.DurationUs}");
var videoIdx = snapshot.PrimaryVideo?.SourceIndex;
var audioIdx = snapshot.PrimaryAudio?.SourceIndex;
var selected = new List<int>();
if (videoIdx is int v) selected.Add(v);
if (audioIdx is int a) selected.Add(a);

var request = new ConversionRequest
{
    SourceSnapshotId = snapshot.MediaId,
    Mode = ConversionMode.VideoTranscode,
    Container = ContainerFormat.Mp4,
    SelectedStreamIndices = selected,
    Quality = QualityTier.Balanced,
    Speed = SpeedTier.Balanced,
    Audio = new AudioSpec { Codec = AudioCodec.Aac, BitRateKbps = 192 },
    Output = new OutputPolicy { OutputDirectory = outDir, FileNameOverride = "e2e-result" },
    Hardware = hardwareSmoke || fallbackSmoke ? HardwarePreference.Auto : HardwarePreference.Cpu,
};

var sw = Stopwatch.StartNew();
var jobId = await scheduler.EnqueueAsync(new EnqueueRequest { Request = request, Source = snapshot }, default);
Console.WriteLine("enqueued job=" + jobId);

Job? finalJob = null;
var lastStage = "";
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    await foreach (var ev in scheduler.WatchAsync(new JobWatchQuery { JobId = jobId }, cts.Token))
    {
        if (ev.Job is { } j)
        {
            finalJob = j;
            var stage = j.State.ToString();
            if (stage != lastStage) { Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] state={stage}"); lastStage = stage; }
            if (JobStateMachine.IsTerminal(j.State)) break;
        }
        if (ev.Kind == JobEventKind.OutputCommitted && ev.Output is { } o)
            Console.WriteLine($"  committed -> {o.FinalPath} ({o.Size} bytes) {o.VerificationSummary}");
        if (ev.Kind == JobEventKind.Failed && ev.Failure is { } f)
            Console.WriteLine($"  failure {f.Code}: {f.TechnicalDetail}");
    }
}
catch (OperationCanceledException) { Console.WriteLine("WATCH TIMEOUT"); }

var outputs = await store.GetOutputsForJobAsync(jobId, default);
var ok = finalJob?.State == JobState.Succeeded && outputs.Count == 1 && File.Exists(outputs[0].FinalPath);
Console.WriteLine($"FINAL state={finalJob?.State} outputs={outputs.Count} elapsed={sw.ElapsedMilliseconds}ms");

if (ok)
{
    var finalPath = outputs[0].FinalPath;
    var outProbe = await probe.ProbeAsync(new SourceReference { AbsolutePath = finalPath }, default);
    var srcHashAfter = Sha(sample);
    Console.WriteLine("output probe ok=" + outProbe.IsSuccess +
        (outProbe.IsSuccess ? $" v={outProbe.Value!.VideoStreams.Count()} a={outProbe.Value!.AudioStreams.Count()} {outProbe.Value!.PrimaryVideo?.Width}x{outProbe.Value!.PrimaryVideo?.Height} codec={outProbe.Value!.PrimaryVideo?.Codec}" : ""));
    Console.WriteLine("source unchanged=" + (srcHashBefore == srcHashAfter));
    var attempts = await store.GetAttemptsForJobAsync(jobId, default);
    foreach (var attempt in attempts) Console.WriteLine($"attempt={attempt.Ordinal} device={attempt.DeviceIdentity ?? "CPU"} state={attempt.State} failure={attempt.Failure?.Code} detail={attempt.Failure?.TechnicalDetail}");
    var pass = outProbe.IsSuccess && srcHashBefore == srcHashAfter
        && (!hardwareSmoke || attempts.Count == 1 && attempts[0].DeviceIdentity is not null)
        && (!fallbackSmoke || attempts.Count == 2 && attempts[0].DeviceIdentity is not null && attempts[1].DeviceIdentity is null);
    Run(core.FfmpegPath, new[] { "-v", "error", "-i", finalPath, "-f", "null", "-" });
    Console.WriteLine(pass ? "E2E_RESULT=PASS" : "E2E_RESULT=FAIL");
    return pass ? 0 : 3;
}
Console.WriteLine("E2E_RESULT=FAIL");
return 4;

static void Run(string exe, string[] args)
{
    var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0) throw new Exception("ffmpeg sample generation failed: " + err);
}

static string Sha(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(s));
}

// Fault injection exercises the real runner, scheduler and CPU fallback without changing a driver.
sealed class InvalidLevelRunner(IMediaRunner inner) : IMediaRunner
{
    public Task<RunResult> RunAsync(AttemptContext attempt, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        if (attempt.Candidate.Backend != HardwareBackend.Cpu)
        {
            var injected = attempt.Candidate.Arguments.ToList();
            injected.InsertRange(injected.Count - 1, new[] { "-level:v", "1" });
            attempt = attempt with { Candidate = attempt.Candidate with { Arguments = injected } };
        }
        return inner.RunAsync(attempt, progress, ct);
    }
}
