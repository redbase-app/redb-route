using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using redb.Route.Abstractions;
using redb.Route.Predicates;

namespace redb.Route.Expressions;

/// <summary>
/// Expression for evaluating XPath queries against the message body in an <see cref="IExchange"/>.
/// </summary>
/// <remarks>
/// Extracts data from an XML document using W3C XPath 1.0 expressions.
/// Supports <see cref="XDocument"/>, <see cref="XElement"/>, string XML, and POCO bodies
/// (auto-serialized to XML). Optionally accepts an <see cref="IXmlNamespaceResolver"/>
/// for querying namespace-qualified documents.
/// </remarks>
public class XPathExpression : Expression, IPredicateExpression
{
    private readonly string _xpath;
    private readonly string _effectivePath;
    private readonly IXmlNamespaceResolver? _namespaceResolver;
    private readonly XPathResult _result;
    private readonly IExpression? _source;
    private readonly bool _trim;
    private readonly IReadOnlyList<(string Name, IExpression Value)>? _parameters;

    /// <summary>
    /// Initializes a new instance of the <see cref="XPathExpression"/> class.
    /// </summary>
    /// <param name="xpath">The XPath expression used to extract data.</param>
    /// <param name="namespaceResolver">
    /// Optional namespace resolver for querying documents with XML namespaces.
    /// </param>
    /// <param name="result">
    /// The XPath-level type of the result. The default, <see cref="XPathResult.NodeSet"/>, is
    /// XPath's own default.
    /// </param>
    /// <param name="source">
    /// What to run the path against. <c>null</c> — the default — means the message body.
    /// </param>
    /// <param name="trim">Trim whitespace off extracted text. Default <c>false</c>.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="xpath"/> is <c>null</c>.</exception>
    public XPathExpression(
        string xpath,
        IXmlNamespaceResolver? namespaceResolver = null,
        XPathResult result = XPathResult.NodeSet,
        IExpression? source = null,
        bool trim = false)
        : this(xpath, namespaceResolver, result, source, trim, null)
    {
    }

    private XPathExpression(
        string xpath,
        IXmlNamespaceResolver? namespaceResolver,
        XPathResult result,
        IExpression? source,
        bool trim,
        IReadOnlyList<(string Name, IExpression Value)>? parameters)
    {
        _xpath = xpath ?? throw new ArgumentNullException(nameof(xpath));
        _namespaceResolver = namespaceResolver;
        _result = result;
        _source = source;
        _trim = trim;
        _parameters = parameters;

        // string(), number() and boolean() are XPath's own coercions, so asking the engine for
        // them is both shorter and more faithful than converting the node-set ourselves.
        _effectivePath = result switch
        {
            XPathResult.String => $"string({_xpath})",
            XPathResult.Number => $"number({_xpath})",
            XPathResult.Boolean => $"boolean({_xpath})",
            _ => _xpath
        };
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XPathExpression"/> class with an explicit
    /// XPath-level result type.
    /// </summary>
    /// <param name="xpath">The XPath expression used to extract data.</param>
    /// <param name="result">The XPath-level type of the result.</param>
    public XPathExpression(string xpath, XPathResult result)
        : this(xpath, null, result)
    {
    }

    /// <summary>
    /// Returns the same expression reading <paramref name="source"/> instead of the message body.
    /// </summary>
    /// <remarks>
    /// <c>XPath("/invoice/id").From(Header("original-request"))</c>. Without it, parsing XML that
    /// arrived in a header means moving it into the body first — an extra step that also damages
    /// the body for the rest of the route.
    /// </remarks>
    /// <param name="source">The expression producing the XML to read.</param>
    /// <returns>A new expression; this one is unchanged.</returns>
    public XPathExpression From(IExpression source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new XPathExpression(_xpath, _namespaceResolver, _result, source, _trim, _parameters);
    }

    /// <summary>
    /// Returns the same expression with prefixes bound to namespace URIs, so a path can address a
    /// namespace-qualified document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>XPath("/s:Envelope/s:Body").WithNamespaces(("s", "http://schemas.xmlsoap.org/soap/envelope/"))</c>.
    /// The expression has always accepted an <see cref="IXmlNamespaceResolver"/>, but only through
    /// its constructor — a route written in the DSL had no way to reach it, which made every
    /// namespaced document unqueryable from a route without dropping out of the DSL.
    /// </para>
    /// <para>
    /// The prefixes are the expression's own: they need not match the ones in the document, only
    /// the URIs must. Camel keeps namespace sets in a registry and refers to them by name
    /// (<c>namespacesRef</c>) because its XML DSL cannot write a map inline; in C# the map is
    /// ordinary code, so a shared set is an ordinary variable.
    /// </para>
    /// </remarks>
    /// <param name="namespaces">Prefix/URI pairs.</param>
    /// <returns>A new expression; this one is unchanged.</returns>
    public XPathExpression WithNamespaces(params (string Prefix, string Uri)[] namespaces)
    {
        ArgumentNullException.ThrowIfNull(namespaces);

        var manager = new XmlNamespaceManager(new NameTable());
        foreach (var (prefix, uri) in namespaces)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix, nameof(namespaces));
            ArgumentException.ThrowIfNullOrWhiteSpace(uri, nameof(namespaces));
            manager.AddNamespace(prefix, uri);
        }

