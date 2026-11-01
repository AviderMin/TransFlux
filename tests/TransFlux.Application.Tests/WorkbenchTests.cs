using TransFlux.App.State;
using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;
using TransFlux.Domain.Media;
using TransFlux.Domain.Plans;

namespace TransFlux.Application.Tests;

public sealed class WorkbenchTests
{
    private sealed class MemoryWorkspace : IWorkspacePersistence
    {
        public IReadOnlyList<WorkspaceEntry>? Entries { get; set; }
        public Func<IReadOnlyList<WorkspaceEntry>, Task>? Saving { get; set; }
        public Task<IReadOnlyList<WorkspaceEntry>?> LoadAsync(CancellationToken ct) => Task.FromResult(Entries);
        public async Task SaveAsync(IReadOnlyList<WorkspaceEntry> entries, CancellationToken ct)
        { if (Saving is not null) await Saving(entries); Entries = entries.ToArray(); }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tf-workbench-" + Guid.NewGuid().ToString("N"));
        public string[] Paths { get; }
        public Files(int count = 2)
        {
            Directory.CreateDirectory(_root);
            Paths = Enumerable.Range(0, count).Select(i => Path.Combine(_root, $"input-{i}.mp4")).ToArray();
            foreach (var path in Paths) File.WriteAllText(path, "test source");
        }
        public void Dispose() => Directory.Delete(_root, true);
    }
    private static WorkbenchStore Store(SchedulerHarness h, MemoryWorkspace? saved = null, int probes = 2)
    {
        h.Probe.Handler = (source, _) => Task.FromResult(Result<SourceSnapshot>.Ok(h.Source with
            { MediaId = source.MediaId, ColorClassification = ColorClassification.SdrBt709 }));
        return new(h.Scheduler, h.Probe, saved ?? new MemoryWorkspace(), a => a(), probes);
    }
    private static async Task Loaded(WorkbenchStore store)
    { store.Start(); await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Loaded); }
    private static Task Ready(WorkbenchStore store, int count)
        => SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items.Count(i => i.Draft.IsReady) == count);

    [Fact]
    public async Task ImportSelectsNewBatch_AndBatchEditOnlyChangesTouchedField()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(); using var store = Store(h);
        await Loaded(store); await store.ImportAsync([files.Paths[0]], ConversionOptions.Default with { OutputDirectory = "first" });
        await store.ImportAsync([files.Paths[1]], ConversionOptions.Default with { OutputDirectory = "second", Quality = QualityTier.HigherQuality });
        await Ready(store, 2);
        var before = store.GetSnapshot(); Assert.Single(before.Selected);
        store.SelectAll(true);
        await store.EditAsync(store.GetSnapshot().Selected, o => o with { Quality = QualityTier.SmallerSize });
        var after = store.GetSnapshot();
        Assert.All(after.Items, i => Assert.Equal(QualityTier.SmallerSize, i.Draft.Options.Quality));
        Assert.Equal(new[] { "first", "second" }, after.Items.Select(i => i.Draft.Options.OutputDirectory));
    }

    [Fact]
    public async Task ExecutionParametersAreFrozen_ActiveRemovalIsRejected_AndCancelStopsRunner()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        h.Runner.Handler = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return RunResult.CanceledResult(TimeSpan.Zero); };
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var id = store.GetSnapshot().Items[0].Id;
        h.Scheduler.Start(); await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Running);
        await store.EditAsync([id], o => o with { Quality = QualityTier.HigherQuality });
        await store.RemoveAsync([id]);
        Assert.Equal(QualityTier.Balanced, Assert.Single(store.GetSnapshot().Items).Draft.Options.Quality);
        await store.CancelAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Canceled);
        Assert.Equal(QualityTier.Balanced, Assert.Single(h.Runner.Attempts).Request.Quality);
    }

    [Fact]
    public async Task QueuedEditCancelsOldRequest_AndReturnsSameCardToEditableState()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var id = store.GetSnapshot().Items[0].Id;
        await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Queued);
        var oldJob = store.GetSnapshot().Items[0].Draft.JobId!.Value;
        await store.EditQueuedAsync(id);
        Assert.True(Assert.Single(store.GetSnapshot().Items).CanEdit);
        Assert.Equal(id, store.GetSnapshot().Items[0].Id);
        Assert.Equal(JobState.Canceled, (await h.Store.GetJobAsync(oldJob, default))!.State);
        await store.EditAsync([id], o => o with { Quality = QualityTier.HigherQuality });
        await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.Job.JobId != oldJob);
        Assert.Equal(QualityTier.HigherQuality, store.GetSnapshot().Items[0].Draft.Options.Quality);
    }

    [Fact]
    public async Task WorkspaceRestoresVisibleJobsAndUnstartedDrafts_WithoutMergingSameSource()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); var saved = new MemoryWorkspace();
        Guid first;
        using (var store = Store(h, saved))
        {
            await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
            first = store.GetSnapshot().Items[0].Id;
            await store.StartAsync([first]);
            await store.CopyAsync(first); await Ready(store, 2); await store.PersistAsync();
        }
        await h.Scheduler.PauseQueueAsync(default);
        using var restored = Store(h, saved); await Loaded(restored);
        await SchedulerHarness.WaitUntilAsync(() => restored.GetSnapshot().Items.Count == 2 && restored.GetSnapshot().Items.Any(i => i.CanStart));
        Assert.True(restored.GetSnapshot().Paused);
        Assert.Equal(2, restored.GetSnapshot().Items.Select(i => i.Id).Distinct().Count());
        Assert.NotNull(restored.GetSnapshot().Items.Single(i => i.Id == first).Execution);
    }

    [Fact]
    public async Task ImportIsBounded_RemovingAProbingCardCancelsItsProbe_AndUndoReprobes()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(5); using var store = Store(h, probes: 2);
        var entered = 0; var running = 0; var peak = 0; var canceled = 0;
        h.Probe.Handler = async (source, ct) =>
        {
            Interlocked.Increment(ref entered); var active = Interlocked.Increment(ref running);
            Interlocked.Exchange(ref peak, Math.Max(Volatile.Read(ref peak), active));
            try { await Task.Delay(Timeout.Infinite, ct); return Result<SourceSnapshot>.Ok(h.Source); }
            catch (OperationCanceledException) { Interlocked.Increment(ref canceled); throw; }
            finally { Interlocked.Decrement(ref running); }
        };
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default);
        await SchedulerHarness.WaitUntilAsync(() => Volatile.Read(ref entered) == 2);
        await store.RemoveAsync(store.GetSnapshot().Items.Select(i => i.Id).ToArray());
        await SchedulerHarness.WaitUntilAsync(() => Volatile.Read(ref running) == 0);
        Assert.InRange(peak, 1, 2); Assert.InRange(canceled, 1, files.Paths.Length); Assert.Empty(store.GetSnapshot().Items);
        h.Probe.Handler = (_, _) => Task.FromResult(Result<SourceSnapshot>.Ok(h.Source));
        await store.UndoAsync(); await Ready(store, 5);
    }

    [Fact]
    public async Task RemoveUndoBannerReportsCountAndCanBeDismissed()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(2); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 2);
        await store.RemoveAsync(store.GetSnapshot().Items.Select(i => i.Id).ToArray());
        var snapshot = store.GetSnapshot();
        Assert.True(snapshot.CanUndo);
        Assert.Equal(2, snapshot.UndoCount);
        store.DismissUndo();
        snapshot = store.GetSnapshot();
        Assert.False(snapshot.CanUndo);
        Assert.Equal(0, snapshot.UndoCount);
        await store.UndoAsync();
        Assert.Empty(store.GetSnapshot().Items);
    }

    [Fact]
    public async Task RetryPreservesTheVisibleCard_AndCopyDoesNotOverwriteOriginalOptions()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var id = store.GetSnapshot().Items[0].Id;
        await store.StartAsync([id]); await store.CancelAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Canceled);
        await store.RetryAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Queued);
        Assert.Equal(id, Assert.Single(store.GetSnapshot().Items).Id);
        var copy = (await store.CopyAsync(id))!.Value; await Ready(store, 2);
        await store.EditAsync([copy], o => o with { Quality = QualityTier.HigherQuality });
        Assert.Equal(QualityTier.Balanced, store.GetSnapshot().Items.Single(i => i.Id == id).Draft.Options.Quality);
        Assert.Equal(QualityTier.HigherQuality, store.GetSnapshot().Items.Single(i => i.Id == copy).Draft.Options.Quality);
    }

    [Fact]
    public async Task StartWhileQueuePausedKeepsUserPauseAndLeavesJobWaiting()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        await h.Scheduler.PauseQueueAsync(default); h.Scheduler.Start();
        var id = store.GetSnapshot().Items[0].Id;
        await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Queued);
        await Task.Delay(100);
        Assert.True(store.GetSnapshot().Paused);
        Assert.Empty(h.Runner.Attempts);
    }

    [Fact]
    public async Task RetryWhileQueuePausedKeepsUserPauseAndLeavesRetryWaiting()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var id = store.GetSnapshot().Items[0].Id;
        await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Queued);
        var original = store.GetSnapshot().Items[0].Draft.JobId!.Value;
        await store.CancelAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Canceled);
        await h.Scheduler.PauseQueueAsync(default);
        await store.RetryAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution is { Job.JobId: var retryId, State: JobState.Queued } && retryId != original);
        await Task.Delay(100);
        Assert.True(store.GetSnapshot().Paused);
        Assert.Empty(h.Runner.Attempts);
        Assert.Single(store.GetSnapshot().Items);
    }

    [Fact]
    public async Task RetryWhileQueueRunningLeavesQueueRunningAndCompletes()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); using var store = Store(h);
        var calls = 0;
        h.Runner.Handler = async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1) await Task.Delay(Timeout.Infinite, ct);
            return RunResult.Ok(0, TimeSpan.FromSeconds(1), 1000, null);
        };
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var id = store.GetSnapshot().Items[0].Id;
        h.Scheduler.Start();
        await store.StartAsync([id]);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Running);
        await store.CancelAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Canceled);
        await store.RetryAsync(id);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items.Any(i => i.Execution?.State == JobState.Succeeded));
        Assert.False(store.GetSnapshot().Paused);
        Assert.Single(store.GetSnapshot().Items);
    }

    [Fact]
    public async Task NeedsAttentionCardRequiresReviewBeforeAnyRetryAndCanBeRemoved()
    {
        await using var h = new SchedulerHarness(); var saved = new MemoryWorkspace();
        var id = await h.EnqueueAsync(); var job = (await h.Store.GetJobAsync(id, default))!;
        await h.Store.UpdateJobAsync(job with { State = JobState.NeedsAttention, Revision = job.Revision + 1 }, job.Revision, default);
        using var store = Store(h, saved); await Loaded(store);
        var card = Assert.Single(store.GetSnapshot().Items);
        Assert.True(card.CanRemove);
        Assert.False(card.Execution!.CanRetry);
        await store.RemoveAsync([card.Id]);
        Assert.Empty(store.GetSnapshot().Items);
        await store.UndoAsync();
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items.Count == 1);
        await store.RetryAsync(card.Id);
        var unchanged = Assert.Single(store.GetSnapshot().Items);
        Assert.Equal(JobState.NeedsAttention, unchanged.Execution!.State);
        Assert.Equal(card.Id, unchanged.Id);
    }

    [Fact]
    public async Task NewlyEnqueuedJobWaitsUntilItsCardBindingIsSaved()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(1); var saved = new MemoryWorkspace();
        using var store = Store(h, saved); await Loaded(store);
        await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 1);
        var writing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        saved.Saving = async entries => { if (entries.Any(e => e.JobId is not null)) { writing.TrySetResult(); await release.Task; } };
        h.Scheduler.Start();
        var start = store.StartAsync(store.GetSnapshot().Selected);
        await writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Runner.Attempts);
        Assert.Equal(JobState.Queued, Assert.Single(await h.Store.GetJobsAsync(null, default)).State);
        release.SetResult(); await start;
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items[0].Execution?.State == JobState.Succeeded);
        Assert.False(store.GetSnapshot().Paused);
    }

    [Fact]
    public async Task RemovedInterruptedCardDoesNotReappearAfterRestart()
    {
        await using var h = new SchedulerHarness(); var saved = new MemoryWorkspace();
        var id = await h.EnqueueAsync(); var job = (await h.Store.GetJobAsync(id, default))!;
        await h.Store.UpdateJobAsync(job with { State = JobState.Interrupted, Revision = job.Revision + 1 }, job.Revision, default);
        using (var first = Store(h, saved))
        {
            await Loaded(first); var card = Assert.Single(first.GetSnapshot().Items);
            await first.RemoveAsync([card.Id]); Assert.Empty(first.GetSnapshot().Items);
        }
        using var second = Store(h, saved); await Loaded(second);
        Assert.Empty(second.GetSnapshot().Items);
        Assert.NotNull(await h.Store.GetJobAsync(id, default));
    }

    [Fact]
    public async Task QueuedCardsMoveWithoutChangingCardIdentityOrRunningJobs()
    {
        await using var h = new SchedulerHarness(); using var files = new Files(2); using var store = Store(h);
        await Loaded(store); await store.ImportAsync(files.Paths, ConversionOptions.Default); await Ready(store, 2);
        var ids = store.GetSnapshot().Items.Select(i => i.Id).ToArray();
        await store.StartAsync(ids);
        await SchedulerHarness.WaitUntilAsync(() => store.GetSnapshot().Items.All(i => i.Execution?.State == JobState.Queued));
        var firstJob = store.GetSnapshot().Items[0].Execution!.Job.JobId;
        var secondJob = store.GetSnapshot().Items[1].Execution!.Job.JobId;

        await store.MoveQueuedAsync(ids[1], -1);

        var snapshot = store.GetSnapshot();
        Assert.Equal(new[] { ids[1], ids[0] }, snapshot.Items.Select(i => i.Id));
        Assert.Equal(secondJob, snapshot.Items[0].Execution!.Job.JobId);
        var queued = await h.Store.GetJobsAsync(new[] { JobState.Queued }, default);
        Assert.Equal(new[] { secondJob, firstJob }, queued.Select(j => j.JobId));
    }

    [Fact]
    public void SourceStreamLinesSeparatePresentCategoriesAndOmitMissingOnes()
    {
        var source = TestData.MakeSource();
        var lines = Fmt.SourceStreamLines(source);
        Assert.Equal(2, lines.Count);
        Assert.StartsWith("视频流：", lines[0]);
        Assert.StartsWith("音频流：", lines[1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("字幕流："));

        var withSubtitle = source with
        {
            Streams = [.. source.Streams, new StreamDescriptor
            {
                SourceIndex = 2, Kind = MediaKind.Subtitle, Codec = "subrip",
            }],
        };
        var withSubtitleLines = Fmt.SourceStreamLines(withSubtitle);
        Assert.Equal(3, withSubtitleLines.Count);
        Assert.StartsWith("字幕流：SRT", withSubtitleLines[2]);
    }

    [Fact]
    public void ApplyingPresetRetainsTrimTracksAndOutputLocation()
    {
        var source = ConversionOptions.Default with
        {
            TrimStart = TimeSpan.FromSeconds(2), TrimEnd = TimeSpan.FromSeconds(8),
            SelectedStreams = [1, 3], OutputDirectory = "output", OutputFileName = "name",
        };
        var template = (ConversionOptions.Default with { Quality = QualityTier.HigherQuality, CrfOverride = 21 }).ToTemplate();
        var applied = source.ApplyPresetTemplate(template);
        Assert.Equal(QualityTier.HigherQuality, applied.Quality);
        Assert.Equal(21, applied.CrfOverride);
        Assert.Equal(source.TrimStart, applied.TrimStart);
        Assert.Equal(source.TrimEnd, applied.TrimEnd);
        Assert.Equal(source.SelectedStreams, applied.SelectedStreams);
        Assert.Equal("output", applied.OutputDirectory);
        Assert.Equal("name", applied.OutputFileName);
    }

    [Fact]
    public void DiagnosticReportRedactsAbsolutePathsAndFileNames()
    {
        var item = new WorkbenchItem(new MediaDraft
        {
            MediaId = Guid.NewGuid(),
            Path = @"C:\Users\alice\Private Clips\secret-movie.mp4",
            Status = DraftStatus.Ready,
        }, null);

        var report = DiagnosticReport.Build(item, DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain("alice", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-movie.mp4", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[path]", report);
    }

    [Fact]
    public void InvalidTrimBlocksStart_AndAdvancedPresetRoundTripsWithoutSourceSpecificData()
    {
        var source = TestData.MakeSource();
        var options = ConversionOptions.Default with { CrfOverride = 18, AudioChannels = 1, AudioSampleRate = 44100,
            TargetWidth = 640, TargetHeight = 360, TrimStart = TimeSpan.FromSeconds(5), OutputDirectory = "private", OutputFileName = "private" };
        var item = new WorkbenchItem(new MediaDraft { MediaId = source.MediaId, Path = source.AbsolutePath,
            Status = DraftStatus.Ready, Snapshot = source, Options = options }, null);
        Assert.False(item.CanStart);
        var preset = ConversionOptions.FromTemplate(options.ToTemplate());
        Assert.Equal(18, preset.CrfOverride); Assert.Equal(44100, preset.AudioSampleRate);
        Assert.Null(preset.OutputDirectory); Assert.Null(preset.OutputFileName); Assert.Null(preset.TrimStart);
    }
}
