using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>
/// JSON to XML by a schema. The schema decides what JSON keys alone cannot: the namespace of every element and
/// attribute, the order of children (the order the content declares, whatever the order of the keys), which branch of
/// a choice is taken, and the text of a value (numbers in the lexical form of their type). A JSON key matches an
/// element or attribute by its local name; a prefix on the key only picks the prefix written for that namespace.
/// Strict: a key the content has no place for, two branches of one choice, an array where one element is allowed, or
/// null where the element is not nillable, is an error naming the JSON path. Whether every required element is present
/// is left to <c>validateXsd</c>, the recommended next step.
/// </summary>
internal sealed class JsonToXmlBySchema
{
    private readonly XmlJsonOptions _options;
    private readonly SchemaModel _schema;
    private readonly Dictionary<string, string> _declared = new(StringComparer.Ordinal); // namespace -> prefix from @xmlns keys

    public JsonToXmlBySchema(XmlJsonOptions options, SchemaModel schema)
    {
        _options = options;
        _schema = schema;
    }

    public XDocument Convert(JsonNode? node)
    {
        string key;
        JsonNode? content;
        if (_options.Root is { } rootName)
        {
            key = rootName;
            content = node;
        }
        else
        {
            if (node is not JsonObject top || top.Count != 1)
                throw new InvalidOperationException(
                    $"xmljson: the JSON is {JsonToXml.Describe(node)}; XML has one root element: give the JSON one top-level key, or name the root with root=.");
            (key, content) = top.First();
        }

        var globals = _schema.GlobalsNamed(LocalPart(key));
        if (globals.Count != 1)
            throw new InvalidOperationException(globals.Count == 0
                ? $"xmljson: the root '{key}' is not a global element of the schema."
                : $"xmljson: the root '{key}' is a global element in {globals.Count} namespaces; the schema cannot tell which.");
        var declaration = globals[0];
        if (content is JsonArray)
            throw new InvalidOperationException($"xmljson: /{key} holds an array; XML has one root element.");

        CollectDeclarations(content);
        var root = Element(declaration.QualifiedName, declaration, content, "/" + key);
        DeclareNamespaces(root);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private XElement Element(XmlQualifiedName name, XmlSchemaElement declaration, JsonNode? value, string path)
    {
        var element = new XElement(XName.Get(name.Name, name.Namespace));
        var type = declaration.ElementSchemaType;
        switch (value)
        {
            case null:
                if (!declaration.IsNillable)
                    throw new InvalidOperationException($"xmljson: {path} is null, and the schema does not declare it nillable.");
                element.Add(new XAttribute(XmlToJson.Xsi + "nil", "true"));
                break;
            case JsonObject members:
                Members(element, type, members, path);
                break;
            case JsonArray:
                throw new InvalidOperationException(
                    $"xmljson: {path} is an array inside an array; XML repeats an element, it cannot nest a list in a list.");
            default:
                if (SchemaModel.IsComplexContent(type) && type is XmlSchemaComplexType { ContentType: not XmlSchemaContentType.Mixed })
                    throw new InvalidOperationException(
                        $"xmljson: {path} holds {JsonToXml.Describe(value)}, and the schema declares element content: an object is expected.");
                element.Value = Lexical(value, SchemaModel.TextDatatype(type), path);
                break;
        }
        return element;
    }

    private void Members(XElement element, XmlSchemaType? type, JsonObject members, string path)
    {
        var slots = _schema.Slots(type);
        var children = new List<(ContentSlot Slot, int Sequence, XElement Element)>();
        var branches = new Dictionary<int, (int Branch, string Key)>();
        var sequence = 0;

        foreach (var (key, value) in members)
        {
            var at = path + "/" + key;
            if (IsDeclaration(key))
                continue;
            if (key == _options.TextKey)
            {
                element.Add(new XText(Lexical(value, SchemaModel.TextDatatype(type), at)));
                continue;
            }
            if (_options.AttributePrefix.Length > 0 && key.StartsWith(_options.AttributePrefix, StringComparison.Ordinal))
            {
                Attribute(element, type, key[_options.AttributePrefix.Length..], value, at);
                continue;
            }

            var local = LocalPart(key);
            var slot = slots.FirstOrDefault(s => !s.Wildcard && s.Name.Name == local) ?? slots.FirstOrDefault(s => s.Wildcard)
                ?? throw new InvalidOperationException($"xmljson: {at}: the content of {path} in the schema has no place for '{local}'.");

            if (slot.Branch is { } branch)
            {
                if (branches.TryGetValue(branch.Choice, out var taken) && taken.Branch != branch.Branch)
                    throw new InvalidOperationException(
                        $"xmljson: {path} has both '{taken.Key}' and '{key}', two branches of one choice in the schema.");
                branches[branch.Choice] = (branch.Branch, key);
            }

            foreach (var item in Items(slot, key, value, at))
                children.Add((slot, sequence++, item));
        }

        foreach (var (_, _, child) in children.OrderBy(c => c.Slot.Order).ThenBy(c => c.Sequence))
            element.Add(child);
    }

    private IEnumerable<XElement> Items(ContentSlot slot, string key, JsonNode? value, string path)
    {
        if (slot.Wildcard)
        {
            // Under xs:any nothing is known: a global declaration of the name if there is one, else the plain reverse.
            var global = _schema.GlobalsNamed(LocalPart(key));
            if (global.Count == 1)
                return Repeat(value, path, item => Element(global[0].QualifiedName, global[0], item, path));
            var plain = JsonToXml.Plain(_options);
            return Repeat(value, path, item => plain.Convert(new JsonObject { [key] = item?.DeepClone() }).Root!);
        }

        var declaration = slot.Declaration!;
        if (value is JsonArray array && !slot.Repeats)
        {
            if (WrapperItem(declaration) is { } inner)
            {
                // A schema wrapper: "Items": [...] where Items holds one repeated element writes Items with its items.
                var wrapper = new XElement(XName.Get(slot.Name.Name, slot.Name.Namespace));
                foreach (var item in array)
                    wrapper.Add(Element(inner.Name, inner.Declaration!, item, path));
                return [wrapper];
            }
            if (array.Count > 1)
                throw new InvalidOperationException(
                    $"xmljson: {path} is an array of {array.Count}, and the schema allows one '{slot.Name.Name}' here.");
        }
        return Repeat(value, path, item => Element(slot.Name, declaration, item, path));
    }

    /// <summary>The one repeated element a wrapper's content consists of, or <c>null</c>.</summary>
    private ContentSlot? WrapperItem(XmlSchemaElement declaration)
    {
        var slots = _schema.Slots(declaration.ElementSchemaType);
        return slots.Count == 1 && slots[0] is { Wildcard: false, Repeats: true } only
               && declaration.ElementSchemaType is XmlSchemaComplexType { AttributeUses.Count: 0 }
            ? only
            : null;
    }

    private static IEnumerable<XElement> Repeat(JsonNode? value, string path, Func<JsonNode?, XElement> one)
        => value is JsonArray array ? array.Select(one).ToList() : [one(value)];

    private void Attribute(XElement element, XmlSchemaType? type, string name, JsonNode? value, string path)
    {
        if (value is null)
            return; // an attribute cannot be nil: a null attribute is absent
        var local = LocalPart(name);
        var prefix = name.Contains(':') ? name[..name.IndexOf(':')] : null;
        if (prefix == "xsi")
        {
            element.Add(new XAttribute(XmlToJson.Xsi + local, JsonToXml.Scalar(value, path)!));
            return;
        }
        var declared = SchemaModel.AttributeNamed(type, local);
        if (declared is null && !SchemaModel.HasAnyAttribute(type))
            throw new InvalidOperationException($"xmljson: {path}: the schema declares no attribute '{local}' here.");
        var xname = declared is null ? XName.Get(local) : XName.Get(declared.QualifiedName.Name, declared.QualifiedName.Namespace);
        element.Add(new XAttribute(xname, Lexical(value, declared?.AttributeSchemaType?.Datatype, path)));
    }

    /// <summary>
    /// The text of a value in the lexical form of its type: a string as is, a boolean as <c>true</c>/<c>false</c>, a
    /// number as the JSON writes it, except that a decimal-family type never takes an exponent (<c>1e3</c> is written
    /// <c>1000</c>).
    /// </summary>
    private static string Lexical(JsonNode? value, XmlSchemaDatatype? datatype, string path)
    {
        var text = JsonToXml.Scalar(value, path) ?? "";
        if (value is JsonValue number && number.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && datatype is { TypeCode: not (XmlTypeCode.Double or XmlTypeCode.Float) } && IsNumeric(datatype.TypeCode)
            && text.IndexOfAny(['e', 'E']) >= 0)
            return decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return text;
    }

    private static bool IsNumeric(XmlTypeCode code) => code is XmlTypeCode.Decimal or XmlTypeCode.Integer
        or XmlTypeCode.NonPositiveInteger or XmlTypeCode.NegativeInteger or XmlTypeCode.Long or XmlTypeCode.Int
        or XmlTypeCode.Short or XmlTypeCode.Byte or XmlTypeCode.NonNegativeInteger or XmlTypeCode.UnsignedLong
        or XmlTypeCode.UnsignedInt or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedByte or XmlTypeCode.PositiveInteger;

    private bool IsDeclaration(string key)
    {
        var p = _options.AttributePrefix;
        return p.Length > 0 && (key == p + "xmlns" || key.StartsWith(p + "xmlns:", StringComparison.Ordinal));
    }

    /// <summary>The prefixes the JSON declares with @xmlns:p keys anywhere, to write them for those namespaces.</summary>
    private void CollectDeclarations(JsonNode? node)
    {
        if (node is JsonObject members)
        {
            foreach (var (key, value) in members)
            {
                var p = _options.AttributePrefix;
                if (p.Length > 0 && key.StartsWith(p + "xmlns:", StringComparison.Ordinal)
                    && JsonToXml.Scalar(value, key) is { Length: > 0 } uri)
                    _declared.TryAdd(uri, key[(p.Length + "xmlns:".Length)..]);
                else
                    CollectDeclarations(value);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var item in items)
                CollectDeclarations(item);
        }
    }

    /// <summary>Declares on the root every namespace the document uses: the JSON's prefix, the configured one, or nsN.</summary>
    private void DeclareNamespaces(XElement root)
    {
        var used = root.DescendantsAndSelf()
            .SelectMany(e => e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Name.Namespace).Prepend(e.Name.Namespace))
            .Where(ns => ns != XNamespace.None && ns != XNamespace.Xml)
            .Distinct()
            .ToList();
        var configured = _options.Prefixes.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
        // A generated nsN never takes a prefix the JSON or the options give to another namespace.
        var taken = new HashSet<string>(_declared.Values.Concat(configured.Values).Append("xsi"), StringComparer.Ordinal);
        var next = 0;
        var declarations = new List<XAttribute>();
        foreach (var ns in used)
        {
            string prefix;
            if (ns == XmlToJson.Xsi)
                prefix = "xsi";
            else if (_declared.TryGetValue(ns.NamespaceName, out var fromJson))
                prefix = fromJson;
            else if (configured.TryGetValue(ns.NamespaceName, out var fromOptions))
                prefix = fromOptions;
            else
                while (!taken.Add(prefix = "ns" + next++)) { }
            declarations.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }
        root.ReplaceAttributes(declarations.Concat(root.Attributes().ToList()));
    }

    private static string LocalPart(string name) => name.Contains(':') ? name[(name.IndexOf(':') + 1)..] : name;
}
