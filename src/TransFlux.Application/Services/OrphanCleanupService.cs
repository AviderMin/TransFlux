using TransFlux.Application.Contracts;
using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Services;

/// <summary>
/// One-shot startup sweep for staging artifacts left behind by terminal jobs (A05). A job is
/// eligible only when it is terminal, older than a conservative age, and has no open commit
/// journal; NeedsAttention and active jobs keep their staging as user/recovery evidence.
/// Cleanup is strictly limited to the validated .transflux-work attempt directory and the
/// reservation lock derived from the attempt's final path — user output files are never touched.
/// Every item is best-effort: cleanup must never prevent the application from starting.
/// </summary>
public static class OrphanCleanupService
{
    public static readonly TimeSpan DefaultMinimumAge = TimeSpan.FromHours(24);

    private static readonly IReadOnlyCollection<JobState> TerminalStates = new[]
    {
        JobState.Succeeded, JobState.Failed, JobState.Canceled, JobState.Interrupted,
    };

    /// <summary>Runs the sweep with the default 24h safety margin.</summary>
    public static Task RunAsync(IJobStore store, IOutputPathService outputPaths, IClock clock, CancellationToken ct)
        => RunAsync(store, outputPaths, clock, DefaultMinimumAge, ct);

    /// <summary>Runs the sweep. Never throws for storage/filesystem failures; cancellation only
    /// stops the pass early.</summary>
    public static async Task RunAsync(
        IJobStore store, IOutputPathService outputPaths, IClock clock, TimeSpan minimumAge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(outputPaths);
        ArgumentNullException.ThrowIfNull(clock);
        if (minimumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAge));
        }

        try
        {
            // A journal means recovery may still need the staging file; skip such jobs entirely.
            var jobsWithOpenJournal = (await store.GetOpenJournalsAsync(ct).ConfigureAwait(false))
                .Select(j => j.JobId)
                .ToHashSet();

            var cutoff = clock.UtcNow - minimumAge;
            foreach (var job in await store.GetJobsAsync(TerminalStates, ct).ConfigureAwait(false))
            {
                if (!JobStateMachine.IsTerminal(job.State)
                    || job.UpdatedUtc >= cutoff
                    || jobsWithOpenJournal.Contains(job.JobId))
                {
                    continue;
                }

                try
                {
                    await CleanupJobAsync(store, outputPaths, job.JobId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // Best effort per job: one unreadable attempt must not stop the sweep.
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Startup shutdown: stop quietly.
        }
        catch
        {
            // The sweep is maintenance only; it must never break application startup.
        }
    }

    private static async Task CleanupJobAsync(
        IJobStore store, IOutputPathService outputPaths, Guid jobId, CancellationToken ct)
    {
        var attempts = await store.GetAttemptsForJobAsync(jobId, ct).ConfigureAwait(false);
        foreach (var attempt in attempts)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var workDirectory = OutputStagingLayout.GetWorkDirectory(attempt.TempPath);
                var reservation = new ReservedOutput
                {
                    FinalPath = attempt.ReservedFinalPath,
                    WorkDirectory = workDirectory ?? string.Empty,
                    CommitToken = workDirectory is null ? string.Empty : Path.GetFileName(workDirectory),
                    Extension = Path.GetExtension(attempt.TempPath) ?? string.Empty,
                };

                // The reservation lock path derives only from the final output path; deleting a
                // stale lock is safe and never touches the output file itself. Keep the two
                // operations independent so one failing cleanup step cannot block the other.
                if (!string.IsNullOrWhiteSpace(attempt.ReservedFinalPath))
                {
                    try { outputPaths.ReleaseReservation(reservation); } catch { }
                }
                if (workDirectory is not null)
                {
                    try { outputPaths.DeleteWorkDirectory(reservation); } catch { }
                }
            }
            catch
            {
                // Best effort per attempt.
            }
        }
    }
}
