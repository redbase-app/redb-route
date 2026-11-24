using System.Globalization;

namespace redb.Route.Core;

/// <summary>
/// A decimal from a binary floating-point value the way an expression, a JSONPath read or a converted body needs it: the
/// decimal of the shortest text that reads back as the same double ("R"), so 19.95 stays 19.95 and <c>round(2.567, 2)</c>
/// stays 2.57 on every runtime. The same technique as redb.Core's <c>RedbDecimal</c>, for the packages that do not
/// reference redb.Core.
/// </summary>
/// <remarks>
/// The explicit conversion <c>(decimal)double</c>, which <see cref="Convert.ToDecimal(double)"/> and
/// <see cref="Convert.ChangeType(object, Type)"/> use, rounds to 15 significant digits up to .NET 10
/// (12345678901234.56 became 12345678901234.6) and gives the exact binary value from .NET 11 on
/// (19.95 became 19.949999999999999289457264240). Neither is the number that was written.
/// </remarks>
public static class RouteDecimal
{
    /// <summary>The decimal the double stands for. NaN, an infinity or a value outside the decimal range throws
    /// <see cref="OverflowException"/>, as the explicit conversion does.</summary>
    public static decimal FromDouble(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new OverflowException($"{value.ToString(CultureInfo.InvariantCulture)} has no decimal value.");

        // The shortest text of a double is at most 24 characters ("-1.7976931348623157E+308"): formatted on the stack,
        // parsed from the span, no string per value.
        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out var written, "R", CultureInfo.InvariantCulture);
        return decimal.Parse(text[..written], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>The decimal the float stands for (the shortest text of the float itself, not of its widening to double).</summary>
    public static decimal FromSingle(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            throw new OverflowException($"{value.ToString(CultureInfo.InvariantCulture)} has no decimal value.");

        Span<char> text = stackalloc char[32];
        value.TryFormat(text, out var written, "R", CultureInfo.InvariantCulture);
        return decimal.Parse(text[..written], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <see cref="Convert.ToDecimal(object, IFormatProvider)"/> with a double or a float read through
    /// <see cref="FromDouble"/> / <see cref="FromSingle"/>. Anything else converts as before.
    /// </summary>
    public static decimal From(object value) => value switch
    {
        decimal d => d,
        double dbl => FromDouble(dbl),
        float f => FromSingle(f),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/>, except that a double or a float asked for as a
    /// decimal goes through <see cref="FromDouble"/> / <see cref="FromSingle"/>. Every other pair converts exactly as
    /// <see cref="Convert.ChangeType(object, Type, IFormatProvider)"/> does with the same provider (null: the current
    /// culture, as <see cref="Convert.ChangeType(object, Type)"/>).
    /// </summary>
    public static object? ChangeType(object? value, Type targetType, IFormatProvider? provider = null) =>
        targetType == typeof(decimal) && value is double or float
            ? From(value)
            : Convert.ChangeType(value, targetType, provider);
}
