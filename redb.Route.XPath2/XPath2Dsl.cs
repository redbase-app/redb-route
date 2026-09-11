using System.Xml;
using redb.Route.Abstractions;

namespace redb.Route.XPath2;

/// <summary>
/// Factory methods for XPath 2.0 expressions, written to be imported:
/// <c>using static redb.Route.XPath2.XPath2Dsl;</c>
/// </summary>
/// <remarks>
/// <para>
/// The XPath 1.0 helpers live on <c>RouteBuilder</c> as protected members, which a package outside
/// the core assembly cannot extend. A static import gives the same shape at the call site —
/// <c>.Filter(XPath2("some $o in /orders/order satisfies $o/total > 100"))</c> — without the core
/// having to know this package exists.
/// </para>
/// </remarks>
public static class XPath2Dsl
{
    /// <summary>Creates an XPath 2.0 expression over the message body.</summary>
    /// <param name="path">The XPath 2.0 expression text.</param>
    public static XPath2Expression XPath2(string path) => new(path);

    /// <summary>Creates an XPath 2.0 expression with namespace prefixes bound.</summary>
    /// <param name="path">The XPath 2.0 expression text.</param>
    /// <param name="namespaces">Prefix/URI pairs.</param>
    /// <remarks>
    /// The prefixes go in here rather than into a fluent call, because XPath 2.0 resolves them
    /// while compiling and the expression compiles as soon as it exists — see
    /// <see cref="XPath2Expression.Namespaces"/>.
    /// </remarks>
    public static XPath2Expression XPath2(string path, params (string Prefix, string Uri)[] namespaces)
        => new(path, XPath2Expression.Namespaces(namespaces));

    /// <summary>Creates an XPath 2.0 expression with an explicit namespace resolver.</summary>
    /// <param name="path">The XPath 2.0 expression text.</param>
    /// <param name="namespaces">The resolver to use.</param>
    public static XPath2Expression XPath2(string path, IXmlNamespaceResolver namespaces) => new(path, namespaces);

    /// <summary>Creates an XPath 2.0 expression whose result is converted to <typeparamref name="T"/>.</summary>
    /// <param name="path">The XPath 2.0 expression text.</param>
    public static TypedXPath2Expression<T> XPath2<T>(string path) => new(path);

    /// <inheritdoc cref="XPath2(string)"/>
    /// <remarks>Java-style alias, matching the <c>xpath(...)</c> spelling of the 1.0 helpers.</remarks>
    public static XPath2Expression xpath2(string path) => new(path);

    /// <inheritdoc cref="XPath2{T}(string)"/>
    /// <remarks>Java-style alias.</remarks>
    public static TypedXPath2Expression<T> xpath2<T>(string path) => new(path);
}

/// <summary>
/// An XPath 2.0 expression that remembers the CLR type its result should take, so a pipeline
/// asking for <c>Evaluate&lt;object&gt;</c> still gets that type.
/// </summary>
/// <typeparam name="TValue">The target CLR type.</typeparam>
public sealed class TypedXPath2Expression<TValue> : redb.Route.Expressions.Expression
{
    private readonly XPath2Expression _inner;

    /// <summary>Creates a typed XPath 2.0 expression.</summary>
    /// <param name="path">The XPath 2.0 expression text.</param>
    public TypedXPath2Expression(string path) : this(new XPath2Expression(path))
    {
    }

    private TypedXPath2Expression(XPath2Expression inner) => _inner = inner;

    /// <inheritdoc cref="XPath2Expression.From(IExpression)"/>
    public TypedXPath2Expression<TValue> From(IExpression source) => new(_inner.From(source));

    /// <inheritdoc cref="XPath2Expression.Trimmed(bool)"/>
    public TypedXPath2Expression<TValue> Trimmed(bool trim = true) => new(_inner.Trimmed(trim));

    /// <inheritdoc cref="XPath2Expression.WithParameters"/>
    public TypedXPath2Expression<TValue> WithParameters(params (string Name, IExpression Value)[] parameters)
        => new(_inner.WithParameters(parameters));

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange)
    {
        if (typeof(T) == typeof(object))
            return (T)(object)_inner.Evaluate<TValue>(exchange)!;

        return _inner.Evaluate<T>(exchange);
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value) => _inner.SetValue(exchange, value);
}
