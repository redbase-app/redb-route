namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>
/// The plural-wrapper rule: a parent named as the English plural of its children's name (<c>Incidents</c> /
/// <c>Incident</c>) holds a list. The singular candidates of a plural are the regular forms (<c>-ies</c> to <c>-y</c>,
/// <c>-es</c> and <c>-s</c> left out) and a short list of irregular ones; a name with none of them is never a plural.
/// Names compare as written, case included.
/// </summary>
internal static class Plural
{
    private static readonly Dictionary<string, string> Irregular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["children"] = "child",
        ["people"] = "person",
        ["men"] = "man",
        ["women"] = "woman",
        ["indices"] = "index",
        ["matrices"] = "matrix",
        ["vertices"] = "vertex",
        ["analyses"] = "analysis",
        ["criteria"] = "criterion",
        ["feet"] = "foot",
        ["teeth"] = "tooth",
        ["mice"] = "mouse",
    };

    /// <summary>Whether <paramref name="parent"/> is a plural of <paramref name="child"/>.</summary>
    public static bool IsPair(string parent, string child)
    {
        if (child.Length == 0)
            return false;
        foreach (var singular in Singulars(parent))
            if (string.Equals(singular, child, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>The singular forms <paramref name="plural"/> may stand for; none for a name that is not a plural.</summary>
    public static IEnumerable<string> Singulars(string plural)
    {
        if (Irregular.TryGetValue(plural, out var irregular))
            yield return MatchCase(plural, irregular);
        if (plural.Length > 3 && plural.EndsWith("ies", StringComparison.Ordinal))
            yield return plural[..^3] + "y";
        if (plural.Length > 2 && plural.EndsWith("es", StringComparison.Ordinal))
            yield return plural[..^2];
        if (plural.Length > 1 && plural.EndsWith('s') && !plural.EndsWith("ss", StringComparison.Ordinal))
            yield return plural[..^1];
    }

    /// <summary>
    /// The one singular JSON to XML writes for the plural <paramref name="plural"/>, or <c>null</c> when the name is not
    /// a plural. Irregular first, then <c>-ies</c> to <c>-y</c> (<c>Categories</c>), <c>-es</c> after <c>ss</c>, <c>us</c>,
    /// <c>x</c>, <c>z</c>, <c>ch</c>, <c>sh</c> (<c>Addresses</c>, <c>Statuses</c>, <c>Boxes</c>), else <c>-s</c>
    /// (<c>Cases</c>, <c>Incidents</c>). A choice has to be made where English is ambiguous: <c>Movies</c> gives
    /// <c>Movy</c>; such a name is written with <c>arrays=</c> and an explicit wrapper instead.
    /// </summary>
    public static string? SingularFor(string plural)
    {
        if (Irregular.TryGetValue(plural, out var irregular))
            return MatchCase(plural, irregular);
        if (plural.Length > 3 && plural.EndsWith("ies", StringComparison.Ordinal))
            return plural[..^3] + "y";
        foreach (var ending in (string[])["sses", "uses", "xes", "zes", "ches", "shes"])
            if (plural.Length > ending.Length && plural.EndsWith(ending, StringComparison.Ordinal))
                return plural[..^2];
        if (plural.Length > 1 && plural.EndsWith('s') && !plural.EndsWith("ss", StringComparison.Ordinal))
            return plural[..^1];
        return null;
    }

    /// <summary>The irregular singular with the first letter cased as the plural's (<c>Children</c> gives <c>Child</c>).</summary>
    private static string MatchCase(string plural, string singular)
        => char.IsUpper(plural[0]) ? char.ToUpperInvariant(singular[0]) + singular[1..] : singular;
}
