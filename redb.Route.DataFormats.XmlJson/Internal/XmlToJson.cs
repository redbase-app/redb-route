using redb.Route.Validation;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using System.Xml.Linq;
using redb.Route.Core;

namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>
/// XML to JSON in the Json.NET notation. The root element is the one key of the result. An element with neither
/// attributes nor child elements is its text (<c>""</c> when empty, <c>null</c> under <c>xsi:nil="true"</c>); any other
/// element is an object: attributes under the attribute prefix, child elements by name in document order, its text
/// under the text key. Child elements of one name are an array when they repeat, when an arrays path names them, or
/// when the schema lets them repeat. With a schema, numbers and booleans are JSON numbers and booleans, and an element
/// of element content is an object even when empty. Comments and processing instructions are left out; CDATA is text.
/// </summary>
internal sealed class XmlToJson
{
    internal static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private static readonly JsonSerializerOptions Indented = new(Compact) { WriteIndented = true };

    private readonly XmlJsonOptions _options;
    private readonly ArrayPaths _arrays;
    private readonly SchemaModel? _schema;

    public XmlToJson(XmlJsonOptions options)
    {
        _options = options;
        _arrays = new ArrayPaths(options.Arrays);
        var set = options.SchemaSet ?? (options.Schema is { } path ? SchemaResolver.Load("xmljson", path, options.SchemaRoot) : null);
        _schema = set is null ? null : new SchemaModel(set);
    }

    /// <summary>Reads <paramref name="xml"/> through <see cref="SafeXml"/> and returns the JSON tree.</summary>
    public JsonObject Convert(byte[] xml) => Convert(SafeXml.Load(xml, XmlLimits.Default, LoadOptions.PreserveWhitespace));

    /// <summary>The JSON tree of <paramref name="document"/>.</summary>
    public JsonObject Convert(XDocument document)
    {
        var root = document.Root ?? throw new InvalidOperationException("xmljson: the XML document has no root element.");
        var path = new List<(string Local, string Written)>();
        return new JsonObject { [JsonName(root)] = Element(root, path, _schema?.Root(root.Name)) };
    }

    /// <summary>The JSON text of <paramref name="tree"/>, UTF-8.</summary>
    public byte[] Write(JsonNode tree)
        => Encoding.UTF8.GetBytes(tree.ToJsonString(_options.Indent ? Indented : Compact));

