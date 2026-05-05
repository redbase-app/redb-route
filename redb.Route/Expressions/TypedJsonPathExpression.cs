using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// A JsonPath expression that remembers the target CLR type <typeparamref name="TValue"/>.
/// When the pipeline calls <c>Evaluate&lt;object&gt;(exchange)</c>, the stored type is used
/// for the internal <see cref="JsonPathExpression.Evaluate{T}"/> call, ensuring proper
/// type conversion (e.g., <c>jpath&lt;bool&gt;("$.isHired")</c> returns a boxed <see cref="bool"/>
/// instead of a raw <see cref="Newtonsoft.Json.Linq.JValue"/>).
/// </summary>
/// <typeparam name="TValue">The target CLR type for JsonPath result conversion.</typeparam>
public sealed class TypedJsonPathExpression<TValue> : Expression
{
    private readonly JsonPathExpression _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypedJsonPathExpression{TValue}"/> class.
    /// </summary>
    /// <param name="jsonPath">The JsonPath expression string.</param>
    public TypedJsonPathExpression(string jsonPath)
    {
        _inner = new JsonPathExpression(jsonPath);
    }

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        // When the processor asks for Evaluate<object>, we evaluate with the stored TValue
        // so that JsonPathExpression performs proper type conversion.
        if (typeof(T) == typeof(object))
        {
            var typed = _inner.Evaluate<TValue>(exchange);
            return (T)(object)typed!;
        }

        // If the caller requests a specific type, forward directly
        return _inner.Evaluate<T>(exchange);
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value)
    {
        _inner.SetValue(exchange, value);
    }
}
