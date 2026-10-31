namespace TransFlux.Domain.Common;

/// <summary>A reduced-free rational number used for frame rates, SAR and DAR.
/// Denominator is never zero for valid values; null represents Unknown.</summary>
public readonly record struct Rational(int Numerator, int Denominator)
{
    public static readonly Rational Zero = new(0, 1);

    public static Rational? Create(int? numerator, int? denominator)
        => numerator is int n && denominator is int d && d != 0 ? new Rational(n, d) : null;

    public double ToDouble() => Denominator == 0 ? 0d : (double)Numerator / Denominator;

    public override string ToString() => Denominator == 0 ? "0/0" : $"{Numerator}/{Denominator}";
}

/// <summary>Thrown when a domain invariant is violated.</summary>
public sealed class DomainException(string message) : Exception(message);

public static class DomainGuard
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new DomainException(message);
    }

    public static void NotNull(object? value, string name)
    {
        if (value is null) throw new DomainException($"{name} must not be null.");
    }
}