    private JsonNode? Element(XElement element, List<(string Local, string Written)> path, SchemaElement? declared)
    {
        path.Add((element.Name.LocalName, PrefixedName(element)));
        try
        {
            var attributes = Attributes(element, declared, path, out var nil);
            var children = element.Elements().ToList();
            if (attributes.Count == 0 && children.Count == 0 && !SchemaModel.IsComplexContent(declared?.Type))
                return nil ? null : SchemaModel.Typed(Text(element, onlyChild: true), SchemaModel.TextDatatype(declared?.Type), Where(path));
            if (attributes.Count == 0 && children.Count == 0 && nil)
                return null;

            // Beside other attributes a nil is kept as the attribute it is, so nothing of the element is lost.
            if (nil)
                attributes.Add((_options.AttributePrefix + AttributeName(element, element.Attribute(Xsi + "nil")!),
                    JsonValue.Create(element.Attribute(Xsi + "nil")!.Value)));

            var result = new JsonObject();
            foreach (var (name, value) in attributes)
                result[name] = value;

            var groups = children.GroupBy(JsonName, StringComparer.Ordinal).ToList();
            // A plural wrapper holds child elements of one name only; its own attributes do not matter.
            var pluralWrapper = _options.Plural && groups.Count == 1
                                && Internal.Plural.IsPair(element.Name.LocalName, children[0].Name.LocalName);
            foreach (var group in groups)
            {
                var members = group.ToList();
                var child = ChildDeclaration(declared, members[0], path);
                path.Add((members[0].Name.LocalName, PrefixedName(members[0])));
                var isArray = members.Count > 1 || _arrays.Matches(path) || child?.Repeats == true || pluralWrapper;
                path.RemoveAt(path.Count - 1);
                if (isArray)
                {
                    var array = new JsonArray();
                    foreach (var member in members)
                        array.Add(Element(member, path, ChildDeclaration(declared, member, path)));
                    result[group.Key] = array;
                }
                else
                {
                    result[group.Key] = Element(members[0], path, child);
                }
            }

            var text = Text(element, onlyChild: children.Count == 0);
            if (text.Length > 0)
                result[_options.TextKey] = SchemaModel.Typed(text, SchemaModel.TextDatatype(declared?.Type), Where(path));
            return result;
        }
        finally
        {
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>
    /// The declaration of <paramref name="child"/> in the content of its parent; an error when the schema has no place
    /// for it. <c>null</c> without a schema.
    /// </summary>
    private SchemaElement? ChildDeclaration(SchemaElement? parent, XElement child, List<(string Local, string Written)> path)
    {
        if (_schema is null || parent is null)
            return null;
        return _schema.Child(parent, child.Name) ?? throw new InvalidOperationException(
            $"xmljson: {Where(path)} holds '{child.Name.LocalName}', which its content in the schema has no place for.");
    }

    /// <summary>
    /// The attributes as JSON keys and values. <c>xsi:nil="true"</c> makes <paramref name="nil"/> true and is not an
    /// attribute of the result; namespace declarations are attributes under <see cref="XmlJsonNamespaces.Keep"/> only.
    /// </summary>
    private List<(string Name, JsonNode? Value)> Attributes(XElement element, SchemaElement? declared,
        List<(string Local, string Written)> path, out bool nil)
    {
        nil = false;
        var result = new List<(string, JsonNode?)>();
        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name == Xsi + "nil" && attribute.Value.Trim() is "true" or "1")
            {
                nil = true;
                continue;
            }
            if (attribute.IsNamespaceDeclaration)
            {
                if (_options.Namespaces == XmlJsonNamespaces.Keep)
                    result.Add((_options.AttributePrefix + (attribute.Name.Namespace == XNamespace.None
                        ? "xmlns"
                        : "xmlns:" + attribute.Name.LocalName), JsonValue.Create(attribute.Value)));
                continue;
            }
            var name = AttributeName(element, attribute);
            result.Add((_options.AttributePrefix + name, SchemaModel.Typed(attribute.Value,
                SchemaModel.AttributeDatatype(declared?.Type, attribute.Name), Where(path) + "/@" + name)));
        }
        return result;
    }

    private string AttributeName(XElement element, XAttribute attribute)
    {
        if (_options.Namespaces == XmlJsonNamespaces.Drop || attribute.Name.Namespace == XNamespace.None)
            return attribute.Name.LocalName;
        var prefix = element.GetPrefixOfNamespace(attribute.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? attribute.Name.LocalName : prefix + ":" + attribute.Name.LocalName;
    }

    /// <summary>The element's name as a JSON key: the written name, or the local name under <see cref="XmlJsonNamespaces.Drop"/>.</summary>
    private string JsonName(XElement element)
        => _options.Namespaces == XmlJsonNamespaces.Drop ? element.Name.LocalName : PrefixedName(element);

    private static string PrefixedName(XElement element)
    {
        if (element.Name.Namespace == XNamespace.None)
            return element.Name.LocalName;
        var prefix = element.GetPrefixOfNamespace(element.Name.Namespace);
        return string.IsNullOrEmpty(prefix) ? element.Name.LocalName : prefix + ":" + element.Name.LocalName;
    }

    private static string Where(List<(string Local, string Written)> path)
        => "/" + string.Join('/', path.Select(p => p.Written));

    /// <summary>
    /// The element's own text: every text and CDATA node in order. Beside child elements a text node of white space
    /// only is layout and is left out; an element without children keeps its text as written.
    /// </summary>
    private static string Text(XElement element, bool onlyChild)
    {
        var text = new StringBuilder();
        foreach (var node in element.Nodes().OfType<XText>())
        {
            if (!onlyChild && string.IsNullOrWhiteSpace(node.Value))
                continue;
            text.Append(node.Value);
        }
        return text.ToString();
    }
}
