using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// The template engine: the one pipeline that gives a <c>${...}</c> placeholder its meaning.
/// Until 2026-08-28 two independent engines interpreted placeholders — one under
/// <see cref="StringExpression"/>, another under <c>ResolveTypedOrTemplate</c> — with
/// complementary holes: the first understood unspaced comparisons and dashed header names but not
/// arithmetic, the second the reverse. Both now route through <see cref="CompilePlaceholder"/>,
/// so a placeholder means the same thing wherever it is written.
/// </summary>
public static partial class ExpressionResolver
{
    /// <summary>
    /// Compiles the inner text of a single <c>${...}</c> placeholder into a typed evaluator.
    /// <para>
    /// The rules, in order: <c>jpath(...)</c> and <c>xpath(...)</c> keep their own syntax (the
    /// expression tokenizer does not read <c>$</c> or <c>/</c> paths); <c>body</c> and
    /// <c>contentType</c> are direct accessors; a <c>header.</c> or <c>property.</c> prefix
    /// resolves by the literal-name-first rule below; anything else is a full expression compiled
    /// by the AST engine — arithmetic, comparisons, functions, ternary and index access included.
    /// </para>
    /// <para>
    /// Literal-name-first: <c>header.Content-Type</c> is the header of that exact name when it
    /// exists; <c>header.user.Age</c> is the member path behind the <c>user</c> header;
    /// <c>header.a+1</c>, matching neither, is the expression. The name check happens per
    /// exchange, which is what makes a dash usable in a name at all — no grammar can tell a
    /// dashed name from a subtraction, but the message can.
    /// </para>
    /// </summary>
    /// <param name="placeholder">The text between <c>${</c> and <c>}</c>.</param>
    /// <returns>An evaluator returning the CLR-typed value of the placeholder.</returns>
    /// <exception cref="ExpressionCompilationException">Thrown when the text is not a resolvable placeholder.</exception>
    internal static Func<IExchange, object?> CompilePlaceholder(string placeholder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(placeholder);
        var text = placeholder.Trim();

        // ${expr:F4}: a format suffix, format(expr, 'F4') with the invariant culture (findings register row 34 point 4).
        if (TrySplitFormatSuffix(text, out var formatted, out var suffix))
        {
            var value = CompilePlaceholder(formatted);
            return exchange => Ast_Format(value(exchange), suffix, null);
        }

        // ${jsonpath:$.a.b} and ${xpath://a}: Apache Camel's spellings of jpath('...') and xpath('...') (row 34 point 2).
        // The path is the rest of the placeholder as written; only a whole placeholder, where its end is known.
        foreach (var (prefix, apply) in PathSpellings)
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                var path = text[prefix.Length..].Trim();
                if (path.Length == 0)
                    throw new ExpressionCompilationException($"${{{text}}} names no path: write ${{{prefix}$.a.b}} or ${{{prefix}//a}}.");
                return exchange => apply(exchange, path);
            }

        // ${file:name}: the file language over the file headers (row 34 point 3).
        if (text.StartsWith("file:", StringComparison.Ordinal))
            return FileKey(text);
        if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ExpressionCompilationException(
                $"${{{text}}}: file keys are lowercase: ${{file:{text["file:".Length..].Trim().ToLowerInvariant()}}}.");

        // A namespaced call (bean:svc.Check(...)) is the AST's, deterministically: no shortcut below knows it.
        if (ContainsNamespacedCall(text))
            return GetCompiledValueExpressionWithAst(text);

        var jpath = JPathFunctionRegex.Match(text);
        if (jpath.Success && jpath.Value.Trim() == text && !HasSecondArgument(jpath.Groups[1].Value))
        {
            var path = Unquote(jpath.Groups[1].Value.Trim());
            return exchange => ApplyJPath(exchange, path);
        }

        var xpath = XPathFunctionRegex.Match(text);
        if (xpath.Success && xpath.Value.Trim() == text && !HasSecondArgument(xpath.Groups[1].Value))
        {
            var path = Unquote(xpath.Groups[1].Value.Trim());
            return exchange => ApplyXPath(exchange, path);
        }

        if (text == "body")
            return exchange => exchange.In.getBody<object>();

        if (text == "contentType")
            return exchange => exchange.In.ContentType;

