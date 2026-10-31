using TransFlux.Domain.Common;

namespace TransFlux.Domain.Tests;

public class RationalTests
{
    [Fact]
    public void Zero_Is_0_over_1()
    {
        Assert.Equal(0, Rational.Zero.Numerator);
        Assert.Equal(1, Rational.Zero.Denominator);
        Assert.Equal(0d, Rational.Zero.ToDouble());
    }

    [Fact]
    public void Create_ValidParts_ReturnsRational()
    {
        var r = Rational.Create(24, 1);
        Assert.NotNull(r);
        Assert.Equal(new Rational(24, 1), r!.Value);
    }

    [Fact]
    public void Create_ZeroDenominator_ReturnsNull()
    {
        Assert.Null(Rational.Create(1, 0));
    }

    [Fact]
    public void Create_NullParts_ReturnNull()
    {
        Assert.Null(Rational.Create(null, 1));
        Assert.Null(Rational.Create(1, null));
        Assert.Null(Rational.Create(null, null));
    }

    [Fact]
    public void ToDouble_ComputesFrameRate()
    {
        var r = new Rational(30000, 1001);
        Assert.Equal(29.97002997, r.ToDouble(), 4);
    }

    [Fact]
    public void ToDouble_ZeroDenominator_IsZero()
    {
        Assert.Equal(0d, new Rational(1, 0).ToDouble());
    }

    [Fact]
    public void ToString_FormatsNumeratorOverDenominator()
    {
        Assert.Equal("16/9", new Rational(16, 9).ToString());
        Assert.Equal("0/1", Rational.Zero.ToString());
        Assert.Equal("0/0", new Rational(1, 0).ToString());
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(new Rational(1, 2), new Rational(1, 2));
        Assert.NotEqual(new Rational(1, 2), new Rational(1, 3));
    }
}
