using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for accessing properties in an <see cref="IExchange"/>.
/// </summary>
/// <remarks>
/// Allows extracting exchange property values
/// for use in predicates and routing logic.
/// </remarks>
public class PropertyExpression : Expression
{
    private readonly string _propertyName;

    /// <summary>
    /// Initializes a new instance of the <see cref="PropertyExpression"/> class.
    /// </summary>
    /// <param name="propertyName">The property name (case-sensitive).</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="propertyName"/> is <c>null</c>.</exception>
    public PropertyExpression(string propertyName)
    {
        _propertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">Thrown when the property type is not compatible with <typeparamref name="T"/>.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the property is not found.</exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));

        return exchange.getProperty<T>(_propertyName);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));
        exchange.setProperty(_propertyName, value);
    }

    /// <inheritdoc />
    public override string ToTemplateString() => $"${{property.{_propertyName}}}";
}
