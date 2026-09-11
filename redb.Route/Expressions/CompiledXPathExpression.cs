using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// An XPath expression whose <b>path</b> is computed per message, delegating the evaluation itself
/// to <see cref="XPathExpression"/>.
/// </summary>
/// <remarks>
/// <para>
/// The ordinary case is a fixed path over a varying document. This is the other one: a route that
/// decides which path to run from the message — a rule id in a header, a per-tenant selector in an
/// exchange property. It costs an XPath compilation per message, which is why it is a separate type
/// rather than the default behaviour.
/// </para>
/// <para>
/// Until 2026-09-03 it did not implement <see cref="IExpression"/>, so despite being public and
/// tested there was nowhere in the DSL to put it: every position that takes an expression takes
/// that interface. It now is one, which makes it usable in the value, condition, routing and stream
/// positions, and in the EIPs that accept an expression.
/// </para>
/// </remarks>
public class CompiledXPathExpression : Expression
{
    private readonly Func<IExchange, object> _pathDelegate;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompiledXPathExpression"/> class.
    /// </summary>
    /// <param name="pathDelegate">A delegate that computes the XPath string from an <see cref="IExchange"/>.</param>
    public CompiledXPathExpression(Func<IExchange, object> pathDelegate)
    {
        _pathDelegate = pathDelegate ?? throw new ArgumentNullException(nameof(pathDelegate));
    }

    /// <summary>
    /// Evaluates the dynamic XPath expression against the exchange.
    /// </summary>
    /// <typeparam name="T">The type of the return value.</typeparam>
    /// <param name="exchange">The exchange containing the message data.</param>
    /// <returns>
    /// The value extracted by the computed XPath, converted to <typeparamref name="T"/>, or the
    /// default when the delegate produced no path.
    /// </returns>
    public override T Evaluate<T>(IExchange exchange)
    {
        var path = _pathDelegate(exchange)?.ToString();

        if (string.IsNullOrEmpty(path))
            return default(T);

        var xpathExpression = new XPathExpression(path);
        return xpathExpression.Evaluate<T>(exchange);
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value)
        => throw new NotSupportedException("CompiledXPathExpression does not support setting values.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: the path exists only per message, so there is no fixed text to serialize.
    /// </exception>
    public override string ToTemplateString()
        => throw new NotSupportedException(
            "CompiledXPathExpression computes its path per message and has no template form.");
}
