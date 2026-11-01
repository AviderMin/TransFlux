namespace TransFlux.App.State;

/// <summary>
/// Numeric coercion for optional parameter fields: empty means auto (null) and typed
/// values are clamped into their documented ranges (UI review fix round).
/// </summary>
public static class FieldValues
{
    /// <summary>Whole number in [min, max]; empty, NaN or non-positive means auto.</summary>
    public static int? IntOrNull(double value, int min, int max)
        => value > 0 && !double.IsNaN(value) ? (int)Math.Clamp(value, min, max) : null;

    /// <summary>CRF in [0, max]; NaN means auto (left empty).</summary>
    public static int? CrfOrNull(double value, int max)
        => double.IsNaN(value) ? null : (int)Math.Clamp(value, 0, max);

    /// <summary>Fractional value in [min, max]; empty, NaN or non-positive means auto.</summary>
    public static double? DoubleOrNull(double value, double min, double max)
        => value > 0 && !double.IsNaN(value) ? Math.Clamp(value, min, max) : null;

    /// <summary>Seconds from zero upward; NaN means no trimming.</summary>
    public static TimeSpan? SecondsOrNull(double value)
        => double.IsNaN(value) ? null : TimeSpan.FromSeconds(Math.Max(0, value));
}
