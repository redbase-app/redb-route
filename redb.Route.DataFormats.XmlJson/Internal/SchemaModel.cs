using System.Globalization;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace redb.Route.DataFormats.XmlJson.Internal;

/// <summary>What the schema says about one element of a document: its declaration and whether it repeats.</summary>
/// <param name="Type">The element's type; <c>null</c> under a wildcard with no global declaration (nothing is known).</param>
/// <param name="Repeats">Whether the content of its parent allows it more than once.</param>
internal sealed record SchemaElement(XmlSchemaType? Type, bool Repeats);

/// <summary>One place in the content of a complex type, in declaration order.</summary>
/// <param name="Name">The element's qualified name; empty for a wildcard.</param>
/// <param name="Declaration">The element declaration; <c>null</c> for a wildcard.</param>
/// <param name="Repeats">Whether the place allows the element more than once.</param>
/// <param name="Order">Position in the content, for writing children in schema order.</param>
/// <param name="Branch">The single-occurrence choice and the branch the place sits in, if any.</param>
/// <param name="Wildcard">Whether the place is an <c>xs:any</c>.</param>
internal sealed record ContentSlot(XmlQualifiedName Name, XmlSchemaElement? Declaration, bool Repeats, int Order,
    (int Choice, int Branch)? Branch, bool Wildcard);

/// <summary>
/// The knowledge xmljson takes from a compiled <see cref="XmlSchemaSet"/>. The compiler has already resolved imports,
/// includes, groups, extensions and restrictions; this walks the compiled content particles itself, because the
/// repetition of an element <c>ref</c> sits on the particle, not on the global declaration a validating reader
/// reports. Lookups are lazy and cached per parent type, so recursive types need nothing special.
/// </summary>
internal sealed class SchemaModel
{
    private readonly XmlSchemaSet _set;
    private readonly Dictionary<XmlQualifiedName, List<XmlSchemaElement>> _substitutes = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(XmlSchemaType Parent, XmlQualifiedName Child), SchemaElement?> _children =
        new(new ParentChildComparer());

    public SchemaModel(XmlSchemaSet set)
    {
        if (!set.IsCompiled)
            set.Compile();
        _set = set;
        foreach (XmlSchemaElement element in set.GlobalElements.Values)
        {
            if (element.SubstitutionGroup.IsEmpty)
                continue;
            if (!_substitutes.TryGetValue(element.SubstitutionGroup, out var members))
                _substitutes[element.SubstitutionGroup] = members = [];
            members.Add(element);
        }
    }

    private static XmlQualifiedName QName(XName name) => new(name.LocalName, name.NamespaceName);

    /// <summary>The root: a global element, or an error naming the root and the namespace.</summary>
    public SchemaElement Root(XName name)
        => _set.GlobalElements[QName(name)] is XmlSchemaElement element
            ? new SchemaElement(element.ElementSchemaType, Repeats: false)
            : throw new InvalidOperationException(
                $"xmljson: the root element '{name.LocalName}'{NamespaceText(name)} is not a global element of the schema.");

    /// <summary>The global elements whose local name is <paramref name="local"/>, for a JSON key that carries no namespace.</summary>
    public IReadOnlyList<XmlSchemaElement> GlobalsNamed(string local)
        => _set.GlobalElements.Values.Cast<XmlSchemaElement>().Where(e => e.QualifiedName.Name == local).ToList();

    /// <summary>
    /// The places of the content of <paramref name="type"/>, in the order the content declares them: what JSON to XML
    /// needs to write children in schema order and to tell the branches of a choice apart. Members of a substitution
    /// group take the place of their head.
    /// </summary>
    public IReadOnlyList<ContentSlot> Slots(XmlSchemaType? type)
    {
        var slots = new List<ContentSlot>();
        if (type is not XmlSchemaComplexType complex)
            return slots;
        var choices = 0;
        Walk(complex.ContentTypeParticle, false, null);
        return slots;

        void Walk(XmlSchemaParticle? particle, bool repeated, (int Choice, int Branch)? branch)
        {
            if (particle is null || particle.MaxOccurs == 0)
                return;
            var here = repeated || particle.MaxOccurs > 1;
            switch (particle)
            {
                case XmlSchemaElement element:
                    foreach (var declaration in Members(element))
                        slots.Add(new ContentSlot(declaration.QualifiedName, declaration, here, slots.Count, branch, Wildcard: false));
                    break;
                case XmlSchemaChoice choice when !here:
                    var id = ++choices;
                    var index = 0;
                    foreach (XmlSchemaParticle item in choice.Items)
                        Walk(item, here, (id, index++));
                    break;
                case XmlSchemaGroupBase group:
                    foreach (XmlSchemaParticle item in group.Items)
                        Walk(item, here, branch);
                    break;
                case XmlSchemaGroupRef groupRef:
                    Walk(groupRef.Particle, here, branch);
                    break;
                case XmlSchemaAny:
                    slots.Add(new ContentSlot(XmlQualifiedName.Empty, null, here, slots.Count, branch, Wildcard: true));
                    break;
            }
        }
    }

