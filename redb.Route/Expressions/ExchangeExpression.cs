using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// An expression that evaluates a value from an exchange using a provided function delegate.
/// </summary>
public class ExchangeExpression : Expression
{
    private readonly Func<IExchange, object?> _expressionFunc;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExchangeExpression"/> class.
    /// </summary>
    /// <param name="expressionFunc">A function that accepts an <see cref="IExchange"/> and returns a result.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="expressionFunc"/> is <c>null</c>.</exception>
    public ExchangeExpression(Func<IExchange, object?> expressionFunc)
    {
        _expressionFunc = expressionFunc ?? throw new ArgumentNullException(nameof(expressionFunc));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">Thrown when the result cannot be cast to <typeparamref name="T"/>.</exception>
    /// <exception cref="FormatException">Thrown when the result cannot be converted to <typeparamref name="T"/> due to format issues.</exception>
    /// <exception cref="InvalidOperationException">Thrown when an unexpected conversion error occurs.</exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));

        var result = _expressionFunc(exchange);

        if (result is T typedResult)
        {
            return typedResult;
        }

        try
        {
            return (T)Convert.ChangeType(result, typeof(T));
        }
        catch (InvalidCastException)
        {
            throw new InvalidCastException($"Cannot cast or convert result to type {typeof(T).Name}.");
        }
        catch (FormatException)
        {
            throw new FormatException($"Result cannot be converted to type {typeof(T).Name} due to format issues.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"An error occurred while converting result to type {typeof(T).Name}.", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because <see cref="ExchangeExpression"/> does not support setting values.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("ExchangeExpression does not support setting values.");
    }
}
