using System.Globalization;

namespace redb.Route.Expressions;

/// <summary>
/// The culture lookup behind the language's <c>format(value, pattern[, culture])</c> function, shared
/// by the compiled and the interpreted branch of the engine so both answer alike.
/// </summary>
internal static class ExpressionFormatting
{
    /// <summary>
    /// The culture named by <paramref name="name"/>; no name means invariant. An unknown name is an
    /// error rather than a silent fall back to invariant: a route that formats money for the wrong
    /// locale must say so at once, not ship plausible-looking text.
    /// </summary>
    public static CultureInfo Culture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CultureInfo.InvariantCulture;

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException ex)
        {
            throw new ArgumentException(
                $"format(): '{name}' is not a known culture name (use an IETF tag such as 'ru-RU', or omit the argument for the invariant culture).", nameof(name), ex);
        }
    }
}
