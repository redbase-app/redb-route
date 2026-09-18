using System.Text;

namespace redb.Route.Sql;

/// <summary>A <c>:#name</c> placeholder found in a statement: its name and where it stands in the text.</summary>
/// <param name="Name">The name, without <c>:#</c>.</param>
/// <param name="Offset">Index of the placeholder's <c>:</c> in the statement.</param>
/// <param name="Length">Length of the placeholder text, <c>:#</c> included.</param>
internal readonly record struct SqlPlaceholder(string Name, int Offset, int Length);

/// <summary>
/// Finds the <c>:#name</c> placeholders of a statement — the way Apache Camel writes named parameters — and rewrites them for
/// the provider. The statement is scanned, not matched: text inside single-quoted literals (<c>''</c> escapes), double-quoted
/// and back-quoted identifiers, <c>--</c> and <c>/* */</c> comments and PostgreSQL dollar quotes (<c>$$ … $$</c>,
/// <c>$tag$ … $tag$</c>) is not searched. <c>@</c> is never a placeholder: it belongs to the database — T-SQL variables and
/// <c>EXEC</c> argument names, MySQL user variables, <c>@@IDENTITY</c>. A backslash is an ordinary character unless the caller
/// asks for backslash escapes (MySQL, MariaDB): then it escapes the next character inside <c>'…'</c> and <c>"…"</c>.
/// </summary>
internal static class SqlParameterParser
{
    /// <summary>The placeholders of <paramref name="sql"/>, in order, repeats included.</summary>
    /// <param name="sql">The statement.</param>
    /// <param name="backslashEscapes">
    /// A backslash inside <c>'…'</c> and <c>"…"</c> escapes the next character, as MySQL and MariaDB read them by default.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// <c>:#</c> is not followed by a name, or starts an Apache Camel <c>:#in:name</c> list; neither is supported.
    /// </exception>
    public static IReadOnlyList<SqlPlaceholder> FindPlaceholders(string? sql, bool backslashEscapes = false)
    {
        if (string.IsNullOrEmpty(sql))
            return [];

        List<SqlPlaceholder>? found = null;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c is '\'' or '"' or '`')
            {
                i = SkipQuoted(sql, i, c, backslashEscapes && c != '`');
            }
            else if (c == '-' && At(sql, i + 1) == '-')
            {
                i = SkipLineComment(sql, i);
            }
            else if (c == '/' && At(sql, i + 1) == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c == '$' && TryReadDollarTag(sql, i, out var tag))
            {
                i = SkipDollarQuoted(sql, i, tag);
            }
            else if (c == ':' && At(sql, i + 1) == '#')
            {
                var placeholder = ReadPlaceholder(sql, i);
                (found ??= []).Add(placeholder);
                i += placeholder.Length;
            }
            else
            {
                i++;
            }
        }

        return found is null ? [] : found;
    }

    /// <summary>Distinct placeholder names of <paramref name="sql"/>, without <c>:#</c>, in the order they first appear; case is ignored.</summary>
    public static IReadOnlyList<string> ExtractParameterNames(string? sql)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var placeholder in FindPlaceholders(sql))
        {
            if (seen.Add(placeholder.Name))
                names.Add(placeholder.Name);
        }

        return names;
    }

    /// <summary>
    /// The statement as the provider takes it: every placeholder written in <paramref name="style"/> — <c>@name</c>,
    /// <c>:name</c> or <c>?</c> — and everything else, literals and comments included, unchanged.
    /// </summary>
    public static string Rewrite(string sql, IReadOnlyList<SqlPlaceholder> placeholders, SqlPlaceholderStyle style)
    {
        if (placeholders.Count == 0)
            return sql;

        var text = new StringBuilder(sql.Length);
        var last = 0;
        foreach (var placeholder in placeholders)
        {
            text.Append(sql, last, placeholder.Offset - last);
            text.Append(style switch
            {
                SqlPlaceholderStyle.Colon => ":" + placeholder.Name,
                SqlPlaceholderStyle.Question => "?",
                _ => "@" + placeholder.Name,
            });
            last = placeholder.Offset + placeholder.Length;
        }

        text.Append(sql, last, sql.Length - last);
        return text.ToString();
    }

    private static char At(string sql, int index) => index < sql.Length ? sql[index] : '\0';

    private static bool IsNameStart(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static SqlPlaceholder ReadPlaceholder(string sql, int start)
    {
        var nameStart = start + 2;
        if (!IsNameStart(At(sql, nameStart)))
        {
            throw new InvalidOperationException(
                $"SQL placeholder ':#' at position {start} is not followed by a name: write :#name. Apache Camel's inline " +
                $"expressions (:#${{...}}) are not supported; use param.name=${{...}}. Query: {sql}");
        }

        var end = nameStart + 1;
        while (end < sql.Length && IsNamePart(sql[end]))
            end++;

        var name = sql[nameStart..end];
        if (name.Equals("in", StringComparison.OrdinalIgnoreCase) && At(sql, end) == ':' && At(sql, end + 1) != ':')
        {
            throw new InvalidOperationException(
                $"SQL placeholder ':#in:' at position {start} is an Apache Camel IN list, which is not supported; bind each value " +
                $"as its own :#name. Query: {sql}");
        }

        return new SqlPlaceholder(name, start, end - start);
    }

    /// <summary>
    /// Index after the quoted run that starts at <paramref name="start"/>; a doubled quote stays inside, and with
    /// <paramref name="backslashEscapes"/> so does the character after a backslash. Unterminated: the end.
    /// </summary>
    private static int SkipQuoted(string sql, int start, char quote, bool backslashEscapes)
    {
        var i = start + 1;
        while (i < sql.Length)
        {
            if (backslashEscapes && sql[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (sql[i] == quote)
            {
                if (At(sql, i + 1) != quote)
                    return i + 1;
                i += 2;
                continue;
            }

            i++;
        }

        return sql.Length;
    }

    private static int SkipLineComment(string sql, int start)
    {
        var end = sql.IndexOf('\n', start + 2);
        return end < 0 ? sql.Length : end + 1;
    }

    private static int SkipBlockComment(string sql, int start)
    {
        var end = sql.IndexOf("*/", start + 2, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + 2;
    }

    /// <summary>
    /// A PostgreSQL dollar-quote opener at <paramref name="start"/>: <c>$$</c> or <c>$tag$</c>, not preceded by a name
    /// character — so neither the positional parameter <c>$1</c> nor <c>a$b</c>.
    /// </summary>
    private static bool TryReadDollarTag(string sql, int start, out string tag)
    {
        tag = "";
        if (start > 0 && IsNamePart(sql[start - 1]))
            return false;

        var i = start + 1;
        if (At(sql, i) == '$')
        {
            tag = "$$";
            return true;
        }

        if (!IsNameStart(At(sql, i)))
            return false;

        while (i < sql.Length && IsNamePart(sql[i]))
            i++;

        if (At(sql, i) != '$')
            return false;

        tag = sql[start..(i + 1)];
        return true;
    }

    private static int SkipDollarQuoted(string sql, int start, string tag)
    {
        var end = sql.IndexOf(tag, start + tag.Length, StringComparison.Ordinal);
        return end < 0 ? sql.Length : end + tag.Length;
    }
}
