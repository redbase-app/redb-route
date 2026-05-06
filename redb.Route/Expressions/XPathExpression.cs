using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using redb.Route.Abstractions;

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
public class XPathExpression : Expression
{
    private readonly string _xpath;
    private readonly IXmlNamespaceResolver? _namespaceResolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="XPathExpression"/> class.
    /// </summary>
    /// <param name="xpath">The XPath expression used to extract data.</param>
    /// <param name="namespaceResolver">
    /// Optional namespace resolver for querying documents with XML namespaces.
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="xpath"/> is <c>null</c>.</exception>
    public XPathExpression(string xpath, IXmlNamespaceResolver? namespaceResolver = null)
    {
        _xpath = xpath ?? throw new ArgumentNullException(nameof(xpath));
        _namespaceResolver = namespaceResolver;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Thrown when the exchange body is <c>null</c> or the XPath expression cannot be evaluated.
    /// </exception>
    public override T Evaluate<T>(IExchange exchange)
    {
        try
        {
            var body = exchange.In.getBody<object>();
            if (body == null)
                throw new InvalidOperationException("Exchange body is null. Cannot evaluate XPath expression.");

            var node = ResolveXNode(body);

            // XPathEvaluate returns: string, double, bool, or IEnumerable<object> (node-set)
            var raw = System.Xml.XPath.Extensions.XPathEvaluate(node, _xpath, _namespaceResolver);

            return ConvertResult<T>(raw);
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

    private static T ConvertResult<T>(object raw)
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
    private static T ConvertScalar<T>(object value)
    {
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
    private static T ProcessNodeList<T>(IEnumerable<object> nodes)
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
    private static T ConvertSingleNode<T>(object node)
    {
        // For T=object, extract text and smart-parse (int, double, bool, or string)
        if (typeof(T) == typeof(object))
        {
            var objectText = ExtractTextValue(node);
            return (T)SmartParseText(objectText);
        }

        // Return the raw node if the caller expects XElement, XAttribute, XObject, etc.
        if (node is T directMatch)
            return directMatch;

        var text = ExtractTextValue(node);

        if (typeof(T) == typeof(string))
            return (T)(object)text;

        // Value types: parse from text
        return ParseText<T>(text);

        // Value types: parse from text
        return ParseText<T>(text);
    }

    /// <summary>
    /// Converts multiple XPath nodes to the requested type.
    /// </summary>
    private static T ConvertMultipleNodes<T>(List<object> nodeList)
    {
        var textValues = nodeList.Select(ExtractTextValue).ToArray();

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
    public override string ToTemplateString() => $"${{xpath({_xpath})}}";
}
