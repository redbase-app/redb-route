using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using redb.Route.Abstractions;
using redb.Route.Expressions;
using Engine = Wmhelp.XPath2.XPath2Expression;
using EngineIterator = Wmhelp.XPath2.XPath2NodeIterator;

namespace redb.Route.XPath2;

/// <summary>
/// An XPath 2.0 expression, usable in every position that takes an <see cref="IExpression"/>.
/// </summary>
/// <remarks>
/// <para>
/// XPath 1.0 has no regular expressions, no sequences, no date arithmetic and no conditional; a
/// route that needs one of those has to leave the language. XPath 2.0 has all of them —
/// <c>matches</c>, <c>replace</c>, <c>tokenize</c>, <c>distinct-values</c>, <c>string-join</c>,
/// <c>avg</c>, <c>max</c>, <c>if…then…else</c>, <c>for $x in … return …</c>,
/// <c>some/every … satisfies</c>, <c>xs:date</c> comparisons, <c>instance of</c>.
/// </para>
/// <para>
/// <b>What this is not.</b> XQuery. <c>let</c>, <c>where</c> and <c>order by</c> are XQuery
/// clauses, not XPath 2.0 ones, and the engine rejects them — so there is no sorting and no
/// grouping here. Filtering goes in a predicate (<c>for $o in /orders/order[total > 8] return …</c>)
/// rather than in a <c>where</c>. See <c>docs/V4/12-XQUERY.md</c> for why a full XQuery processor is
/// not shipped: every .NET implementation of one is commercial.
/// </para>
/// <para>
/// The path is compiled once, when the route is built, so a malformed expression fails the build
/// rather than the first message — the same rule the rest of the expression layer follows.
/// </para>
/// </remarks>
public class XPath2Expression : Expression, IPredicateExpression
{
    private readonly string _xpath;
    private readonly ThreadLocal<Engine> _compiled;
    private readonly IXmlNamespaceResolver? _namespaces;
    private readonly IExpression? _source;
    private readonly IReadOnlyList<(string Name, IExpression Value)>? _parameters;
    private readonly bool _trim;

    /// <summary>
    /// Compiles an XPath 2.0 expression.
    /// </summary>
    /// <param name="xpath">The XPath 2.0 expression text.</param>
    /// <param name="namespaces">Optional prefix bindings for a namespace-qualified document.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="xpath"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the expression does not parse, so a route fails at build time.
    /// </exception>
    public XPath2Expression(string xpath, IXmlNamespaceResolver? namespaces = null)
        : this(xpath, namespaces, null, null, false)
    {
    }

    private XPath2Expression(
        string xpath,
        IXmlNamespaceResolver? namespaces,
        IExpression? source,
        IReadOnlyList<(string Name, IExpression Value)>? parameters,
        bool trim)
    {
        _xpath = xpath ?? throw new ArgumentNullException(nameof(xpath));
        _namespaces = namespaces;
        _source = source;
        _parameters = parameters;
        _trim = trim;

        // Compile once here so a malformed expression fails the route build — this compilation is
        // validation, not the one evaluations use.
        try
        {
            Engine.Compile(_xpath, namespaces);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid XPath 2.0 expression '{xpath}': {ex.Message}", nameof(xpath), ex);
        }

        // Evaluations get a compiled form per thread. The engine's compiled expression mutates
        // shared slots while binding range variables (for / some / every), so one instance
        // evaluated from several threads corrupts — an IndexOutOfRangeException on a good day,
        // a neighbouring message's binding on a bad one. Its Clone() shares the same slots, so
        // isolation has to come from compiling, and per thread is where compiling stays amortised:
        // a pool thread pays the ~30% of one evaluation once, not per message.
        var path = _xpath;
        var resolver = namespaces;
        _compiled = new ThreadLocal<Engine>(() => Engine.Compile(path, resolver));
    }

    /// <summary>The expression text.</summary>
    public string Path => _xpath;

    /// <inheritdoc cref="redb.Route.Expressions.XPathExpression.From(IExpression)"/>
    public XPath2Expression From(IExpression source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new XPath2Expression(_xpath, _namespaces, source, _parameters, _trim);
    }

