using Xunit;

// Real ffmpeg processes are CPU-heavy; running encode tests sequentially keeps timing,
// cancellation and file-cleanup assertions deterministic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
