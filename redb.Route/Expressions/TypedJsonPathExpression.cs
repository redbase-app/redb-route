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
    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypedJsonPathExpression{TValue}"/> class.
    /// </summary>
    /// <param name="jsonPath">The JsonPath expression string.</param>
    /// <param name="source">What to run the path against; <c>null</c> means the message body.</param>
    public TypedJsonPathExpression(string jsonPath, IExpression? source = null)
    {
        _path = jsonPath;
        _inner = new JsonPathExpression(jsonPath, source);
    }

    /// <inheritdoc cref="JsonPathExpression.From(IExpression)"/>
    public TypedJsonPathExpression<TValue> From(IExpression source) => new(_path, source);

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
