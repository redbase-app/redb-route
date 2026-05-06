using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Expressions;

namespace redb.Route.Core;

/// <summary>
/// Abstract base class for defining routes. Subclass and implement Configure() to define route topology.
/// <example>
/// <code>
/// public class MyRoutes : RouteBuilder
/// {
///     protected override void Configure()
///     {
///         From("direct://input")
///             .SetHeader("processed", true)
///             .Process(exchange => exchange.In.Body = $"Processed: {exchange.In.Body}")
///             .To("direct://output");
///
///         From("timer://heartbeat?period=5000")
///             .SetBody("ping")
///             .To("direct://monitor");
///     }
/// }
/// </code>
/// </example>
/// </summary>
public abstract class RouteBuilder : IRouteBuilder
{
    private readonly List<RouteDefinition> _definitions = [];
    private readonly List<ExceptionRouteDefinition> _exceptionDefinitions = [];

    /// <summary>Gets the recorded route definitions after Configure() has been called.</summary>
    public IReadOnlyList<RouteDefinition> Definitions => _definitions;

    /// <summary>Gets the builder-level exception definitions after Configure() has been called, with linked types expanded.</summary>
    internal IReadOnlyList<ExceptionRouteDefinition> ExceptionDefinitions =>
        _exceptionDefinitions.SelectMany(d => d.Expand()).ToList();

    /// <summary>
    /// Override this method to define routes using the fluent DSL.
    /// Call <see cref="From"/> to start each route definition.
    /// </summary>
    protected abstract void Configure();

    /// <summary>Starts a new route definition with the given source endpoint.</summary>
    /// <param name="uri">Source endpoint URI (e.g., "direct://input", "timer://heartbeat?period=5000").</param>
    /// <returns>Route definition for fluent chaining.</returns>
    protected IRouteDefinition From(string uri)
    {
        var definition = new RouteDefinition();
        if (Context is not null)
            definition.SetRouteContext(Context);
        _definitions.Add(definition);
        return definition.From(uri);
    }

    /// <summary>
    /// Defines a global exception handler that applies to all routes in this builder.
    /// Configure retry policies, logging, and recovery steps using the returned definition.
    /// <example>
    /// <code>
    /// OnException&lt;HttpRequestException&gt;()
    ///     .MaximumRedeliveries(3)
    ///     .Handled()
    ///     .Log("HTTP error: ${exception.message}")
    ///     .To("direct://dead-letter");
    /// </code>
    /// </example>
    /// </summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <returns>Route definition for fluent chaining of handler steps and configuration.</returns>
    protected IRouteDefinition OnException<TException>() where TException : Exception
    {
        var definition = new ExceptionRouteDefinition(typeof(TException));
        if (Context is not null)
            definition.SetRouteContext(Context);
        _exceptionDefinitions.Add(definition);
        return definition;
    }

    /// <summary>
    /// Defines a global exception handler that applies to all routes in this builder
    /// for multiple exception types at once.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to handle.</param>
    /// <returns>Route definition for fluent chaining of handler steps and configuration.</returns>
    protected IRouteDefinition OnException(params Type[] exceptionTypes)
    {
        if (exceptionTypes == null || exceptionTypes.Length == 0)
            throw new ArgumentException("At least one exception type must be specified.", nameof(exceptionTypes));

        foreach (var t in exceptionTypes)
        {
            if (!typeof(Exception).IsAssignableFrom(t))
                throw new ArgumentException($"Type {t.Name} is not an Exception type.", nameof(exceptionTypes));
        }

        // Create one definition for the primary type; extra types are linked and expanded at compile time
        var def = new ExceptionRouteDefinition(exceptionTypes[0]);
        if (Context is not null)
            def.SetRouteContext(Context);
        if (exceptionTypes.Length > 1)
            def.LinkedTypes = new List<Type>(exceptionTypes.Skip(1));
        _exceptionDefinitions.Add(def);
        return def;
    }

    // ── Expression DSL helpers ──

    /// <summary>
    /// Creates a JsonPath expression for extracting data from the message body.
    /// <example><c>.SetBody(JPath("$"))</c> or <c>.SetProperty("name", JPath("$.name"))</c></example>
    /// </summary>
    /// <param name="path">JsonPath expression (e.g., <c>"$.store.books[0].title"</c>).</param>
    /// <returns>A <see cref="JsonPathExpression"/> that can be passed to <c>SetBody</c>, <c>SetHeader</c>, <c>SetProperty</c>.</returns>
    protected static JsonPathExpression JPath(string path) => new(path);

    /// <summary>
    /// Creates a typed JsonPath expression. When evaluated, the result is converted to <typeparamref name="T"/>
    /// before being set on the exchange — useful for booleans, numbers, dates, typed arrays, etc.
    /// <example><c>.SetProperty("isHired", JPath&lt;bool&gt;("$.isHired"))</c></example>
    /// </summary>
    /// <typeparam name="T">Target CLR type for the JsonPath result.</typeparam>
    /// <param name="path">JsonPath expression.</param>
    /// <returns>A <see cref="TypedJsonPathExpression{T}"/>.</returns>
    protected static TypedJsonPathExpression<T> JPath<T>(string path) => new(path);

    /// <inheritdoc cref="JPath(string)"/>
    /// <remarks>Java-style alias for <see cref="JPath(string)"/>.</remarks>
    protected static JsonPathExpression jpath(string path) => new(path);

    /// <inheritdoc cref="JPath{T}(string)"/>
    /// <remarks>Java-style alias for <see cref="JPath{T}(string)"/>.</remarks>
    protected static TypedJsonPathExpression<T> jpath<T>(string path) => new(path);

    // ── XPath expression helpers ──

