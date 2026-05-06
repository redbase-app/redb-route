using System;
using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for accessing the message body in an <see cref="IExchange"/>.
/// </summary>
/// <remarks>
/// Allows extracting and converting the message body
/// for use in predicates and routing logic.
/// </remarks>
public class BodyExpression : Expression
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BodyExpression"/> class.
    /// </summary>
    public BodyExpression()
    {
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    /// <exception cref="InvalidCastException">Thrown when the body type is not compatible with <typeparamref name="T"/>.</exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));
        return exchange.In.getBody<T>();
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="exchange"/> is <c>null</c>.</exception>
    public override void SetValue(IExchange exchange, object value)
    {
        if (exchange == null)
            throw new ArgumentNullException(nameof(exchange));
        exchange.In.setBody(value);
    }

    /// <inheritdoc />
    public override string ToTemplateString() => "${body}";
}
