using redb.Route.Abstractions;
using redb.Route.Definitions;
using redb.Route.Expressions;

namespace redb.Route.Core;

/// <summary>
/// Abstract base class for defining routes using the new <see cref="RouteDefinition"/> DSL.
/// Subclass and implement <see cref="Configure"/> to define routes with the canonical
/// ProcessorDefinition model.
/// <example>
/// <code>
/// public class MyRoutes : RouteBuilder
/// {
///     protected override void Configure()
///     {
///         From("direct://input")
///             .SetBody("hello")
///             .To("direct://output");
///
///         From("timer://heartbeat?period=5000")
///             .Log("tick");
///     }
/// }
///
/// context.AddRoutes(new MyRoutes());
/// </code>
/// </example>
/// </summary>
public abstract class RouteBuilder : IRouteBuilder
{
    private readonly List<RouteDefinition> _definitions = [];
    private readonly List<OnExceptionDefinition> _exceptionDefinitions = [];
    private readonly List<InterceptDefinition> _intercepts = [];
    private readonly List<OnCompletionDefinition> _onCompletions = [];

    /// <summary>Gets the route definitions created during <see cref="Configure"/>.</summary>
    public IReadOnlyList<RouteDefinition> Definitions => _definitions;

    /// <summary>Gets the global exception handler definitions registered via <see cref="OnException{TException}"/>.</summary>
    public IReadOnlyList<OnExceptionDefinition> ExceptionDefinitions => _exceptionDefinitions;

    /// <summary>Intercepts declared on this builder — they apply to every route it defines.</summary>
    public IReadOnlyList<InterceptDefinition> Intercepts => _intercepts;

    /// <summary>OnCompletion blocks declared on this builder — they apply to every route it defines.</summary>
    public IReadOnlyList<OnCompletionDefinition> OnCompletions => _onCompletions;

    /// <summary>Steps that run before every step of every route in this builder (Camel <c>intercept()</c>).</summary>
    public InterceptDefinition Intercept() => Register(new InterceptDefinition(InterceptKind.EveryStep, null));

    /// <summary>Steps that run when a message enters any route of this builder whose <c>From</c> matches the mask (all routes when <c>null</c>).</summary>
    public InterceptDefinition InterceptFrom(string? uriPattern = null) => Register(new InterceptDefinition(InterceptKind.From, uriPattern));

    /// <summary>Steps that run before any <c>To</c> / <c>ToD</c> in this builder whose target matches the mask; <c>.SkipSendToOriginalEndpoint()</c> replaces the send.</summary>
    public InterceptDefinition InterceptSendToEndpoint(string uriPattern) => Register(new InterceptDefinition(InterceptKind.SendToEndpoint, uriPattern));

    /// <summary>Steps that run after any route of this builder finished with an exchange (on a copy, outside the route's transaction).</summary>
    public OnCompletionDefinition OnCompletion()
    {
        var def = new OnCompletionDefinition();
        _onCompletions.Add(def);
        return def;
    }

    private InterceptDefinition Register(InterceptDefinition def)
    {
        _intercepts.Add(def);
        return def;
    }

    /// <summary>
    /// Override this method to define routes using <see cref="From"/>.
    /// </summary>
    protected abstract void Configure();

    /// <summary>
    /// Starts a new route definition with the given source endpoint. Public so that DSL layers built
    /// on top of routes (the REST DSL, generators) can add routes to a builder from outside
    /// <see cref="Configure"/>; inside a subclass it reads as before.
    /// </summary>
    /// <param name="uri">Source endpoint URI (e.g., "direct://input", "timer://ping?period=1000").</param>
    /// <returns>The new route definition for fluent chaining.</returns>
    public IRouteDefinition From(string uri)
    {
        var def = new RouteDefinition();
        def._context = Context;
        def.From(uri);
        _definitions.Add(def);
        return def;
    }