    private IEnumerable<XmlSchemaElement> Members(XmlSchemaElement element)
    {
        if (!element.IsAbstract)
            yield return element;
        if (_substitutes.TryGetValue(element.QualifiedName, out var members))
            foreach (var member in members)
                foreach (var nested in Members(member))
                    yield return nested;
    }

    /// <summary>The attribute of <paramref name="type"/> whose local name is <paramref name="local"/>, or <c>null</c>.</summary>
    public static XmlSchemaAttribute? AttributeNamed(XmlSchemaType? type, string local)
        => type is XmlSchemaComplexType complex
            ? complex.AttributeUses.Values.Cast<XmlSchemaAttribute>().FirstOrDefault(a => a.QualifiedName.Name == local)
            : null;

    /// <summary>Whether <paramref name="type"/> accepts attributes it does not declare (<c>xs:anyAttribute</c>).</summary>
    public static bool HasAnyAttribute(XmlSchemaType? type)
        => type is XmlSchemaComplexType { AttributeWildcard: not null };

    /// <summary>
    /// The child <paramref name="name"/> in the content of <paramref name="parent"/>. Under a wildcard
    /// (<c>xs:any</c>) a global declaration of that name is used when there is one, else nothing is known (the type is
    /// <c>null</c>). <c>null</c> when the parent's content has no place for it.
    /// </summary>
    public SchemaElement? Child(SchemaElement parent, XName name)
    {
        if (parent.Type is not XmlSchemaComplexType complex)
            return parent.Type is null ? new SchemaElement(null, false) : null;
        var key = (complex, QName(name));
        return _children.GetOrAdd(key, k => Find((XmlSchemaComplexType)k.Parent, k.Child));
    }

    private SchemaElement? Find(XmlSchemaComplexType parent, XmlQualifiedName name)
    {
        var matches = 0;
        var repeats = false;
        XmlSchemaType? type = null;
        var wildcard = false;
        var wildcardRepeats = false;
        Walk(parent.ContentTypeParticle, false);

        if (matches > 0)
            return new SchemaElement(type, repeats || matches > 1);
        if (!wildcard)
            return null;
        return _set.GlobalElements[name] is XmlSchemaElement global
            ? new SchemaElement(global.ElementSchemaType, wildcardRepeats)
            : new SchemaElement(null, wildcardRepeats);

        void Walk(XmlSchemaParticle? particle, bool repeated)
        {
            if (particle is null || particle.MaxOccurs == 0)
                return;
            var here = repeated || particle.MaxOccurs > 1;
            switch (particle)
            {
                case XmlSchemaElement element:
                    var declaration = Matching(element, name);
                    if (declaration is not null)
                    {
                        matches++;
                        repeats |= here;
                        type ??= declaration.ElementSchemaType;
                    }
                    break;
                case XmlSchemaGroupBase group:
                    foreach (XmlSchemaParticle item in group.Items)
                        Walk(item, here);
                    break;
                case XmlSchemaGroupRef groupRef:
                    Walk(groupRef.Particle, here);
                    break;
                case XmlSchemaAny:
                    wildcard = true;
                    wildcardRepeats |= here;
                    break;
            }
        }
    }

    /// <summary>The declaration <paramref name="element"/> stands for under <paramref name="name"/>: itself, or a member of its substitution group.</summary>
    private XmlSchemaElement? Matching(XmlSchemaElement element, XmlQualifiedName name)
    {
        if (element.QualifiedName == name)
            return element;
        if (!_substitutes.TryGetValue(element.QualifiedName, out var members))
            return null;
        foreach (var member in members)
            if (Matching(member, name) is { } found)
                return found;
        return null;
    }

