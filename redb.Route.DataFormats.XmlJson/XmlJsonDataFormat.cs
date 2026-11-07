using redb.Route.Validation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.DataFormats.XmlJson.Internal;

namespace redb.Route.DataFormats.XmlJson;

/// <summary>
/// XML to JSON and back without a class, as Apache Camel's <c>xmljson</c>: <c>Marshal</c> turns an XML body into JSON,
/// <c>Unmarshal</c> turns a JSON body into XML (or, to a <see cref="JsonNode"/>, an XML body into the JSON tree). The
/// notation is Json.NET's (<c>@attribute</c>, <c>#text</c>); which elements are arrays comes from the schema, the arrays
/// paths, the opt-in plural rule, and otherwise from the document itself (a repeat is an array).
/// <para>
/// When a class exists for the document, the typed path stays the recommended one: <c>Unmarshal&lt;Order&gt;()</c> from
/// XML, then <c>Marshal()</c> to JSON.
/// </para>
/// <para>
/// The schema path resolves through the route context's resource resolver, as <c>validateXsd</c> does, on the first
/// message of each context; the compiled schema is kept for that context.
/// </para>
/// </summary>
public sealed class XmlJsonDataFormat : IMessageDataFormat
{
    /// <summary>Content type the format is registered under, beside the name <c>xmljson</c>.</summary>
    public const string DefaultContentType = "application/x-xmljson";

    /// <summary>The name the format answers to in markup: <c>&lt;marshal format="xmljson"/&gt;</c>.</summary>
    public const string Name = "xmljson";

    private readonly XmlJsonOptions _options;
    private readonly ConditionalWeakTable<IRouteContext, Converters> _byContext = new();
    private Converters? _withoutContext;

    /// <summary>Creates the format with default options; markup sets its properties.</summary>
    public XmlJsonDataFormat() : this(null)
    {
    }

    /// <summary>Creates the format with the given options (defaults when <c>null</c>).</summary>
    public XmlJsonDataFormat(XmlJsonOptions? options) => _options = options ?? new XmlJsonOptions();

    /// <summary>The options in effect.</summary>
    public XmlJsonOptions Options => _options;

    // ── Markup properties: the options in the string forms an attribute can carry ─────────────

    /// <summary>The schema path (<see cref="XmlJsonOptions.Schema"/>).</summary>
    public string? Schema { get => _options.Schema; set => _options.Schema = value; }

    /// <summary>The directory imports may read from, relative to the schema's directory (<see cref="XmlJsonOptions.SchemaRoot"/>).</summary>
    public string? SchemaRoot { get => _options.SchemaRoot; set => _options.SchemaRoot = value; }

