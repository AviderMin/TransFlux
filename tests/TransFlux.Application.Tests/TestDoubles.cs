using System.Collections.Concurrent;
using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;
using TransFlux.Domain.Presets;

namespace TransFlux.Application.Tests;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}

internal sealed class InMemoryJobStore : IJobStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly Dictionary<Guid, Attempt> _attempts = new();
    private readonly List<OutputRecord> _outputs = new();
    private readonly Dictionary<string, CommitJournalEntry> _journals = new();
    private readonly Dictionary<Guid, Preset> _presets = new();
    private readonly Dictionary<string, (string Payload, DateTimeOffset Tested)> _capabilities = new();
    private readonly Dictionary<string, string> _settings = new();
    private long _enqueueOrder;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<long> NextEnqueueOrderAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(++_enqueueOrder);
        }
    }

    public Task CreateJobAsync(Job job, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryAdd(job.JobId, job))
            {
                throw new InvalidOperationException($"Job {job.JobId} already exists.");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Test hook invoked after a successful job update (used to force cancellation windows).</summary>
    public Func<Job, Task>? AfterUpdateJob { get; set; }

    public async Task UpdateJobAsync(Job job, long expectedRevision, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(job.JobId, out var current) || current.Revision != expectedRevision)
            {
                throw new InvalidOperationException($"Job {job.JobId} revision mismatch.");
            }
            _jobs[job.JobId] = job;
        }
        if (AfterUpdateJob is not null)
        {
            await AfterUpdateJob(job).ConfigureAwait(false);
        }
    }

    public Task<Job?> GetJobAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);
        }
    }

    public Task<IReadOnlyList<Job>> GetJobsAsync(IReadOnlyCollection<JobState>? states, CancellationToken ct)
    {
        lock (_gate)
        {
            var result = _jobs.Values
                .Where(j => states is null || states.Contains(j.State))
                .OrderBy(j => j.EnqueueOrder)
                .ToList();
            return Task.FromResult<IReadOnlyList<Job>>(result);
        }
    }

    public Task CreateAttemptAsync(Attempt attempt, CancellationToken ct)
    {
        lock (_gate)
        {
            _attempts[attempt.AttemptId] = attempt;
        }
        return Task.CompletedTask;
    }

    public Task UpdateAttemptAsync(Attempt attempt, CancellationToken ct)
    {
        lock (_gate)
        {
            _attempts[attempt.AttemptId] = attempt;
        }
        return Task.CompletedTask;
    }

    public Task<Attempt?> GetAttemptAsync(Guid id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_attempts.TryGetValue(id, out var a) ? a : null);
        }
    }

    public Task<IReadOnlyList<Attempt>> GetAttemptsForJobAsync(Guid jobId, CancellationToken ct)
    {
        lock (_gate)
        {
            var list = _attempts.Values.Where(a => a.JobId == jobId).OrderBy(a => a.Ordinal).ToList();
            return Task.FromResult<IReadOnlyList<Attempt>>(list);
        }
    }

    public Task SaveOutputAsync(OutputRecord record, CancellationToken ct)
    {
        lock (_gate)
        {
            _outputs.Add(record);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutputRecord>> GetOutputsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OutputRecord>>(_outputs.ToList());
        }
    }

    public Task<IReadOnlyList<OutputRecord>> GetOutputsForJobAsync(Guid jobId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OutputRecord>>(_outputs.Where(o => o.JobId == jobId).ToList());
        }
    }

    public Task UpsertJournalAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        lock (_gate)
        {
            _journals[entry.CommitToken] = entry;
        }
        return Task.CompletedTask;
    }

    public Task<CommitJournalEntry?> GetJournalAsync(string token, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_journals.TryGetValue(token, out var e) ? e : (CommitJournalEntry?)null);
        }
    }

    public Task<IReadOnlyList<CommitJournalEntry>> GetOpenJournalsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var open = _journals.Values
                .Where(j => j.Phase is CommitPhase.Prepared or CommitPhase.Published)
                .ToList();
            return Task.FromResult<IReadOnlyList<CommitJournalEntry>>(open);
        }
    }

    public Task DeleteJournalAsync(string token, CancellationToken ct)
    {
        lock (_gate)
        {
            _journals.Remove(token);
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Preset>> GetPresetsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Preset>>(_presets.Values.ToList());
        }
    }

    public Task UpsertPresetAsync(Preset preset, CancellationToken ct)
    {
        lock (_gate)
        {
            _presets[preset.PresetId] = preset;
        }
        return Task.CompletedTask;
    }

    public Task DeletePresetAsync(Guid presetId, CancellationToken ct)
    {
        lock (_gate)
        {
            _presets.Remove(presetId);
        }
        return Task.CompletedTask;
    }

    public Task SaveCapabilityAsync(string cacheKey, string payloadJson, DateTimeOffset testedUtc, CancellationToken ct)
    {
        lock (_gate)
        {
            _capabilities[cacheKey] = (payloadJson, testedUtc);
        }
        return Task.CompletedTask;
    }

    public Task<(string PayloadJson, DateTimeOffset TestedUtc)?> GetCapabilityAsync(string cacheKey, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_capabilities.TryGetValue(cacheKey, out var v))
            {
                return Task.FromResult<(string, DateTimeOffset)?>(v);
            }
            return Task.FromResult<(string, DateTimeOffset)?>(null);
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_settings));
        }
    }

    public Task SaveSettingAsync(string key, string valueJson, CancellationToken ct)
    {
        lock (_gate)
        {
            _settings[key] = valueJson;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Job>> MarkActiveJobsInterruptedAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var activeStates = new[]
            {
                JobState.Preflighting, JobState.Running, JobState.Verifying,
                JobState.Committing, JobState.Recovering, JobState.CancelRequested,
            };
            var interrupted = new List<Job>();
            foreach (var job in _jobs.Values.Where(j => activeStates.Contains(j.State)).ToList())
            {
                var updated = job with
                {
                    State = JobState.Interrupted,
                    Revision = job.Revision + 1,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                };
                _jobs[job.JobId] = updated;
                interrupted.Add(updated);
            }
            return Task.FromResult<IReadOnlyList<Job>>(interrupted);
        }
    }

    // Test helpers -------------------------------------------------------------
    public IReadOnlyList<Attempt> AllAttempts
    {
        get { lock (_gate) { return _attempts.Values.ToList(); } }
    }
}