    /// <summary>
    /// Builds a namespace resolver from prefix/URI pairs, for the constructor.
    /// </summary>
    /// <remarks>
    /// There is deliberately no fluent <c>WithNamespaces(...)</c> here, unlike the XPath 1.0
    /// expression. XPath 2.0 resolves prefixes when the expression is <em>compiled</em>, and this
    /// type compiles in its constructor so a bad path fails the route build rather than the first
    /// message. A prefix therefore has to be known before the object exists; a fluent method could
    /// only ever run after the constructor had already rejected the path, so it would be a method
    /// that cannot work.
    /// </remarks>
    /// <param name="namespaces">Prefix/URI pairs.</param>
    /// <returns>A resolver to hand to the constructor.</returns>
    public static IXmlNamespaceResolver Namespaces(params (string Prefix, string Uri)[] namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);

        var manager = new XmlNamespaceManager(new NameTable());
        foreach (var (prefix, uri) in namespaces)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix, nameof(namespaces));
            ArgumentException.ThrowIfNullOrWhiteSpace(uri, nameof(namespaces));
            manager.AddNamespace(prefix, uri);
        }

        return manager;
    }

    /// <inheritdoc cref="redb.Route.Expressions.XPathExpression.Trimmed(bool)"/>
    public XPath2Expression Trimmed(bool trim = true)
        => new(_xpath, _namespaces, _source, _parameters, trim);

    /// <inheritdoc cref="redb.Route.Expressions.XPathExpression.WithParameters"/>
    public XPath2Expression WithParameters(params (string Name, IExpression Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        foreach (var (name, value) in parameters)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(parameters));
            ArgumentNullException.ThrowIfNull(value, nameof(parameters));
            if (name[0] == '$')
                throw new ArgumentException(
                    $"XPath parameter name '{name}' must be written without the '$': the '$' belongs " +
                    "in the path, not in the binding.", nameof(parameters));
        }

        var merged = _parameters is null or { Count: 0 }
            ? parameters
            : [.. _parameters, .. parameters];

        return new XPath2Expression(_xpath, _namespaces, _source, merged, _trim);
    }

    /// <inheritdoc />
    public override T Evaluate<T>(IExchange exchange) => XPath2Values.Convert<T>(EvaluateRaw(exchange), _xpath, _trim);

    /// <inheritdoc cref="redb.Route.Expressions.XPathExpression.Matches(IExchange)"/>
    public bool Matches(IExchange exchange) => XPath2Values.ToBoolean(EvaluateRaw(exchange));

    /// <summary>
    /// Runs the compiled expression and returns the engine's own result: a sequence, a navigator,
    /// a string, a number or a boolean.
    /// </summary>
    private object? EvaluateRaw(IExchange exchange)
    {
        var input = _source is null ? exchange.In.getBody<object>() : _source.Evaluate<object?>(exchange);
        if (input is null)
            throw new InvalidOperationException(_source is null
                ? "Exchange body is null. Cannot evaluate XPath 2.0 expression."
                : "The source expression produced no value, so there is nothing for the XPath 2.0 expression to read.");

        var node = redb.Route.Expressions.XPathExpression.ResolveXNode(input);

        try
        {
            // Evaluate takes the context item and the bindings, both per message; the compiled
            // form is this thread's own (see the constructor for why).
            return _compiled.Value!.Evaluate(new NodeContext(node.CreateNavigator()), Bindings(exchange));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error evaluating XPath 2.0 '{_xpath}': {ex.Message}", ex);
        }
    }

    private IDictionary<XmlQualifiedName, object>? Bindings(IExchange exchange)
    {
        if (_parameters is null or { Count: 0 })
            return null;

        var bindings = new Dictionary<XmlQualifiedName, object>();
        foreach (var (name, value) in _parameters)
            bindings[new XmlQualifiedName(name)] = XPath2Values.ToEngineValue(value.Evaluate<object?>(exchange));

        return bindings;
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value)
        => throw new NotSupportedException("XPath2Expression does not support setting values.");

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Always thrown: the expression language has no <c>xpath2()</c> function to serialize into.
    /// </exception>
    public override string ToTemplateString()
        => throw new NotSupportedException(
            "XPath2Expression has no template form: the expression language has no xpath2() function. " +
            "Use the expression object where a route takes an IExpression.");

    /// <summary>The context item an evaluation runs against — one node, at position one of one.</summary>
    private sealed class NodeContext(XPathNavigator navigator) : Wmhelp.XPath2.IContextProvider
    {
        public XPathItem Context => navigator;

        public int CurrentPosition => 1;

        public int LastPosition => 1;
    }
}
