using TransFlux.Domain.Common;
using TransFlux.Domain.Plans;

namespace TransFlux.Domain.Tests;

public class TrimRangeTests
{
    [Fact]
    public void Create_ValidRange_ComputesDuration()
    {
        var t = TrimRange.Create(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(1), t.Start);
        Assert.Equal(TimeSpan.FromSeconds(4), t.End);
        Assert.Equal(TimeSpan.FromSeconds(3), t.Duration);
    }

    [Fact]
    public void Create_EndEqualToDuration_IsAllowed()
    {
        var t = TrimRange.Create(TimeSpan.Zero, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), t.Duration);
    }

    [Fact]
    public void Create_NegativeStart_Throws()
    {
        Assert.Throws<DomainException>(() =>
            TrimRange.Create(TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(2), null));
    }

    [Fact]
    public void Create_EndNotAfterStart_Throws()
    {
        Assert.Throws<DomainException>(() =>
            TrimRange.Create(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), null));
    }

    [Fact]
    public void Create_EndBeforeStart_Throws()
    {
        Assert.Throws<DomainException>(() =>
            TrimRange.Create(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), null));
    }

    [Fact]
    public void Create_EndBeyondKnownDuration_Throws()
    {
        Assert.Throws<DomainException>(() =>
            TrimRange.Create(TimeSpan.Zero, TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Create_UnknownDuration_AllowsOpenEnd()
    {
        var t = TrimRange.Create(TimeSpan.Zero, TimeSpan.FromHours(1), null);
        Assert.Equal(TimeSpan.FromHours(1), t.Duration);
    }
}