internal sealed class FakeProbe : IMediaProbe
{
    public Func<SourceReference, CancellationToken, Task<Result<SourceSnapshot>>> Handler { get; set; } =
        (_, _) => throw new InvalidOperationException("FakeProbe.Handler not configured.");

    public Task<Result<SourceSnapshot>> ProbeAsync(SourceReference source, CancellationToken ct) => Handler(source, ct);
}

internal sealed class FakeCapabilityService : ICapabilityService
{
    public CapabilitySnapshot Snapshot { get; set; } = TestData.DefaultCapabilities();

    public Task<CapabilitySnapshot> GetAsync(CapabilityQuery query, CancellationToken ct) => Task.FromResult(Snapshot);

    public Task<CapabilitySnapshot> RefreshAsync(CancellationToken ct) => Task.FromResult(Snapshot);
}

internal sealed class FakePlanner : IExecutionPlanner
{
    public Func<ConversionRequest, SourceSnapshot, CapabilitySnapshot, CancellationToken, Task<Result<ValidatedPlan>>> Handler { get; set; } =
        (_, _, _, _) => throw new InvalidOperationException("FakePlanner.Handler not configured.");

    public Task<Result<ValidatedPlan>> BuildAsync(
        ConversionRequest request, SourceSnapshot source, CapabilitySnapshot capabilities, CancellationToken ct)
        => Handler(request, source, capabilities, ct);
}

internal sealed class FakeRunner : IMediaRunner
{
    public ConcurrentQueue<AttemptContext> Attempts { get; } = new();

    public Func<AttemptContext, IProgress<EngineProgress>, CancellationToken, Task<RunResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null));

    public Task<RunResult> RunAsync(AttemptContext attempt, IProgress<EngineProgress> progress, CancellationToken ct)
    {
        Attempts.Enqueue(attempt);
        return Handler(attempt, progress, ct);
    }
}

internal sealed class FakeVerifier : IOutputVerifier
{
    public Func<VerificationRequest, CancellationToken, Task<VerificationResult>> Handler { get; set; } =
        (r, _) => Task.FromResult(VerificationResult.Ok("ok"));

    public Task<VerificationResult> VerifyAsync(VerificationRequest request, CancellationToken ct) => Handler(request, ct);
}

internal sealed class FakeOutputPathService : IOutputPathService
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "tf-tests", Guid.NewGuid().ToString("N"));
    public List<ReservedOutput> Released { get; } = new();
    public Func<OutputReservationRequest, Result<ReservedOutput>>? OnReserve { get; set; }

    public Task<Result<ReservedOutput>> ReserveAsync(OutputReservationRequest request, CancellationToken ct)
    {
        if (OnReserve is not null)
        {
            return Task.FromResult(OnReserve(request));
        }
        var token = Guid.NewGuid().ToString("N");
        var extension = request.Container.Extension();
        var reserved = new ReservedOutput
        {
            FinalPath = Path.Combine(Root, $"out-{token}{extension}"),
            WorkDirectory = Root,
            CommitToken = token,
            Extension = extension,
        };
        return Task.FromResult(Result<ReservedOutput>.Ok(reserved));
    }

    public void Release(ReservedOutput reserved)
    {
        lock (Released)
        {
            Released.Add(reserved);
        }
    }
}

