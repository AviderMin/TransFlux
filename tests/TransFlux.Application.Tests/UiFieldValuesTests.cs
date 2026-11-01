using TransFlux.App.State;

namespace TransFlux.Application.Tests;

/// <summary>UI review fix round: NumberBox coercion keeps "empty means auto" and clamps typed values.</summary>
public sealed class UiFieldValuesTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-5)]
    public void EmptyOrNonPositiveMeansAuto(double value)
        => Assert.Null(FieldValues.IntOrNull(value, 2, 16384));

    [Fact]
    public void TypedValuesAreClampedIntoRange()
    {
        Assert.Equal(2, FieldValues.IntOrNull(1, 2, 16384));
        Assert.Equal(1920, FieldValues.IntOrNull(1920, 2, 16384));
        Assert.Equal(16384, FieldValues.IntOrNull(99999, 2, 16384));
        Assert.Equal(1, FieldValues.IntOrNull(1.9, 1, 8));
    }

    [Fact]
    public void CrfKeepsAutoWhenBlank()
    {
        Assert.Null(FieldValues.CrfOrNull(double.NaN, 51));
        Assert.Equal(0, FieldValues.CrfOrNull(-3, 51));
        Assert.Equal(51, FieldValues.CrfOrNull(99, 51));
        Assert.Equal(63, FieldValues.CrfOrNull(99, 63));
    }

    [Fact]
    public void FrameRateAndSecondsCoerceLikeInts()
    {
        Assert.Null(FieldValues.DoubleOrNull(double.NaN, 1, 240));
        Assert.Null(FieldValues.DoubleOrNull(0, 1, 240));
        Assert.Equal(240d, FieldValues.DoubleOrNull(300, 1, 240));
        Assert.Null(FieldValues.SecondsOrNull(double.NaN));
        Assert.Equal(TimeSpan.Zero, FieldValues.SecondsOrNull(-5));
        Assert.Equal(TimeSpan.FromSeconds(12.5), FieldValues.SecondsOrNull(12.5));
    }
}
