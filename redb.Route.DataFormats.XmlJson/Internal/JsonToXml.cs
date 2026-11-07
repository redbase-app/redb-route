using redb.Route.Validation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>
/// JSON to XML in the Json.NET notation, the reverse of <see cref="XmlToJson"/>. A key is an element; a key under the
/// attribute prefix is an attribute (<c>@xmlns</c> and <c>@xmlns:p</c> declare namespaces); the text key is the
/// element's text; an array is the element repeated; <c>null</c> is <c>xsi:nil="true"</c>. Numbers and booleans are
/// written as the JSON writes them. A key that is not an XML name, or a value XML cannot hold where it stands, is an
/// error naming its path: nothing is renamed or dropped silently.
/// </summary>
internal sealed class JsonToXml
{
    private static readonly JsonDocumentOptions Reading = new() { MaxDepth = 256 };

    private readonly XmlJsonOptions _options;
    private readonly SchemaModel? _schema;

    /// <summary>The converter of <paramref name="options"/>, by its schema when it names one.</summary>
    public JsonToXml(XmlJsonOptions options)
        : this(options, options.SchemaSet ?? (options.Schema is { } path ? SchemaResolver.Load("xmljson", path, options.SchemaRoot) : null))
    {
    }

    private JsonToXml(XmlJsonOptions options, XmlSchemaSet? set)
    {
        _options = options;
        _schema = set is null ? null : new SchemaModel(set);
    }

    /// <summary>The converter without a schema, for content the schema says nothing about (<c>xs:any</c>).</summary>
    internal static JsonToXml Plain(XmlJsonOptions options) => new(options, (XmlSchemaSet?)null);

