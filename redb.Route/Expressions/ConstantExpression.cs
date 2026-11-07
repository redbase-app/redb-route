using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// A constant expression that always returns the same value regardless of the exchange.
/// </summary>
public class ConstantExpression : Expression
{
    private readonly object _value;

    /// <summary>
    /// Gets the constant value held by this expression.
    /// </summary>
    public object Value => _value;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConstantExpression"/> class with the specified value.
    /// </summary>
    /// <param name="value">The constant value.</param>
    public ConstantExpression(object value)
    {
        _value = value;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidCastException">
    /// Thrown when the constant value cannot be converted to <typeparamref name="K"/>.
    /// </exception>
    public override K Evaluate<K>(IExchange exchange)
    {
        if (_value is K typedValue)
        {
            return typedValue;
        }

        if (_value == null)
        {
            return default;
        }

        try
        {
            return (K)Convert.ChangeType(_value, typeof(K));
        }
        catch (Exception ex)
        {
            throw new InvalidCastException(
                $"Cannot convert constant value of type {_value.GetType().Name} to {typeof(K).Name}", ex);
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always thrown because a constant expression is read-only.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("Cannot set value on a constant expression");
    }

    /// <inheritdoc />
    public override string ToTemplateString() => _value?.ToString() ?? "";
}