        return new XPathExpression(_xpath, manager, _result, _source, _trim, _parameters);
    }

    /// <summary>
    /// Returns the same expression with whitespace trimmed off the text it extracts.
    /// </summary>
    /// <remarks>
    /// Off by default, which is where this library has always been and differs from Camel's
    /// <c>trim=true</c>. For a single value XPath answers this itself —
    /// <c>XPath("normalize-space(/order/name)")</c> — but a node-set of several values cannot be
    /// normalised from inside XPath 1.0, and an indented document gives every one of them the
    /// surrounding whitespace.
    /// </remarks>
    /// <param name="trim">Whether to trim; the parameter exists so the option can be turned back off.</param>
    /// <returns>A new expression; this one is unchanged.</returns>
    public XPathExpression Trimmed(bool trim = true)
        => new(_xpath, _namespaceResolver, _result, _source, trim, _parameters);

    /// <summary>
    /// Returns the same expression with named XPath variables bound to expressions, so a value from
    /// the message can take part in the query without being pasted into its text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>XPath("/order[@id=$id]").WithParameters(("id", Header("orderId")))</c>. The value is
    /// passed to the engine <em>as a value</em>: it is compared, never parsed, so a header holding
    /// <c>B-2' or '1'='1</c> selects an order with that literal id and matches nothing, rather than
    /// changing what the query asks.
    /// </para>
    /// <para>
    /// The alternative is building the path by concatenation, which Camel offers as
    /// <c>allowSimple</c> — and which is the reason XPath injection has a CodeQL query and an OWASP
    /// entry of its own. Binding values instead is old and settled practice: JAXP's
    /// <c>XPathVariableResolver</c>, XQuery's <c>declare variable $x external</c>, .NET's
    /// <c>XsltArgumentList</c>, and prepared statements in SQL are all the same idea.
    /// </para>
    /// <para>
    /// The path also stays one constant string whatever the data, which is what lets a compiled
    /// expression be reused; concatenation defeats that by construction.
    /// </para>
    /// <para>
    /// XPath only. <c>jpath</c> has no equivalent, and not because of the library: RFC 9535 has no
    /// variables at all, so there is nothing for JsonPath.Net to expose.
    /// </para>
    /// </remarks>
    /// <param name="parameters">Variable names (without the <c>$</c>) and the expressions producing their values.</param>
    /// <returns>A new expression; this one is unchanged.</returns>
    public XPathExpression WithParameters(params (string Name, IExpression Value)[] parameters)
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

        return new XPathExpression(_xpath, _namespaceResolver, _result, _source, _trim, merged);
    }

    /// <summary>
    /// Parameters are evaluated against a message, so an evaluation that has no message cannot
    /// honour them — and silently dropping a binding would turn a query into a different query.
    /// </summary>
    private IExchange RequireExchange(IExchange? exchange)
        => exchange ?? throw new InvalidOperationException(
            $"XPath '{_xpath}' has bound parameters, which need an exchange to evaluate against. " +
            "The two-argument xpath() function of the expression language does not carry them; " +
            "use the expression object form.");

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Thrown when the exchange body is <c>null</c> or the XPath expression cannot be evaluated.
    /// </exception>
    public override T Evaluate<T>(IExchange exchange) => ConvertResult<T>(EvaluateRaw(exchange));

    /// <summary>
    /// Evaluates the expression in a condition position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node-set answers whether the path matched: XPath 1.0 reads a node-set as true exactly
    /// when it is non-empty, and the content of the matched nodes never enters the question. So
    /// <c>Filter(XPath("/order/discount"))</c> passes a message whose discount is <c>0</c>, and an
    /// empty <c>&lt;vip/&gt;</c> counts as a match.
    /// </para>
    /// <para>
    /// A scalar — from <c>string(...)</c>, <c>count(...) &gt; 0</c>, or an explicit
    /// <see cref="XPathResult"/> — is a value like any other and goes through the single DSL
    /// truthiness rule, so <c>"0"</c> is false there. To read a node's <em>content</em> as a
    /// boolean instead, ask for the CLR type: <c>XPath&lt;bool&gt;("/order/flag")</c>.
    /// </para>
    /// </remarks>
    /// <param name="exchange">The exchange to test.</param>
    /// <returns><c>true</c> when the expression holds for the exchange.</returns>
    public bool Matches(IExchange exchange)
    {
        var raw = EvaluateRaw(exchange);
        return raw is IEnumerable<object> nodes ? nodes.Any() : RouteTruthiness.ToBoolean(raw);
    }

    /// <summary>
    /// Runs the XPath and returns the engine's own result: a string, a double, a bool, or an
    /// <see cref="IEnumerable{T}"/> of nodes.
    /// </summary>
    private object EvaluateRaw(IExchange exchange)
        => EvaluateRawOn(ExpressionValues.ReadInput(_source, exchange), _source is not null, exchange);

    /// <summary>
    /// Runs the XPath against an already-resolved input, whatever produced it. Used by the
    /// expression language, where the source arrives as a value rather than as an expression.
    /// </summary>
    internal T EvaluateOn<T>(object? input, bool fromSource) => ConvertResult<T>(EvaluateRawOn(input, fromSource, null));

    private object EvaluateRawOn(object? input, bool fromSource, IExchange? exchange)
    {
        try
        {
            var body = input;
            if (body == null)
                throw new InvalidOperationException(ExpressionValues.NoInput(fromSource, "XPath"));

            var node = ResolveXNode(body);

            // Parameters are bound per message, so the resolver they travel in is built per
            // evaluation; without them the expression's own namespace resolver is passed straight
            // through, which is the path every existing route takes.
            var resolver = _parameters is null or { Count: 0 }
                ? _namespaceResolver
                : XPathVariableContext.Build(_namespaceResolver, _parameters, RequireExchange(exchange));

            // XPathEvaluate returns: string, double, bool, or IEnumerable<object> (node-set)
            var raw = System.Xml.XPath.Extensions.XPathEvaluate(node, _effectivePath, resolver);

            // NODE is a node-set narrowed to its first member in document order.
            if (_result == XPathResult.Node && raw is IEnumerable<object> nodes)
                return nodes.Take(1).ToList();

            return raw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException($"XML parsing error in XPathExpression: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Error evaluating XPath '{_xpath}': {ex.Message}", ex);
        }
    }

    // ── Body → XNode ──

    /// <summary>
    /// Resolves the exchange body to an <see cref="XNode"/> suitable for XPath evaluation.
    /// </summary>
    internal static XNode ResolveXNode(object body)
    {
        if (body is XDocument xDoc) return xDoc;
        if (body is XElement xElem) return xElem;

        if (body is string s)
        {
            s = s.Trim();
            if (s.Length == 0)
                throw new InvalidOperationException("Exchange body is an empty string. Cannot evaluate XPath expression.");
            return XDocument.Parse(s);
        }

        // POCO → serialize to XML via XmlSerializer
        return SerializeToXDocument(body);
    }

    private static XDocument SerializeToXDocument(object body)
    {
        var serializer = new XmlSerializer(body.GetType());
        using var sw = new StringWriter();
        using var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false });
        serializer.Serialize(xw, body);
        return XDocument.Parse(sw.ToString());
    }

    // ── Result conversion ──

    private T ConvertResult<T>(object raw)
    {
        // XPathEvaluate returns IEnumerable<object> for node-set results
        if (raw is IEnumerable<object> nodes)
            return ProcessNodeList<T>(nodes);

        // Scalar results (string, double, bool)
        return ConvertScalar<T>(raw);
    }

    /// <summary>
    /// Converts a scalar XPath result (string, double, bool) to the requested type.
    /// </summary>
    private T ConvertScalar<T>(object value)
    {
        // A string scalar is text the same way a node's value is, so trimming applies to both.
        if (_trim && value is string text)
            value = text.Trim();

        if (value is T direct)
            return direct;

        if (typeof(T) == typeof(string))
            return (T)(object)(value?.ToString() ?? string.Empty);

        if (typeof(T) == typeof(object))
            return (T)SmartConvertScalar(value);

        // double → numeric types
        if (value is double d)
        {
            if (typeof(T) == typeof(int)) return (T)(object)(int)d;
            if (typeof(T) == typeof(long)) return (T)(object)(long)d;
            if (typeof(T) == typeof(float)) return (T)(object)(float)d;
            if (typeof(T) == typeof(decimal)) return (T)(object)(decimal)d;
            if (typeof(T) == typeof(double)) return (T)(object)d;
            if (typeof(T) == typeof(bool)) return (T)(object)(d != 0);
        }

        // bool → other types
        if (value is bool b)
        {
            if (typeof(T) == typeof(int)) return (T)(object)(b ? 1 : 0);
            if (typeof(T) == typeof(string)) return (T)(object)(b ? "true" : "false");
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot convert XPath scalar result '{value}' ({value?.GetType().Name}) to {typeof(T).Name}.", ex);
        }
    }

    /// <summary>
    /// Smart conversion for <c>Evaluate&lt;object&gt;</c>: keeps original scalar type.
    /// </summary>
    private static object SmartConvertScalar(object value)
    {
        // double that is actually an integer → return int
        if (value is double d && d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue)
            return (int)d;

        return value;
    }

    // ── Node-set processing ──

    /// <summary>
    /// Processes a node-set (IEnumerable&lt;object&gt;) returned by <c>XPathEvaluate</c>.
    /// Nodes can be <see cref="XElement"/>, <see cref="XAttribute"/>, <see cref="XText"/>,
    /// <see cref="XComment"/>, or <see cref="XProcessingInstruction"/>.
    /// </summary>
    private T ProcessNodeList<T>(IEnumerable<object> nodes)
    {
        var list = nodes.ToList();

        if (list.Count == 0)
        {
            if (typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null)
                throw new InvalidOperationException($"No values found for XPath expression (expected non-nullable {typeof(T).Name}).");
            return default!;
        }

        // ── Single node ──
        if (list.Count == 1)
            return ConvertSingleNode<T>(list[0]);

        // ── Multiple nodes ──
        return ConvertMultipleNodes<T>(list);
    }

    /// <summary>
    /// Converts a single XPath node to the requested type.
    /// </summary>
    private T ConvertSingleNode<T>(object node)
    {
        // For T=object, extract text and smart-parse (int, double, bool, or string)
        if (typeof(T) == typeof(object))
        {
            var objectText = Text(node);
            return (T)SmartParseText(objectText);
        }

        // Return the raw node if the caller expects XElement, XAttribute, XObject, etc.
        if (node is T directMatch)
            return directMatch;

        var text = Text(node);

        if (typeof(T) == typeof(string))
            return (T)(object)text;

        // Value types: parse from text
        return ParseText<T>(text);
    }

    /// <summary>
    /// Converts multiple XPath nodes to the requested type.
    /// </summary>
    private T ConvertMultipleNodes<T>(List<object> nodeList)
    {
        var textValues = nodeList.Select(Text).ToArray();

        // string → comma-joined
        if (typeof(T) == typeof(string))
            return (T)(object)string.Join(", ", textValues);

        // string[] → direct
        if (typeof(T) == typeof(string[]))
            return (T)(object)textValues;

        // int[]
        if (typeof(T) == typeof(int[]))
            return (T)(object)textValues.Select(v => int.Parse(v)).ToArray();

        // double[]
        if (typeof(T) == typeof(double[]))
            return (T)(object)textValues.Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        // XElement[]
        if (typeof(T) == typeof(XElement[]))
            return (T)(object)nodeList.OfType<XElement>().ToArray();

        // List<XElement>
        if (typeof(T) == typeof(List<XElement>))
            return (T)(object)nodeList.OfType<XElement>().ToList();

        // object → return string[] for multiple text values
        if (typeof(T) == typeof(object))
            return (T)(object)textValues;

        // Try T[] via element-wise parsing
        if (typeof(T).IsArray)
        {
            var elemType = typeof(T).GetElementType()!;
            var arr = Array.CreateInstance(elemType, textValues.Length);
            for (int i = 0; i < textValues.Length; i++)
                arr.SetValue(Convert.ChangeType(textValues[i], elemType, System.Globalization.CultureInfo.InvariantCulture), i);
            return (T)(object)arr;
        }

        throw new InvalidOperationException(
            $"Cannot convert {nodeList.Count} XPath nodes to {typeof(T).Name}.");
    }

    // ── Helpers ──

    /// <summary>
    /// Extracts a string value from an XPath node (XElement, XAttribute, XText, etc.).
    /// </summary>
    private string Text(object node)
    {
        var value = ExtractTextValue(node);
        return _trim ? value.Trim() : value;
    }

    /// <summary>
    /// Extracts a string value from an XPath node (XElement, XAttribute, XText, etc.).
    /// </summary>
    internal static string ExtractTextValue(object node) => node switch
    {
        XElement e => e.Value,
        XAttribute a => a.Value,
        XText t => t.Value,
        XComment c => c.Value,
        XProcessingInstruction pi => pi.Data,
        _ => node?.ToString() ?? string.Empty
    };

    /// <summary>
    /// Tries to parse a text value to the best-fitting CLR type (int, double, bool, or string).
    /// Used for <c>Evaluate&lt;object&gt;</c>.
    /// </summary>
    private static object SmartParseText(string text)
    {
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(text, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        if (bool.TryParse(text, out var b))
            return b;
        return text;
    }

    /// <summary>
    /// Parses a text value to a specific type.
    /// </summary>
    private static T ParseText<T>(string text)
    {
        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        if (targetType == typeof(bool))
        {
            // Support both "true"/"false" and "1"/"0"
            if (bool.TryParse(text, out var b)) return (T)(object)b;
            if (text == "1") return (T)(object)true;
            if (text == "0") return (T)(object)false;
        }

        try
        {
            return (T)Convert.ChangeType(text, targetType, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot convert XPath text value '{text}' to {typeof(T).Name}.", ex);
        }
    }

    /// <inheritdoc />
    public override void SetValue(IExchange exchange, object value)
    {
        throw new NotSupportedException("XPathExpression does not support setting values.");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// Thrown when the expression carries anything the template cannot: a non-default
    /// <see cref="XPathResult"/>, a source, or bound parameters. The one-argument <c>xpath()</c>
    /// function of the expression language takes the path and nothing else, so a template string
    /// would quietly drop the rest and mean something different from the expression it was
    /// serialized from — a parameterised path would even fail at evaluation, its <c>$name</c>
    /// left unbound. Refusing loudly beats serialising a lie.
    /// </exception>
    public override string ToTemplateString()
    {
        if (_result != XPathResult.NodeSet)
            throw new NotSupportedException(
                $"XPathExpression with result type {_result} cannot be serialized to a template string: " +
                "the xpath() function takes only a path.");

        if (_source is not null)
            throw new NotSupportedException(
                "XPathExpression with a source cannot be serialized to a template string: " +
                "the template would silently read the body instead.");

        if (_parameters is { Count: > 0 })
            throw new NotSupportedException(
                "XPathExpression with bound parameters cannot be serialized to a template string: " +
                "the bindings would be dropped, leaving the path's $variables unbound.");

        return $"${{xpath({_xpath})}}";
    }
}