    /// <summary>Whether <paramref name="type"/> holds element content (as opposed to text): its value is an object even when empty.</summary>
    public static bool IsComplexContent(XmlSchemaType? type)
        => type is XmlSchemaComplexType { ContentType: XmlSchemaContentType.ElementOnly or XmlSchemaContentType.Empty or XmlSchemaContentType.Mixed };

    /// <summary>The datatype of an element's text: a simple type, or a complex type with simple content.</summary>
    public static XmlSchemaDatatype? TextDatatype(XmlSchemaType? type) => type switch
    {
        XmlSchemaSimpleType simple => simple.Datatype,
        XmlSchemaComplexType { ContentType: XmlSchemaContentType.TextOnly } complex => complex.Datatype,
        _ => null,
    };

    /// <summary>The datatype of the attribute <paramref name="name"/> on an element of <paramref name="type"/>.</summary>
    public static XmlSchemaDatatype? AttributeDatatype(XmlSchemaType? type, XName name)
        => type is XmlSchemaComplexType complex && complex.AttributeUses[QName(name)] is XmlSchemaAttribute attribute
            ? attribute.AttributeSchemaType?.Datatype
            : null;

    /// <summary>
    /// The JSON value of <paramref name="text"/> under <paramref name="datatype"/>: a number for the numeric types, a
    /// boolean for <c>xs:boolean</c>, the text for every other type. A text that is not of its type is an error naming
    /// <paramref name="where"/>.
    /// </summary>
    public static JsonNode? Typed(string text, XmlSchemaDatatype? datatype, string where)
    {
        if (datatype is null || datatype.Variety != XmlSchemaDatatypeVariety.Atomic)
            return JsonValue.Create(text);
        var trimmed = text.Trim();
        switch (datatype.TypeCode)
        {
            case XmlTypeCode.Boolean:
                return trimmed switch
                {
                    "true" or "1" => JsonValue.Create(true),
                    "false" or "0" => JsonValue.Create(false),
                    _ => throw NotOfType(text, "xs:boolean", where),
                };
            case XmlTypeCode.Double or XmlTypeCode.Float:
                if (trimmed is "INF" or "-INF" or "NaN")
                    return JsonValue.Create(trimmed); // not a JSON number: kept as the schema writes it
                return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    ? JsonValue.Create(real)
                    : throw NotOfType(text, datatype.TypeCode == XmlTypeCode.Double ? "xs:double" : "xs:float", where);
            case XmlTypeCode.Decimal or XmlTypeCode.Integer or XmlTypeCode.NonPositiveInteger or XmlTypeCode.NegativeInteger
                or XmlTypeCode.Long or XmlTypeCode.Int or XmlTypeCode.Short or XmlTypeCode.Byte
                or XmlTypeCode.NonNegativeInteger or XmlTypeCode.UnsignedLong or XmlTypeCode.UnsignedInt
                or XmlTypeCode.UnsignedShort or XmlTypeCode.UnsignedByte or XmlTypeCode.PositiveInteger:
                return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number)
                    ? JsonValue.Create(number)
                    : throw NotOfType(text, "xs:" + datatype.TypeCode.ToString().ToLowerInvariant(), where);
            default:
                return JsonValue.Create(text);
        }
    }

    private static InvalidOperationException NotOfType(string text, string type, string where)
        => new($"xmljson: {where} holds '{text}', which is not an {type} as the schema declares.");

    private static string NamespaceText(XName name)
        => name.Namespace == XNamespace.None ? " (no namespace)" : $" in namespace '{name.NamespaceName}'";

    private sealed class ParentChildComparer : IEqualityComparer<(XmlSchemaType Parent, XmlQualifiedName Child)>
    {
        public bool Equals((XmlSchemaType Parent, XmlQualifiedName Child) x, (XmlSchemaType Parent, XmlQualifiedName Child) y)
            => ReferenceEquals(x.Parent, y.Parent) && x.Child == y.Child;

        public int GetHashCode((XmlSchemaType Parent, XmlQualifiedName Child) obj)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Parent), obj.Child);
    }
}
