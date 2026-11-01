using TransFlux.App.State;

namespace TransFlux.Application.Tests;

/// <summary>UI review fix round: per-keystroke settings edits coalesce into one save and never drop the last change.</summary>
public sealed class UiDebouncedActionTests
{
    [Fact]
    public async Task RapidTriggersCoalesceIntoSingleRun()
    {
        using var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(150));
        var runs = 0;
        var last = 0;
        for (var i = 1; i <= 3; i++)
        {
            var value = i;
            debounced.Schedule(() => { Interlocked.Increment(ref runs); last = value; return Task.CompletedTask; });
            await Task.Delay(30);
        }
        await Task.Delay(500);
        Assert.Equal(1, runs);
        Assert.Equal(3, last);
    }

    [Fact]
    public async Task FlushAwaitsPendingRun()
    {
        using var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(250));
        var ran = false;
        debounced.Schedule(() => { ran = true; return Task.CompletedTask; });
        await debounced.FlushAsync();
        Assert.True(ran);
    }

    [Fact]
    public async Task DisposeCancelsPendingRun()
    {
        var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(200));
        var ran = false;
        debounced.Schedule(() => { ran = true; return Task.CompletedTask; });
        debounced.Dispose();
        await Task.Delay(400);
        Assert.False(ran);
    }

    [Fact]
    public async Task FailuresInsideTheActionDoNotEscape()
    {
        using var debounced = new DebouncedAction(TimeSpan.FromMilliseconds(10));
        debounced.Schedule(() => throw new InvalidOperationException("boom"));
        await debounced.FlushAsync();
        await Task.Delay(20);
    }
}