    /// <summary>
    /// Defines a global exception handler that applies to all routes in this builder.
    /// <example>
    /// <code>
    /// OnException&lt;IOException&gt;()
    ///     .MaximumRedeliveries(3)
    ///     .Handled()
    ///     .Log("IO error: ${exception.message}");
    /// </code>
    /// </example>
    /// </summary>
    /// <typeparam name="TException">Exception type to handle.</typeparam>
    /// <returns>Definition for fluent chaining of handler steps and configuration.</returns>
    public OnExceptionDefinition OnException<TException>() where TException : Exception
    {
        var def = new OnExceptionDefinition(new[] { typeof(TException) });
        _exceptionDefinitions.Add(def);
        return def;
    }

    /// <summary>
    /// Defines a global exception handler for multiple exception types at once.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to handle.</param>
    /// <returns>Definition for fluent chaining.</returns>
    public OnExceptionDefinition OnException(params Type[] exceptionTypes)
    {
        if (exceptionTypes is not { Length: > 0 })
            throw new ArgumentException("At least one exception type must be specified.", nameof(exceptionTypes));
        foreach (var t in exceptionTypes)
            if (!typeof(Exception).IsAssignableFrom(t))
                throw new ArgumentException($"Type '{t.Name}' is not an Exception type.", nameof(exceptionTypes));

        var def = new OnExceptionDefinition(exceptionTypes);
        _exceptionDefinitions.Add(def);
        return def;
    }

    /// <summary>Gets the route context, available inside <see cref="Configure"/> after registration.</summary>
    protected IRouteContext? Context { get; private set; }

    /// <summary>
    /// Called by <see cref="RouteContext"/> to populate <see cref="Definitions"/> by running <see cref="Configure"/>.
    /// </summary>
    internal void InternalBuild(IRouteContext? context = null)
    {
        _definitions.Clear();
        _exceptionDefinitions.Clear();
        _intercepts.Clear();
        _onCompletions.Clear();
        Context = context;
        Configure();
        IsBuilt = true;
    }

    /// <summary>Whether <see cref="Configure"/> has run at least once (AdviceWith builds early; a builder added later has not been built yet).</summary>
    internal bool IsBuilt { get; private set; }

    /// <inheritdoc />
    void IRouteBuilder.Configure(IRouteContext context) => InternalBuild(context);

    // ── Expression DSL helpers ──────────────────────────────────────────────

    /// <summary>Creates a JsonPath expression for extracting data from the message body.</summary>
    /// <param name="path">JsonPath expression (e.g., <c>"$.store.books[0].title"</c>).</param>
    protected static JsonPathExpression JPath(string path) => new(path);

    /// <summary>Creates a typed JsonPath expression that converts the result to <typeparamref name="T"/>.</summary>
    protected static TypedJsonPathExpression<T> JPath<T>(string path) => new(path);

    /// <inheritdoc cref="JPath(string)"/>
    /// <remarks>Java-style alias.</remarks>
    protected static JsonPathExpression jpath(string path) => new(path);

    /// <inheritdoc cref="JPath{T}(string)"/>
    /// <remarks>Java-style alias.</remarks>
    protected static TypedJsonPathExpression<T> jpath<T>(string path) => new(path);

    /// <summary>Creates an XPath expression for extracting data from an XML message body.</summary>
    /// <param name="path">XPath 1.0 expression.</param>
    /// <remarks>
    /// In a condition the result is a node-set, so the expression asks whether the path matched
    /// rather than what the matched node says. Use <see cref="XPath(string, XPathResult)"/> or
    /// <see cref="XPath{T}(string)"/> to ask the other question.
    /// </remarks>
    protected static XPathExpression XPath(string path) => new(path);

    /// <summary>Creates an XPath expression with an explicit XPath-level result type.</summary>
    /// <param name="path">XPath 1.0 expression.</param>
    /// <param name="result">The XPath-level type of the result: node-set, node, string, number or boolean.</param>
    protected static XPathExpression XPath(string path, XPathResult result) => new(path, result);

    /// <summary>Creates a typed XPath expression that converts the result to <typeparamref name="T"/>.</summary>
    protected static TypedXPathExpression<T> XPath<T>(string path) => new(path);

    /// <inheritdoc cref="XPath(string)"/>
    /// <remarks>Java-style alias.</remarks>
    protected static XPathExpression xpath(string path) => new(path);

