using System;
using System.Collections.Generic;
using System.Text;
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
        // A tail that cannot compile as an expression (a dashed name is a subtraction of unknowns
        // and compiles fine; "a b c" does not) still works as a pure name lookup.
        Func<IExchange, object?>? expression;
        try
        {
            expression = GetCompiledValueExpressionWithAst(full);
        }
        catch (Exception)
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
        var parts = TemplateRegex.Split(template);
        var segments = new List<Func<IExchange, string>>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i % 2 == 0)
            {
                if (part.Length > 0)
                    segments.Add(_ => part);
            }
            else
            {
                var hole = CompilePlaceholder(part);
                segments.Add(exchange => TemplateText(hole(exchange)));
            }
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
    /// Reports whether the whole string is exactly one <c>${...}</c> placeholder, in which case
    /// its value keeps the CLR type instead of being rendered to text.
    /// </summary>
    /// <param name="template">The template string.</param>
    /// <param name="placeholder">The inner text when the string is a single placeholder.</param>
    /// <returns><c>true</c> when the string is one placeholder with nothing around it.</returns>
    internal static bool TryGetSinglePlaceholder(string template, out string placeholder)
    {
        if (template.Length > 3
            && template.StartsWith("${", StringComparison.Ordinal)
            && template.EndsWith("}", StringComparison.Ordinal)
            && template.IndexOf('}') == template.Length - 1)
        {
            placeholder = template[2..^1];
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

    private static string Unquote(string text)
        => text.Length >= 2
           && ((text.StartsWith('\'') && text.EndsWith('\'')) || (text.StartsWith('"') && text.EndsWith('"')))
            ? text[1..^1]
            : text;
}
