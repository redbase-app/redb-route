using System.Globalization;

namespace redb.Route.Expressions;

/// <summary>Partial class ExpressionResolver — how a <c>${...}</c> hole becomes text.</summary>
public static partial class ExpressionResolver
{
    /// <summary>
    /// Renders one substituted value of a <c>${...}</c> template. Numbers and dates are written
    /// culture-invariant (dates as ISO 8601), so a route renders the same text on every machine;
    /// locale-specific formatting is an explicit choice (<c>format(x, 'N2', 'ru-RU')</c>), not the
    /// server's accident. Strings and booleans are unchanged.
    /// </summary>
    internal static string TemplateText(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b.ToString(),
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
