using TransFlux.Application.Contracts;
using TransFlux.Application.Services;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

/// <summary>Wires a real JobScheduler to deterministic in-memory fakes.</summary>
internal sealed class SchedulerHarness : IAsyncDisposable
{
    public InMemoryJobStore Store { get; } = new();
    public FakeClock Clock { get; } = new(DateTimeOffset.UnixEpoch.AddDays(1));
    public FakeProbe Probe { get; } = new();
    public FakeCapabilityService Caps { get; } = new();
    public FakePlanner Planner { get; } = new();
    public FakeRunner Runner { get; } = new();
    public FakeVerifier Verifier { get; } = new();
    public FakeOutputPathService OutputPaths { get; } = new();
    public FakeCommitter Committer { get; } = new();
    public FakeFileSystemService Fs { get; } = new();
    public SchedulerOptions Options { get; }
    public JobScheduler Scheduler { get; }
    public SourceSnapshot Source { get; }

    public SchedulerHarness(Action<SchedulerOptions>? configure = null)
    {
        Options = new SchedulerOptions
        {
            MaxConcurrentTranscodes = 1,
            AllowAutomaticFallback = true,
            MaxAutoFallbackAttemptsPerJob = 1,
            ProgressThrottle = TimeSpan.FromMilliseconds(250),
        };
        configure?.Invoke(Options);

        Source = TestData.MakeSource();
        Probe.Handler = (_, _) => Task.FromResult(Result<SourceSnapshot>.Ok(Source));
        Planner.Handler = (_, _, _, _) => Task.FromResult(Result<ValidatedPlan>.Ok(TestData.MakePlan(1)));

        Scheduler = new JobScheduler(Store, Planner, Probe, Caps, Runner, Verifier, OutputPaths, Committer, Fs, Clock, Options);
    }

    public Task<Guid> EnqueueAsync(CancellationToken ct = default)
        => Scheduler.EnqueueAsync(new EnqueueRequest
        {
            Request = TestData.MakeRequest(Source.MediaId),
            Source = Source,
        }, ct);

    public async Task<Job> WaitForStateAsync(Guid id, JobState state, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            var job = await Store.GetJobAsync(id, CancellationToken.None);
            if (job is not null && job.State == state)
            {
                return job;
            }
            await Task.Delay(10);
        }
        var last = await Store.GetJobAsync(id, CancellationToken.None);
        throw new TimeoutException($"Job {id} did not reach {state}; last state={last?.State.ToString() ?? "null"}.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    public async ValueTask DisposeAsync() => await Scheduler.DisposeAsync();
}