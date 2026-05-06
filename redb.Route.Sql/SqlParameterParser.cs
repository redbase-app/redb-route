using System.Text.RegularExpressions;

namespace redb.Route.Sql;

/// <summary>
/// Extracts <c>@paramName</c> parameter names from SQL text.
/// Handles standard SQL syntax — skips string literals and comments.
/// </summary>
internal static partial class SqlParameterParser
{
    // Matches @paramName (but not @@variables like @@IDENTITY)
    [GeneratedRegex(@"(?<!@)@([A-Za-z_]\w*)", RegexOptions.Compiled)]
    private static partial Regex ParamRegex();

    /// <summary>
    /// Extracts all unique parameter names from SQL text (without the @ prefix).
    /// </summary>
    /// <param name="sql">SQL text containing @param references.</param>
    /// <returns>Distinct parameter names.</returns>
    public static IReadOnlyList<string> ExtractParameterNames(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return [];

        var matches = ParamRegex().Matches(sql);
        if (matches.Count == 0)
            return [];

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in matches)
            names.Add(match.Groups[1].Value);

        return [.. names];
    }

    /// <summary>
    /// Translates <c>@param</c> to <c>:param</c> for Oracle-style providers.
    /// </summary>
    /// <param name="sql">SQL with <c>@param</c> syntax.</param>
    /// <returns>SQL with <c>:param</c> syntax.</returns>
    public static string TranslateToOracle(string sql)
    {
        return ParamRegex().Replace(sql, ":$1");
    }
}