    /// <inheritdoc cref="XPath(string, XPathResult)"/>
    /// <remarks>Java-style alias.</remarks>
    protected static XPathExpression xpath(string path, XPathResult result) => new(path, result);

    /// <inheritdoc cref="XPath{T}(string)"/>
    /// <remarks>Java-style alias.</remarks>
    protected static TypedXPathExpression<T> xpath<T>(string path) => new(path);

    /// <summary>Creates an expression that reads the exchange message body.</summary>
    protected static BodyExpression Body() => new();

    /// <summary>Creates an expression that reads a message header value.</summary>
    /// <param name="name">Header name.</param>
    protected static HeaderExpression Header(string name) => new(name);

    /// <summary>Creates an expression that reads an exchange property value.</summary>
    /// <param name="name">Property name.</param>
    protected static PropertyExpression Property(string name) => new(name);

    /// <summary>Creates a constant expression that always returns the specified value.</summary>
    /// <param name="value">The constant value.</param>
    protected static ConstantExpression Constant(object value) => new(value);

    /// <summary>Creates an expression evaluated from the exchange using a delegate.</summary>
    /// <param name="func">Function that accepts an <see cref="IExchange"/> and returns a value.</param>
    protected static ExchangeExpression Exchange(Func<IExchange, object?> func) => new(func);

    /// <summary>
    /// Creates a string expression that evaluates a <c>${...}</c> template or raw expression.
    /// <example><c>.SetBody(Expr("${header.source}"))</c></example>
    /// </summary>
    /// <param name="template">Expression string with <c>${...}</c> placeholders or raw expression.</param>
    /// <summary>
    /// A <c>${...}</c> template (or any expression text) as an <see cref="IExpression"/>:
    /// <c>SetBody(Expr("Hello ${header.name}"))</c>. Public and static, so it also reads well from the
    /// lambda style, where a protected member of the builder would be out of reach:
    /// <c>ctx.AddRoutes(b =&gt; b.From("...").SetBody(RouteBuilder.Expr("${header.x}")))</c>.
    /// Equivalent to <c>new StringExpression(template)</c>.
    /// </summary>
    public static StringExpression Expr(string template) => new(template);

    // ── Streaming tokenizer helpers ─────────────────────────────────────────

    /// <summary>Creates a splitter function that splits the body into lines.</summary>
    /// <param name="separator">Line separator (default: newline).</param>
    /// <param name="skipEmpty">Whether to skip empty/whitespace lines.</param>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitLines(
        string separator = "\n", bool skipEmpty = false) =>
        exchange => Expressions.Tokenizers.LineTokenizer.Tokenize(exchange.In.Body, separator, skipEmpty);

    /// <summary>Creates a splitter function that extracts XML elements by local name.</summary>
    /// <param name="elementName">Local name of elements to extract.</param>
    /// <param name="inheritNamespaceFrom">Optional parent element whose namespaces should be injected.</param>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitXml(
        string elementName, string? inheritNamespaceFrom = null) =>
        exchange => Expressions.Tokenizers.XmlTokenizer.Tokenize(exchange.In.Body, elementName, inheritNamespaceFrom);

    /// <summary>Creates a splitter function that splits a JSON array body into individual elements.</summary>
    protected static Func<IExchange, IAsyncEnumerable<object?>> SplitJsonArray() =>
        exchange => Expressions.Tokenizers.JsonArrayTokenizer.Tokenize(exchange.In.Body);
}

/// <summary>
/// Route builder that accepts an inline configure action, for use with
/// <see cref="RouteContext.AddRoutes(Action{InlineRouteBuilder})"/>.
/// </summary>
public sealed class InlineRouteBuilder : RouteBuilder
{
    private readonly Action<InlineRouteBuilder> _configure;

    /// <summary>Creates an inline route builder.</summary>
    public InlineRouteBuilder(Action<InlineRouteBuilder> configure)
    {
        _configure = configure ?? throw new ArgumentNullException(nameof(configure));
    }


    /// <inheritdoc />
    protected override void Configure() => _configure(this);
}