        if (text.StartsWith(HEADER_PREFIX, StringComparison.Ordinal) && text.Length > HEADER_PREFIX.Length)
            return CompileAccessorPlaceholder(text, text[HEADER_PREFIX.Length..], isHeader: true);

        if (text.StartsWith(PROPERTY_PREFIX, StringComparison.Ordinal) && text.Length > PROPERTY_PREFIX.Length)
            return CompileAccessorPlaceholder(text, text[PROPERTY_PREFIX.Length..], isHeader: false);

        return GetCompiledValueExpressionWithAst(text);
    }

    /// <summary>
    /// Tells a one-argument <c>xpath(path)</c> from a two-argument <c>xpath(path, source)</c>.
    /// </summary>
    /// <remarks>
    /// The shortcut above exists because a bare path is not something the expression tokenizer can
    /// read — <c>$.store.book</c> and <c>/order/id</c> are not tokens. A second argument is,
    /// though, and it has to be evaluated against the exchange, so the placeholder belongs to the
    /// AST engine instead. Commas inside quotes and inside brackets are part of the path
    /// (<c>$..book[0,1]</c>, <c>/a[@x='1,2']</c>) and do not count.
    /// </remarks>
    private static bool HasSecondArgument(string arguments)
    {
        var depth = 0;
        var quote = '\0';

        foreach (var c in arguments)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }

            switch (c)
            {
                case '\'' or '"': quote = c; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',' when depth == 0: return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The literal-name-first evaluator for a <c>header.</c> or <c>property.</c> placeholder:
    /// the exact name wins, then the smart nested path, then the whole text as an expression.
    /// The expression leg is compiled eagerly so a placeholder that can be nothing at all still
    /// fails at build time rather than rendering as emptiness forever.
    /// </summary>
    private static Func<IExchange, object?> CompileAccessorPlaceholder(string full, string tail, bool isHeader)
    {
        // A tail that cannot compile as an expression still works as a pure name lookup when it is one word (a dashed
        // name is a subtraction of unknowns and compiles anyway). A tail with whitespace is an expression, and one that
        // does not compile (header.b == 3 garbage) is the error, at build time (findings register row 32).
        Func<IExchange, object?>? expression;
        try
        {
            expression = GetCompiledValueExpressionWithAst(full);
        }
        catch (Exception) when (!tail.Any(char.IsWhiteSpace))
        {
            expression = null;
        }

        var nested = tail.Contains('.');

        return exchange =>
        {
            if (isHeader)
            {
                if (exchange.In.Headers.ContainsKey(tail))
                    return exchange.In.getHeader<object>(tail);
                if (nested)
                {
                    var value = ResolveHeaderSmart(exchange, tail);
                    if (value is not null)
                        return value;
                }
            }
            else
            {
                if (exchange.Properties.ContainsKey(tail))
                    return exchange.getProperty<object>(tail);
                if (nested)
                {
                    var value = ResolvePropertySmart(exchange, tail);
                    if (value is not null)
                        return value;
                }
            }

            return expression?.Invoke(exchange);
        };
    }

    /// <summary>
    /// Compiles a full template — literal text with <c>${...}</c> holes — into a string
    /// producer. Each hole goes through <see cref="CompilePlaceholder"/>; the text between holes
    /// is emitted verbatim, whatever operators it happens to contain.
    /// </summary>
    private static Func<IExchange, string> CompileTemplateString(string template)
    {
        var holes = TemplateScanner.Holes(template);
        var segments = new List<Func<IExchange, string>>(holes.Count * 2 + 1);

        var position = 0;
        foreach (var hole in holes)
        {
            if (hole.Start > position)
            {
                var literal = Unescape(template[position..hole.Start]);
                segments.Add(_ => literal);
            }
            var compiled = CompilePlaceholder(hole.InnerOf(template));
            segments.Add(exchange => TemplateText(compiled(exchange)));
            position = hole.Start + hole.Length;
        }
        if (position < template.Length)
        {
            var tail = Unescape(template[position..]);
            segments.Add(_ => tail);
        }

        return exchange =>
        {
            var text = new StringBuilder();
            foreach (var segment in segments)
                text.Append(segment(exchange));
            return text.ToString();
        };
    }

    /// <summary>
    /// The text between holes with each escape <c>\${</c> written as the literal <c>${</c> it stands for. Outside a
    /// hole a <c>\${</c> is always an escape: the scanner never opens a hole there.
    /// </summary>
    private static string Unescape(string literal)
        => literal.Contains("\\${", StringComparison.Ordinal) ? literal.Replace("\\${", "${", StringComparison.Ordinal) : literal;

    /// <summary>
    /// Reports whether the whole string is exactly one <c>${...}</c> placeholder, in which case
    /// its value keeps the CLR type instead of being rendered to text.
    /// </summary>
    /// <param name="template">The template string.</param>
    /// <param name="placeholder">The inner text when the string is a single placeholder.</param>
    /// <returns><c>true</c> when the string is one placeholder with nothing around it.</returns>
    internal static bool TryGetSinglePlaceholder(string template, out string placeholder)
    {
        if (template.Length > 3 && template.StartsWith("${", StringComparison.Ordinal)
            && TemplateScanner.Scan(template) is { Unclosed.Count: 0, Holes: [var only] } && only.Length == template.Length)
        {
            placeholder = only.InnerOf(template);
            return true;
        }

        placeholder = string.Empty;
        return false;
    }

    /// <summary>
    /// Gets a cached typed evaluator for the inner text of a single placeholder.
    /// </summary>
    internal static Func<IExchange, object?> GetCompiledPlaceholder(string placeholder, string? contextId = null)
    {
        var cacheKey = BuildCacheKey(contextId, "placeholder:" + placeholder);
        return _valueExpressionCache.GetOrAdd(cacheKey, _ => CompilePlaceholder(placeholder));
    }

    /// <summary>
    /// <c>${file:key}</c>, Apache Camel's file language (row 34 point 3) over the <c>redbFile.*</c> headers the file
    /// consumers set, through <see cref="FileLanguage"/>; an unknown key is an error listing the keys.
    /// </summary>
    private static Func<IExchange, object?> FileKey(string text)
    {
        var key = text["file:".Length..];
        if (!FileLanguage.TryGetFact(key, StringComparison.Ordinal, out var fact))
            throw new ExpressionCompilationException($"${{{text}}}: {FileLanguage.UnknownKeyMessage(key)}");
        return exchange => FileLanguage.Value(fact, new FileHeaderFacts(exchange.In.Headers));
    }

    /// <summary>The Camel path spellings of a whole placeholder and the readers they stand for.</summary>
    private static readonly (string Prefix, Func<IExchange, string, object?> Apply)[] PathSpellings =
    [
        ("jsonpath:", ApplyJPath),
        ("xpath:", ApplyXPath),
    ];

    /// <summary>
    /// A placeholder <c>expr:FORMAT</c> whose last colon at the top level (outside quotes, parentheses and brackets, with
    /// no <c>?</c> of a ternary before it) is followed by a standard .NET format: one letter and up to two digits
    /// (<c>F4</c>, <c>N2</c>, <c>D8</c>). The left side must name a value with a dot or a call (<c>total.CostUsd</c>,
    /// <c>round(x)</c>), so a namespace written against its name (<c>env:X</c>, <c>file:name</c>) is never a format.
    /// </summary>
    internal static bool TrySplitFormatSuffix(string text, out string expression, out string format)
    {
        expression = format = string.Empty;
        var depth = 0;
        var quote = '\0';
        var colon = -1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < text.Length) i++;
                else if (ch == quote) quote = '\0';
                continue;
            }
            switch (ch)
            {
                case '\'' or '"': quote = ch; break;
                case '(' or '[': depth++; break;
                case ')' or ']': if (depth > 0) depth--; break;
                case '?' when depth == 0: return false; // a ternary's colon, or ??
                case ':' when depth == 0: colon = i; break;
            }
        }
        if (colon <= 0)
            return false;
        var left = text[..colon].Trim();
        var right = text[(colon + 1)..].Trim();
        if (!FormatSuffixRegex.IsMatch(right) || left.Length == 0 || BareWordRegex.IsMatch(left))
            return false;
        expression = left;
        format = right;
        return true;
    }

    private static readonly Regex FormatSuffixRegex = new(@"^[A-Za-z][0-9]{0,2}$", RegexOptions.Compiled);
    private static readonly Regex BareWordRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static string Unquote(string text)
        => text.Length >= 2
           && ((text.StartsWith('\'') && text.EndsWith('\'')) || (text.StartsWith('"') && text.EndsWith('"')))
            ? text[1..^1]
            : text;
}
