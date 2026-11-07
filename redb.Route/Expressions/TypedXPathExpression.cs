using redb.Route.Abstractions;

namespace redb.Route.Expressions;

/// <summary>
/// An XPath expression that remembers the target CLR type <typeparamref name="TValue"/>.
/// When the pipeline calls <c>Evaluate&lt;object&gt;(exchange)</c>, the stored type is used
/// for the internal <see cref="XPathExpression.Evaluate{T}"/> call, ensuring proper
/// type conversion (e.g., <c>xpath&lt;bool&gt;("//active")</c> returns a boxed <see cref="bool"/>
/// instead of a raw string).
/// </summary>
/// <typeparam name="TValue">The target CLR type for XPath result conversion.</typeparam>
public sealed class TypedXPathExpression<TValue> : Expression
{
    private readonly XPathExpression _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypedXPathExpression{TValue}"/> class.
    /// </summary>
    /// <param name="xpath">The XPath expression string.</param>
    /// <param name="result">
    /// The XPath-level type of the result, applied before conversion to <typeparamref name="TValue"/>.
    /// </param>
    /// <param name="source">What to run the path against; <c>null</c> means the message body.</param>
    public TypedXPathExpression(string xpath, XPathResult result = XPathResult.NodeSet, IExpression? source = null)
        : this(new XPathExpression(xpath, null, result, source))
    {
    }

    private TypedXPathExpression(XPathExpression inner) => _inner = inner;

    /// <inheritdoc cref="XPathExpression.From(IExpression)"/>
    public TypedXPathExpression<TValue> From(IExpression source) => new(_inner.From(source));

    /// <inheritdoc cref="XPathExpression.WithNamespaces"/>
    public TypedXPathExpression<TValue> WithNamespaces(params (string Prefix, string Uri)[] namespaces)
        => new(_inner.WithNamespaces(namespaces));

    /// <inheritdoc cref="XPathExpression.Trimmed(bool)"/>
    public TypedXPathExpression<TValue> Trimmed(bool trim = true) => new(_inner.Trimmed(trim));

    /// <inheritdoc cref="XPathExpression.WithParameters"/>
    public TypedXPathExpression<TValue> WithParameters(params (string Name, IExpression Value)[] parameters)
        => new(_inner.WithParameters(parameters));

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        // When the processor asks for Evaluate<object>, we evaluate with the stored TValue
        // so that XPathExpression performs proper type conversion.
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
