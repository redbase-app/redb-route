using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// A compiled JPath expression that evaluates a dynamic JSON path using a delegate
/// and then delegates the actual JSON path evaluation to <see cref="JsonPathExpression"/>.
/// </summary>
public class CompiledJPathExpression
{
    private readonly Func<IExchange, object> _pathDelegate;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompiledJPathExpression"/> class.
    /// </summary>
    /// <param name="pathDelegate">A delegate that computes the JSON path string from an <see cref="IExchange"/>.</param>
    public CompiledJPathExpression(Func<IExchange, object> pathDelegate)
    {
        _pathDelegate = pathDelegate;
    }

    /// <summary>
    /// Evaluates the dynamic JSON path expression against the exchange.
    /// </summary>
    /// <typeparam name="T">The type of the return value.</typeparam>
    /// <param name="exchange">The exchange containing the message data.</param>
    /// <returns>The value extracted by the computed JSON path, converted to <typeparamref name="T"/>.</returns>
    public T Evaluate<T>(IExchange exchange)
    {
        // Compute the path using the delegate
        var path = _pathDelegate(exchange)?.ToString();

        if (string.IsNullOrEmpty(path))
        {
            return default(T);
        }

        // Create a JsonPathExpression with the computed path
        var jsonPathExpression = new JsonPathExpression(path);

        // Evaluate the value at the path
        return jsonPathExpression.Evaluate<T>(exchange);
    }
} 