internal sealed class FakeCommitter : IOutputCommitter, ICommitReconciler
{
    public Func<CommitRequest, CancellationToken, Task<CommitResult>>? OnCommit { get; set; }
    public Func<CommitJournalEntry, CancellationToken, Task<CommitResult>>? OnReconcile { get; set; }
    public int CommitCalls { get; private set; }
    public int ReconcileCalls { get; private set; }

    public Task<CommitResult> CommitAsync(CommitRequest request, CancellationToken ct)
    {
        CommitCalls++;
        if (OnCommit is not null)
        {
            return OnCommit(request, ct);
        }
        return Task.FromResult(new CommitResult
        {
            Success = true,
            CommitToken = Guid.NewGuid().ToString("N"),
            FinalPath = request.FinalPath,
        });
    }

    public Task<CommitResult> ReconcileAsync(CommitJournalEntry entry, CancellationToken ct)
    {
        ReconcileCalls++;
        if (OnReconcile is not null)
        {
            return OnReconcile(entry, ct);
        }
        return Task.FromResult(new CommitResult
        {
            Success = true,
            CommitToken = entry.CommitToken,
            FinalPath = entry.FinalPath,
        });
    }
}

internal sealed class FakeFileSystemService : IFileSystemService
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => true;
    public void CreateDirectory(string path) { }
    public long GetAvailableFreeSpace(string path) => long.MaxValue;
    public void DeleteFile(string path) => Files.Remove(path);
    public string NormalizePath(string path) => path;
    public bool PathsReferToSameFile(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    public Task<FileIdentity?> GetIdentityAsync(string path, CancellationToken ct) => Task.FromResult<FileIdentity?>(null);
    public FileIdentity GetIdentity(string path) => new(path, 0, DateTimeOffset.UnixEpoch, null, null);
    public IReadOnlyList<string> EnumerateFiles(string directory, bool recursive) => Array.Empty<string>();
    public string GetFileNameWithoutExtension(string path) => Path.GetFileNameWithoutExtension(path);
    public string GetExtension(string path) => Path.GetExtension(path);
    public long GetFileLength(string path) => 0;
    public DateTimeOffset GetLastWriteTimeUtc(string path) => DateTimeOffset.UnixEpoch;
    public void MoveNoReplace(string source, string destination) => throw new NotSupportedException();
}

internal static class TestData
{
    public static SourceSnapshot MakeSource(string path = @"C:\media\input.mp4", long size = 10_000)
        => new()
        {
            MediaId = Guid.NewGuid(),
            AbsolutePath = path,
            SizeBytes = size,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            DurationUs = 5_000_000,
            Streams = new[]
            {
                new StreamDescriptor { SourceIndex = 0, Kind = MediaKind.Video, Codec = "h264", Width = 1920, Height = 1080, PixelFormat = "yuv420p" },
                new StreamDescriptor { SourceIndex = 1, Kind = MediaKind.Audio, Codec = "aac", Channels = 2, SampleRate = 48000 },
            },
            Format = new MediaFormat { FormatName = "mp4", DurationUs = 5_000_000, SizeBytes = size },
        };

    public static ConversionRequest MakeRequest(Guid sourceId)
        => new()
        {
            SourceSnapshotId = sourceId,
            Mode = ConversionMode.VideoTranscode,
            Container = ContainerFormat.Mp4,
            Output = new OutputPolicy(),
        };

    public static CapabilitySnapshot DefaultCapabilities()
        => new()
        {
            CapabilityHash = "cap-hash",
            CoreHash = "core-hash",
            Candidates = new[]
            {
                new CandidateCapability { CandidateId = "cpu-x264", Backend = "cpu", EncoderName = "libx264", Available = true },
            },
        };

    public static ValidatedPlan MakePlan(int candidateCount = 1, ContainerFormat container = ContainerFormat.Mp4)
    {
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(i => new ExecutionCandidate
            {
                CandidateId = i == 0 ? "cpu-x264" : $"hw-{i}",
                Backend = i == 0 ? HardwareBackend.Cpu : HardwareBackend.NvidiaNvenc,
                EncoderName = i == 0 ? "libx264" : "h264_nvenc",
                Arguments = Array.Empty<string>(),
                Rationale = i == 0 ? "cpu" : "gpu",
            })
            .ToList();

        return new ValidatedPlan
        {
            PlanId = Guid.NewGuid(),
            RequestHash = "req-hash",
            CoreHash = "core-hash",
            CapabilityHash = "cap-hash",
            CandidatePaths = candidates,
            ExpectedOutput = new ExpectedOutput
            {
                Container = container,
                VideoCodec = VideoCodec.H264,
                AudioCodec = AudioCodec.Aac,
                DurationUs = 5_000_000,
                StreamCount = 2,
                VideoStreamCount = 1,
                AudioStreamCount = 1,
            },
            PlanHash = "plan-hash",
        };
    }
}
