namespace redb.Route.Predicates;

/// <summary>
/// The single truthiness rule of the DSL. Every place that turns a value into a boolean — the
/// condition positions (Filter, When, LoopWhile, Validate), the word-logic operators
/// (<c>AND</c>/<c>OR</c>/<c>XOR</c>/<c>NOT</c>) and the <c>logical()</c> function — goes through
/// this one method, so a value cannot be true in one position and false in another.
/// <para>
/// The rule: a bool is itself; a number is "non-zero means true"; a string is parsed as a boolean
/// word first (<c>true/false</c>, <c>1/0</c>, <c>yes/no</c>, <c>on/off</c>, <c>y/n</c>) and
/// otherwise read as "non-empty means true"; null is false; any other object exists and is
/// therefore true.
/// </para>
/// <para>
/// History note: until 2026-08-28 three different rules coexisted — this one (without the numeric
/// and word cases), the AST operand conversion, and the hand-written logical branch — and they
/// disagreed on <c>0</c>, <c>"0"</c> and <c>"no"</c>: <c>Filter("${header.zero}")</c> passed a
/// message while <c>Filter("logical(header.zero)")</c> dropped it. The merge is recorded in the
/// changelog; the characterisation snapshot pins every form the merge moved.
/// </para>
/// </summary>
internal static class RouteTruthiness
{
    /// <summary>Converts an evaluated expression result to a boolean by the DSL truthiness rule.</summary>
    /// <param name="value">The evaluated expression result.</param>
    /// <returns>The boolean reading of <paramref name="value"/>.</returns>
    internal static bool ToBoolean(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => StringToBoolean(s),
        sbyte n => n != 0,
        byte n => n != 0,
        short n => n != 0,
        ushort n => n != 0,
        int n => n != 0,
        uint n => n != 0,
        long n => n != 0,
        ulong n => n != 0,
        float n => n != 0f,
        double n => n != 0d,
        decimal n => n != 0m,
        _ => true
    };

    /// <summary>
    /// Reads a string as a boolean: a recognised boolean word wins, anything else is
    /// "non-empty means true".
    /// </summary>
    private static bool StringToBoolean(string value)
        => TryParseBooleanWord(value, out var parsed) ? parsed : value.Length > 0;

    /// <summary>
    /// Tries to read a string as an explicit boolean word. Shared with the expression engine so
    /// the words recognised by <c>logical()</c>, by equality coercion and by the condition
    /// positions never drift apart. The set is English-only by design: a routing language must
    /// not change meaning with the author's locale (the historical set also recognised Russian
    /// words; they now read as ordinary non-empty strings).
    /// </summary>
    /// <param name="value">The string to parse.</param>
    /// <param name="result">The parsed boolean when recognised.</param>
    /// <returns><c>true</c> when the string is a recognised boolean word.</returns>
    internal static bool TryParseBooleanWord(string value, out bool result)
    {
        result = false;
        if (string.IsNullOrEmpty(value))
            return false;

        if (bool.TryParse(value, out result))
            return true;

        switch (value.Trim().ToLowerInvariant())
        {
            case "1" or "yes" or "y" or "on":
                result = true;
                return true;
            case "0" or "no" or "n" or "off":
                result = false;
                return true;
            default:
                return false;
        }
    }
}
