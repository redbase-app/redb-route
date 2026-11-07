namespace redb.Route.Expressions;

/// <summary>
/// Finds the <c>${...}</c> holes of a template: the one reader every place that looks for them uses (the template
/// compiler, <see cref="StringExpression"/>, <see cref="Core.EndpointOptions"/>, the single-placeholder test, the
/// pack gate). A hole closes at the <c>}</c> that balances its <c>${</c>, so a hole may hold another one
/// (<c>${bean:svc.Check(${body})}</c>) and a quoted literal may hold a <c>}</c> (<c>${header.a == '}'}</c>).
/// <para>
/// A plain <c>{</c> does not nest and an empty <c>${}</c> is no hole, as before. A quote that never ends inside a
/// hole is read as a character, not as a literal (<c>${header.O'Brien}</c>). A <c>${</c> that nothing closes is an
/// error: the expression was started, so it is a mistake (owner decision 2026-11-05); see <see cref="Unclosed"/>.
/// A literal <c>${</c> is written <c>\${</c>: an escape opens nothing and renders as <c>${</c>.
/// </para>
/// </summary>
internal static class TemplateScanner
{
    /// <summary>One hole: <see cref="Start"/> is the index of <c>$</c>, <see cref="Length"/> spans to the closing <c>}</c>.</summary>
    internal readonly record struct Hole(int Start, int Length)
    {
        /// <summary>The text between <c>${</c> and the closing <c>}</c>.</summary>
        public string InnerOf(string template) => template.Substring(Start + 2, Length - 3);
    }

    /// <summary>
    /// The holes of a template, the index of every <c>${</c> that nothing closes, and the index of every escape
    /// <c>\${</c> (a literal <c>${</c> in the text).
    /// </summary>
    internal sealed record ScanResult(IReadOnlyList<Hole> Holes, IReadOnlyList<int> Unclosed, IReadOnlyList<int> Escapes);

    /// <summary>
    /// The holes of <paramref name="template"/>, in order. Throws <see cref="ExpressionCompilationException"/> naming
    /// the position of the first <c>${</c> that nothing closes.
    /// </summary>
    internal static IReadOnlyList<Hole> Holes(string template)
    {
        var scan = Scan(template);
        if (scan.Unclosed.Count > 0)
            throw new ExpressionCompilationException(Unclosed(template, scan.Unclosed[0]));
        return scan.Holes;
    }

    /// <summary>
    /// Whether <paramref name="template"/> starts at least one <c>${...}</c>, closed or not: an unclosed one is a
    /// template too, and fails where it is compiled.
    /// </summary>
    internal static bool HasPlaceholder(string template)
    {
        if (!template.Contains("${", StringComparison.Ordinal))
            return false;
        var scan = Scan(template);
        return scan.Holes.Count > 0 || scan.Unclosed.Count > 0 || scan.Escapes.Count > 0;
    }

    /// <summary>The message for a <c>${</c> at <paramref name="index"/> that nothing closes.</summary>
    internal static string Unclosed(string template, int index)
        => $"unclosed '${{' at position {index + 1} of \"{template}\": the placeholder has no closing '}}'.";

    /// <summary>Scans <paramref name="template"/>; see the class remarks for the reading.</summary>
    internal static ScanResult Scan(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var holes = new List<Hole>();
        var unclosed = new List<int>();
        var escapes = new List<int>();
        var i = 0;
        while (i < template.Length - 1)
        {
            // \${ is a literal ${ (findings register row 16): it opens nothing, and the compiler writes ${ in its place.
            if (template[i] == '\\' && i + 2 < template.Length && template[i + 1] == '$' && template[i + 2] == '{')
            {
                escapes.Add(i);
                i += 3;
                continue;
            }
            if (template[i] != '$' || template[i + 1] != '{')
            {
                i++;
                continue;
            }

            var close = Close(template, i, quotes: true) ?? Close(template, i, quotes: false);
            if (close is null)
            {
                unclosed.Add(i);
                break;
            }
            if (close.Value - i - 2 > 0)
            {
                holes.Add(new Hole(i, close.Value - i + 1));
                i = close.Value + 1;
            }
            else
            {
                i++;
            }
        }
        return new ScanResult(holes, unclosed, escapes);
    }

    /// <summary>The index of the <c>}</c> that balances the <c>${</c> at <paramref name="start"/>, or null.</summary>
    private static int? Close(string template, int start, bool quotes)
    {
        var depth = 0;
        var j = start;
        while (j < template.Length)
        {
            var c = template[j];
            if (c == '$' && j + 1 < template.Length && template[j + 1] == '{')
            {
                depth++;
                j += 2;
                continue;
            }
            if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return j;
                j++;
                continue;
            }
            if (quotes && (c == '\'' || c == '"'))
            {
                var endQuote = EndOfQuoted(template, j);
                if (endQuote < 0)
                    return null;
                j = endQuote + 1;
                continue;
            }
            j++;
        }
        return null;
    }

    /// <summary>The index of the quote that ends the literal opened at <paramref name="open"/>, or -1.</summary>
    private static int EndOfQuoted(string template, int open)
    {
        var quote = template[open];
        for (var j = open + 1; j < template.Length; j++)
        {
            if (template[j] == '\\')
            {
                j++;
                continue;
            }
            if (template[j] == quote)
                return j;
        }
        return -1;
    }
}