    /// <summary>Parses <paramref name="json"/> and returns the XML document.</summary>
    public XDocument Convert(byte[] json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json, documentOptions: Reading);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"xmljson: the body is not JSON ({ex.Message}).", ex);
        }
        return Convert(node);
    }

    /// <summary>The XML document of <paramref name="node"/>.</summary>
    public XDocument Convert(JsonNode? node)
    {
        if (_schema is not null)
            return new JsonToXmlBySchema(_options, _schema).Convert(node);

        var scope = new Scope(null);
        XElement root;
        if (_options.Root is { } rootName)
        {
            if (node is JsonArray)
                throw new InvalidOperationException(
                    $"xmljson: the JSON is an array, and its items have no element name; wrap it in an object that names them, {{\"Item\": [...]}}, under root={rootName}.");
            root = Element(rootName, node, scope, "/" + rootName);
        }
        else
        {
            if (node is not JsonObject top || top.Count != 1)
                throw new InvalidOperationException(
                    $"xmljson: the JSON is {Describe(node)}; XML has one root element: give the JSON one top-level key, or name the root with root=.");
            var (name, value) = top.First();
            if (value is JsonArray)
                throw new InvalidOperationException(
                    $"xmljson: the top-level key '{name}' holds an array; XML has one root element: name the root with root=.");
            root = Element(name, value, scope, "/" + name);
        }
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>The XML text of <paramref name="document"/>, UTF-8 without a byte order mark.</summary>
    public byte[] Write(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = _options.Indent,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }

    private XElement Element(string key, JsonNode? value, Scope parentScope, string path)
    {
        // Declarations come first: the element's own name may use a prefix it declares.
        var scope = new Scope(parentScope);
        var declarations = new List<XAttribute>();
        if (value is JsonObject declaring)
            foreach (var (name, declared) in declaring)
                if (TryDeclaration(name, out var prefix))
                {
                    var uri = Scalar(declared, path + "/" + name) ?? "";
                    scope.Declare(prefix, uri);
                    declarations.Add(prefix.Length == 0
                        ? new XAttribute("xmlns", uri)
                        : new XAttribute(XNamespace.Xmlns + prefix, uri));
                }

        var element = new XElement(Name(key, scope, path, element: true), declarations);
        switch (value)
        {
            case null:
                if (scope.TryResolve("xsi") is null)
                {
                    scope.Declare("xsi", XmlToJson.Xsi.NamespaceName);
                    element.Add(new XAttribute(XNamespace.Xmlns + "xsi", XmlToJson.Xsi.NamespaceName));
                }
                element.Add(new XAttribute(XmlToJson.Xsi + "nil", "true"));
                break;
            case JsonObject members:
                Members(element, members, scope, path);
                break;
            case JsonArray:
                throw new InvalidOperationException(
                    $"xmljson: {path} is an array inside an array; XML repeats an element, it cannot nest a list in a list.");
            default:
                element.Value = Scalar(value, path)!;
                break;
        }
        return element;
    }

    private void Members(XElement element, JsonObject members, Scope scope, string path)
    {
        foreach (var (name, value) in members)
        {
            var at = path + "/" + name;
            if (TryDeclaration(name, out _))
                continue;
            if (name == _options.TextKey)
            {
                element.Add(new XText(Scalar(value, at) ?? ""));
                continue;
            }
            if (name.StartsWith(_options.AttributePrefix, StringComparison.Ordinal) && _options.AttributePrefix.Length > 0)
            {
                var attributeName = name[_options.AttributePrefix.Length..];
                if (value is null)
                    continue; // an attribute cannot be nil: a null attribute is absent
                element.Add(new XAttribute(Name(attributeName, scope, at, element: false), Scalar(value, at)!));
                continue;
            }
            if (value is JsonArray items)
            {
                var (itemName, wrapper) = PluralItems(name, element, scope, at);
                foreach (var item in items)
                    wrapper.Add(Element(itemName, item, scope, at));
                continue;
            }
            element.Add(Element(name, value, scope, at));
        }
    }

    /// <summary>
    /// Where the items of the array under <paramref name="name"/> go: repeated <paramref name="name"/> elements in
    /// <paramref name="parent"/>, or, under the plural rule, singular elements in a <paramref name="name"/> wrapper
    /// (<c>"Incidents": [...]</c> gives <c>&lt;Incidents&gt;&lt;Incident/&gt;...&lt;/Incidents&gt;</c>).
    /// </summary>
    private (string ItemName, XElement Container) PluralItems(string name, XElement parent, Scope scope, string path)
    {
        if (_options.Plural && Plural.SingularFor(LocalPart(name)) is { } singular)
        {
            var wrapper = new XElement(Name(name, scope, path, element: true));
            parent.Add(wrapper);
            var prefix = name.Contains(':') ? name[..name.IndexOf(':')] + ":" : "";
            return (prefix + singular, wrapper);
        }
        return (name, parent);
    }

    private static string LocalPart(string name) => name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;

    private bool TryDeclaration(string key, out string prefix)
    {
        prefix = "";
        if (_options.AttributePrefix.Length == 0 || !key.StartsWith(_options.AttributePrefix, StringComparison.Ordinal))
            return false;
        var rest = key[_options.AttributePrefix.Length..];
        if (rest == "xmlns")
            return true;
        if (!rest.StartsWith("xmlns:", StringComparison.Ordinal))
            return false;
        prefix = rest["xmlns:".Length..];
        return true;
    }

    private XName Name(string key, Scope scope, string path, bool element)
    {
        var colon = key.IndexOf(':');
        var local = colon < 0 ? key : key[(colon + 1)..];
        if (!IsName(local) || (colon >= 0 && !IsName(key[..colon])))
            throw new InvalidOperationException($"xmljson: {path}: '{key}' is not an XML name.");
        if (colon < 0)
            return element && _options.Namespaces == XmlJsonNamespaces.Keep ? scope.Resolve("", path) + local : local;
        var prefix = key[..colon];
        if (prefix == "xsi")
            return scope.TryResolve(prefix) is { } declared ? declared + local : XmlToJson.Xsi + local;
        return scope.Resolve(prefix, path) + local;
    }

    internal static bool IsName(string name)
    {
        try
        {
            XmlConvert.VerifyNCName(name);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>The text of a JSON value XML can hold: a string as is, a number or boolean as JSON writes it.</summary>
    internal static string? Scalar(JsonNode? value, string path) => value switch
    {
        null => null,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        JsonValue scalar => scalar.ToJsonString(),
        _ => throw new InvalidOperationException(
            $"xmljson: {path} holds {Describe(value)}, where XML has text: an attribute, a text key or a namespace."),
    };

    internal static string Describe(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => $"an object of {o.Count} key(s)",
        JsonArray => "an array",
        _ => "a value",
    };

    /// <summary>The namespace prefixes declared by @xmlns keys, nested as the elements are.</summary>
    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, XNamespace> _prefixes = new(StringComparer.Ordinal);

        public void Declare(string prefix, string uri) => _prefixes[prefix] = uri;

        public XNamespace? TryResolve(string prefix)
        {
            for (var scope = this; scope is not null; scope = scope.Parent)
                if (scope._prefixes.TryGetValue(prefix, out var ns))
                    return ns;
            return null;
        }

        public XNamespace Resolve(string prefix, string path)
        {
            if (TryResolve(prefix) is { } ns)
                return ns;
            if (prefix.Length == 0)
                return XNamespace.None;
            throw new InvalidOperationException(
                $"xmljson: {path}: the prefix '{prefix}' is not declared; declare it with an \"@xmlns:{prefix}\" key on this element or an enclosing one.");
        }

        private Scope? Parent => parent;
    }
}
