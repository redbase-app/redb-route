using System;
using System.Collections.Generic;
using System.Linq;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Adapters that let an <see cref="IExpression"/> stand where an EIP asks for a delegate.
/// </summary>
/// <remarks>
/// Six EIPs — Aggregate, RecipientList, DynamicRouter, IdempotentConsumer, Enrich and PollEnrich —
/// were typed on <see cref="Func{T, TResult}"/> alone, so a route author who wanted a correlation
/// key out of an XML body had to write the wrapper by hand:
/// <c>e =&gt; XPath("/order/id").Evaluate&lt;string&gt;(e)</c>. Wrapping is not the problem; writing
/// it once per call site is, because each hand-rolled wrapper decides on its own what an expression
/// that matched nothing means. These adapters make that decision once, in one place, so every EIP
/// answers the same way — and so a language added later inherits the answer instead of restating it.
/// </remarks>
internal static class ExpressionValues
{
    internal const string DefaultUriDelimiter = ",";

    /// <summary>
    /// Resolves what a query language should read: the message body by default, or whatever the
    /// route named as the source.
    /// </summary>
    /// <remarks>
    /// Kept here rather than in each language so that a language added later inherits the rule
    /// instead of restating it. Camel spells the source as a string prefix on each language
    /// (<c>header:payload</c>); an ordinary expression is better, because a typo in
    /// <c>header.payloadd</c> is then a parse the route can check rather than a string nobody read.
    /// </remarks>
    internal static object? ReadInput(IExpression? source, IExchange exchange)
        => source is null ? exchange.In.getBody<object>() : source.Evaluate<object?>(exchange);

    /// <summary>
    /// The message a language gives when it has nothing to read — which is a different situation
    /// depending on whether the route pointed it somewhere.
    /// </summary>
    internal static string NoInput(bool fromSource, string language) => fromSource
        ? $"The source expression produced no value, so there is nothing for the {language} " +
          "expression to read."
        : $"Exchange body is null. Cannot evaluate {language} expression.";

    /// <summary>
    /// Adapts an expression to a delegate for a position where a missing value has no sensible
    /// reading — a correlation key, an idempotency key, an endpoint URI.
    /// </summary>
    /// <remarks>
    /// A query that matched nothing yields nothing, and the alternatives are all worse than
    /// failing: an empty key silently merges unrelated messages into one aggregate, and an empty
    /// URI reaches the endpoint resolver as a parse error far from its cause.
    /// </remarks>
    internal static Func<IExchange, string> Required(IExpression expression, string eip)
    {
        ArgumentNullException.ThrowIfNull(expression);

        return exchange =>
        {
            var value = expression.Evaluate<string>(exchange);
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException(
                    $"{eip}: the expression produced no value for this exchange, and {eip} cannot " +
                    "proceed without one. A path that matched nothing yields nothing — guard the " +
                    "route with a condition, or give the expression a fallback.");

            return value;
        };
    }

    /// <summary>
    /// Adapts an expression to a delegate for a position where "no value" is itself an answer.
    /// </summary>
    /// <remarks>
    /// Dynamic Router asks for the next hop and reads <c>null</c> as "no more hops", so an
    /// expression that matched nothing ends the routing rather than failing it.
    /// </remarks>
    internal static Func<IExchange, string?> Optional(IExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return exchange => expression.Evaluate<string>(exchange);
    }

    /// <summary>
    /// Adapts an expression to a list of endpoint URIs, accepting either a sequence or a single
    /// delimited string — the two shapes an expression naturally produces.
    /// </summary>
    /// <remarks>
    /// <c>XPath("/order/recipients/uri")</c> over several nodes gives a sequence;
    /// <c>Header("recipients")</c> gives <c>"direct:a,direct:b"</c>. Both mean the same list, and
    /// which one arrives is a property of the body rather than of the route, so both are read.
    /// This mirrors <c>RoutingSlip(IExpression, uriDelimiter)</c>, which already made that choice.
    /// </remarks>
    internal static Func<IExchange, IEnumerable<string>> UriList(IExpression expression, string uriDelimiter)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var delimiter = string.IsNullOrEmpty(uriDelimiter) ? DefaultUriDelimiter : uriDelimiter;

        return exchange => Flatten(expression.Evaluate<object?>(exchange), delimiter);
    }

    private static IEnumerable<string> Flatten(object? value, string delimiter) => value switch
    {
        null => [],
        // Checked before the sequence cases: a string is a sequence of characters, not of URIs.
        string text => Split(text, delimiter),
        IEnumerable<string> many => Clean(many),
        IEnumerable<object> many => Clean(many.Select(item => item?.ToString())),
        _ => Split(value.ToString(), delimiter)
    };

    private static IEnumerable<string> Split(string? text, string delimiter) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> Clean(IEnumerable<string?> values) =>
        values.Where(uri => !string.IsNullOrWhiteSpace(uri)).Select(uri => uri!.Trim());
}
