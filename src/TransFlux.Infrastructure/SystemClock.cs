using TransFlux.Application.Contracts;

namespace TransFlux.Infrastructure;

/// <summary>Wall-clock implementation of <see cref="IClock"/> returning UTC time.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