    /// <summary>
    /// Creates an XPath expression for extracting data from an XML message body.
    /// <example><c>.SetBody(XPath("/root/name"))</c> or <c>.SetProperty("city", XPath("//address/city"))</c></example>
    /// </summary>
    /// <param name="path">XPath 1.0 expression (e.g., <c>"/order/items/item[1]/@id"</c>).</param>
    /// <returns>An <see cref="Expressions.XPathExpression"/> that can be passed to <c>SetBody</c>, <c>SetHeader</c>, <c>SetProperty</c>.</returns>
    protected static Expressions.XPathExpression XPath(string path) => new(path);

    /// <summary>
    /// Creates a typed XPath expression. When evaluated, the result is converted to <typeparamref name="T"/>
    /// before being set on the exchange — useful for booleans, numbers, typed arrays, etc.
    /// <example><c>.SetProperty("count", XPath&lt;int&gt;("count(//item)"))</c></example>
    /// </summary>
    /// <typeparam name="T">Target CLR type for the XPath result.</typeparam>
    /// <param name="path">XPath 1.0 expression.</param>
    /// <returns>A <see cref="TypedXPathExpression{T}"/>.</returns>
    protected static TypedXPathExpression<T> XPath<T>(string path) => new(path);

    /// <inheritdoc cref="XPath(string)"/>
    /// <remarks>Java-style alias for <see cref="XPath(string)"/>.</remarks>
    protected static Expressions.XPathExpression xpath(string path) => new(path);

    /// <inheritdoc cref="XPath{T}(string)"/>
    /// <remarks>Java-style alias for <see cref="XPath{T}(string)"/>.</remarks>
    protected static TypedXPathExpression<T> xpath<T>(string path) => new(path);

    // ── Body / Header / Property / Constant / Exchange helpers ──

    /// <summary>
    /// Creates an expression that reads the exchange message body.
    /// <example><c>.Filter(Body().isEqualTo("ok"))</c></example>
    /// </summary>
    protected static BodyExpression Body() => new();

    /// <summary>
    /// Creates an expression that reads a message header value.
    /// <example><c>.Filter(Header("status").isEqualTo("active"))</c> or <c>.SetBody(Header("source"))</c></example>
    /// </summary>
    /// <param name="name">Header name.</param>
    protected static HeaderExpression Header(string name) => new(name);

    /// <summary>
    /// Creates an expression that reads an exchange property value.
    /// <example><c>.Filter(Property("retry").isGreaterThan(0))</c></example>
    /// </summary>
    /// <param name="name">Property name.</param>
    protected static PropertyExpression Property(string name) => new(name);

    /// <summary>
    /// Creates a constant expression that always returns the specified value.
    /// <example><c>.SetBody(Constant(42))</c></example>
    /// </summary>
    /// <param name="value">The constant value.</param>
    protected static ConstantExpression Constant(object value) => new(value);

    // ── Streaming tokenizer helpers ──

    /// <summary>
    /// Creates a splitter function that splits the body into lines.
    /// Supports Stream, string, and byte[] bodies.
    /// </summary>
    /// <param name="separator">Line separator (default: newline).</param>
    /// <param name="skipEmpty">Whether to skip empty/whitespace lines.</param>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitLines(
        string separator = "\n", bool skipEmpty = false)
    {
        return exchange => Expressions.Tokenizers.LineTokenizer.Tokenize(
            exchange.In.Body, separator, skipEmpty);
    }

    /// <summary>
    /// Creates a splitter function that extracts XML elements by local name.
    /// Uses streaming XmlReader with XXE protection.
    /// </summary>
    /// <param name="elementName">Local name of elements to extract.</param>
    /// <param name="inheritNamespaceFrom">Optional parent element whose namespaces should be injected.</param>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitXml(
        string elementName, string? inheritNamespaceFrom = null)
    {
        return exchange => Expressions.Tokenizers.XmlTokenizer.Tokenize(
            exchange.In.Body, elementName, inheritNamespaceFrom);
    }

    /// <summary>
    /// Creates a splitter function that splits a JSON array body into individual elements.
    /// Each element is yielded as raw JSON text.
    /// </summary>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitJsonArray()
    {
        return exchange => Expressions.Tokenizers.JsonArrayTokenizer.Tokenize(exchange.In.Body);
    }

    /// <summary>
    /// Creates an expression evaluated from the exchange using a delegate.
    /// <example><c>.SetBody(Exchange(e => e.In.Headers["a"] + "-" + e.In.Headers["b"]))</c></example>
    /// </summary>
    /// <param name="func">Function that accepts an <see cref="IExchange"/> and returns a value.</param>
    protected static ExchangeExpression Exchange(Func<IExchange, object?> func) => new(func);

    /// <summary>
    /// Creates a string expression that evaluates a <c>${...}</c> template or raw expression
    /// via the compiled <see cref="ExpressionResolver"/>.
    /// Supports all predicate methods: <c>.isEqualTo()</c>, <c>.contains()</c>, <c>.isGreaterThan()</c>, etc.
    /// <example>
    /// <code>
    /// .SetBody(Expr("${header.source}"))
    /// .SetHeader("full", Expr("${header.first} ${header.last}"))
    /// .Filter(Expr("${header.amount}").isGreaterThan(1000))
    /// </code>
    /// </example>
    /// </summary>
    /// <param name="template">Expression string with <c>${...}</c> placeholders or raw expression.</param>
    protected static StringExpression Expr(string template) => new(template);

    /// <summary>Gets the route context (available after the engine calls Configure).</summary>
    protected IRouteContext? Context { get; private set; }

    /// <inheritdoc />
    void IRouteBuilder.Configure(IRouteContext context)
    {
        // Clear previous definitions so re-start doesn't produce duplicates
        _definitions.Clear();
        _exceptionDefinitions.Clear();

        Context = context;
        Configure();
    }
}