    /// <summary>The arrays paths, comma-separated: <c>//Item,/Order/Tag</c> (<see cref="XmlJsonOptions.Arrays"/>).</summary>
    public string? Arrays
    {
        get => _options.Arrays.Count == 0 ? null : string.Join(',', _options.Arrays);
        set
        {
            _options.Arrays.Clear();
            foreach (var path in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                _options.Arrays.Add(path);
        }
    }

    /// <summary>Namespace prefixes, comma-separated <c>prefix=uri</c> pairs: <c>o=urn:orders</c> (<see cref="XmlJsonOptions.Prefixes"/>).</summary>
    public string? Prefixes
    {
        get => _options.Prefixes.Count == 0 ? null : string.Join(',', _options.Prefixes.Select(p => p.Key + "=" + p.Value));
        set
        {
            _options.Prefixes.Clear();
            foreach (var pair in (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0 || equals == pair.Length - 1)
                    throw new ArgumentException($"xmljson prefixes '{pair}' is not prefix=uri, as in o=urn:orders.", nameof(value));
                _options.Prefixes[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
            }
        }
    }

    /// <summary>The plural-wrapper rule (<see cref="XmlJsonOptions.Plural"/>).</summary>
    public bool Plural { get => _options.Plural; set => _options.Plural = value; }

    /// <summary>How names carry their namespaces (<see cref="XmlJsonOptions.Namespaces"/>).</summary>
    public XmlJsonNamespaces Namespaces { get => _options.Namespaces; set => _options.Namespaces = value; }

    /// <summary>The text key (<see cref="XmlJsonOptions.TextKey"/>).</summary>
    public string TextKey { get => _options.TextKey; set => _options.TextKey = value; }

    /// <summary>The attribute prefix (<see cref="XmlJsonOptions.AttributePrefix"/>).</summary>
    public string AttributePrefix { get => _options.AttributePrefix; set => _options.AttributePrefix = value; }

    /// <summary>The root element of JSON to XML (<see cref="XmlJsonOptions.Root"/>).</summary>
    public string? Root { get => _options.Root; set => _options.Root = value; }

    /// <summary>Whether the output is indented (<see cref="XmlJsonOptions.Indent"/>).</summary>
    public bool Indent { get => _options.Indent; set => _options.Indent = value; }

    // ── IMessageSerializer ────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public string ContentType => DefaultContentType;

    /// <inheritdoc />
    public IReadOnlyCollection<string> MediaTypes => [DefaultContentType];

    /// <inheritdoc />
    public bool WrapsBytes => true;

    /// <summary>XML to JSON, without a route context: the schema path resolves through the default resolver.</summary>
    public byte[] Serialize<T>(T value) => ToJson(For(null), Xml(value));

    /// <inheritdoc />
    public T? Deserialize<T>(byte[] data) => (T?)Deserialize(data, typeof(T));

    /// <summary>
    /// Without a route context: JSON to XML as <paramref name="type"/> (<c>string</c>, <c>byte[]</c>,
    /// <see cref="XDocument"/>, <see cref="XmlDocument"/>), or XML to the JSON tree for a <see cref="JsonNode"/> type.
    /// </summary>
    public object? Deserialize(byte[] data, Type type)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Read(For(null), data, type);
    }

    // ── IMessageDataFormat ────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValueTask MarshalAsync(IExchange exchange, CancellationToken ct = default)
    {
        var body = exchange.In.Body;
        if (body is null)
            return ValueTask.CompletedTask;
        exchange.In.Body = ToJson(For(exchange.Context), Xml(body));
        exchange.In.ContentType = "application/json";
        exchange.In.Headers["Content-Type"] = "application/json";
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask UnmarshalAsync(IExchange exchange, Type targetType, CancellationToken ct = default)
    {
        var bytes = exchange.In.Body switch
        {
            null => null,
            byte[] b => b,
            string s => Encoding.UTF8.GetBytes(s),
            Stream stream => await ReadAll(stream, ct).ConfigureAwait(false),
            JsonNode node => Encoding.UTF8.GetBytes(node.ToJsonString()),
            var other => throw new InvalidOperationException(
                $"xmljson: cannot unmarshal a {other.GetType().Name} body; it reads JSON (or XML, to a JsonNode) as bytes, a string or a stream."),
        };
        if (bytes is null)
            return;
        exchange.In.Body = Read(For(exchange.Context), bytes, targetType);
        var contentType = typeof(JsonNode).IsAssignableFrom(targetType) ? "application/json" : "application/xml";
        exchange.In.ContentType = contentType;
        exchange.In.Headers["Content-Type"] = contentType;
    }

    // ── Conversion ────────────────────────────────────────────────────────────────────────────

    private static byte[] ToJson(Converters converters, byte[] xml) => converters.ToJson.Write(converters.ToJson.Convert(xml));

    private static object? Read(Converters converters, byte[] data, Type type)
    {
        if (typeof(JsonNode).IsAssignableFrom(type))
            return converters.ToJson.Convert(data);
        var document = converters.ToXml.Convert(data);
        if (type == typeof(XDocument) || type == typeof(object))
            return document;
        if (type == typeof(XElement))
            return document.Root;
        var bytes = converters.ToXml.Write(document);
        if (type == typeof(byte[]))
            return bytes;
        if (type == typeof(string))
            return Encoding.UTF8.GetString(bytes);
        if (type == typeof(XmlDocument))
            return SafeXml.LoadDocument(bytes, XmlLimits.Default);
        throw new InvalidOperationException(
            $"xmljson: cannot unmarshal to {type.Name}; the targets are string, byte[], XDocument, XElement, XmlDocument (XML from JSON) and JsonNode (JSON from XML). For a class, unmarshal with the JSON or XML format to it.");
    }

    /// <summary>The XML bytes of a body: bytes or text as they are, an XML tree written out.</summary>
    private static byte[] Xml(object? body) => body switch
    {
        null => [],
        byte[] bytes => bytes,
        string text => Encoding.UTF8.GetBytes(text),
        XDocument document => Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting)),
        XElement element => Encoding.UTF8.GetBytes(element.ToString(SaveOptions.DisableFormatting)),
        XmlDocument document => Encoding.UTF8.GetBytes(document.OuterXml),
        Stream stream => ReadAllSync(stream),
        var other => throw new InvalidOperationException(
            $"xmljson: cannot marshal a {other.GetType().Name} body; it reads XML as bytes, a string, a stream or an XML tree. For a class, marshal it with the JSON format."),
    };

    private static byte[] ReadAllSync(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadAll(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>The converters of a context: its schema resolved once through that context's resource resolver.</summary>
    private Converters For(IRouteContext? context)
    {
        if (context is null)
            return _withoutContext ??= Build(null);
        return _byContext.GetValue(context, Build);
    }

    private Converters Build(IRouteContext? context)
    {
        var set = _options.SchemaSet;
        if (set is null && _options.Schema is { } schema)
        {
            set = SchemaResolver.Load("xmljson", ResourceResolution.Resolve(context, schema, "xmljson schema"), _options.SchemaRoot);
        }
        var resolved = Copy(set);
        return new Converters(new XmlToJson(resolved), new JsonToXml(resolved));
    }

    /// <summary>The options with the compiled schema set in place of the path, so the converters do not load it again.</summary>
    private XmlJsonOptions Copy(XmlSchemaSet? set)
    {
        var copy = new XmlJsonOptions
        {
            TextKey = _options.TextKey,
            AttributePrefix = _options.AttributePrefix,
            Namespaces = _options.Namespaces,
            Plural = _options.Plural,
            Root = _options.Root,
            Indent = _options.Indent,
            SchemaSet = set,
        };
        foreach (var path in _options.Arrays)
            copy.Arrays.Add(path);
        foreach (var (prefix, uri) in _options.Prefixes)
            copy.Prefixes[prefix] = uri;
        return copy;
    }

    private sealed record Converters(XmlToJson ToJson, JsonToXml ToXml);
}
