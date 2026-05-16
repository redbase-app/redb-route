using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// A compiled XPath expression that evaluates a dynamic XPath using a delegate
/// and then delegates the actual XPath evaluation to <see cref="XPathExpression"/>.
/// </summary>
public class CompiledXPathExpression
{
    private readonly Func<IExchange, object> _pathDelegate;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompiledXPathExpression"/> class.
    /// </summary>
    /// <param name="pathDelegate">A delegate that computes the XPath string from an <see cref="IExchange"/>.</param>
    public CompiledXPathExpression(Func<IExchange, object> pathDelegate)
    {
        _pathDelegate = pathDelegate;
    }

    /// <summary>
    /// Evaluates the dynamic XPath expression against the exchange.
    /// </summary>
    /// <typeparam name="T">The type of the return value.</typeparam>
    /// <param name="exchange">The exchange containing the message data.</param>
    /// <returns>The value extracted by the computed XPath, converted to <typeparamref name="T"/>.</returns>
    public T Evaluate<T>(IExchange exchange)
    {
        var path = _pathDelegate(exchange)?.ToString();

        if (string.IsNullOrEmpty(path))
            return default(T);

        var xpathExpression = new XPathExpression(path);
        return xpathExpression.Evaluate<T>(exchange);
    }
}
