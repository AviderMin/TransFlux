using TransFlux.Domain.Jobs;

namespace TransFlux.Application.Scheduling;

/// <summary>
/// FIFO dispatch order with an aging boost. Ordinary jobs are ordered by their frozen
/// enqueue order; a job that has waited for a multiple of <c>agingQuantum</c> has its effective
/// key reduced by one per quantum, so a job skipped for resource reasons eventually overtakes
/// newer work instead of starving. Pure FIFO falls out when all waits are below one quantum.
/// </summary>
internal static class QueueOrdering
{
    public static readonly TimeSpan DefaultAgingQuantum = TimeSpan.FromSeconds(30);

    public static IReadOnlyList<Job> Order(IEnumerable<Job> jobs, DateTimeOffset now, TimeSpan agingQuantum)
    {
        var quantum = agingQuantum <= TimeSpan.Zero ? DefaultAgingQuantum : agingQuantum;
        return jobs
            .Select(job => (job, key: AgingKey(job, now, quantum)))
            .OrderBy(x => x.key)
            .ThenBy(x => x.job.EnqueueOrder)
            .ThenBy(x => x.job.JobId)
            .Select(x => x.job)
            .ToList();
    }

    public static long AgingKey(Job job, DateTimeOffset now, TimeSpan agingQuantum)
    {
        var quantum = agingQuantum <= TimeSpan.Zero ? DefaultAgingQuantum : agingQuantum;
        var waited = now - job.CreatedUtc;
        if (waited < TimeSpan.Zero)
        {
            waited = TimeSpan.Zero;
        }
        var boost = (long)(waited.TotalSeconds / quantum.TotalSeconds);
        return job.EnqueueOrder - boost;
    }
}
