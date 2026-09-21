using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace redb.Route.Xml;

/// <summary>
/// The XSD of the route format, GENERATED from the element registry (Ф4 §2–3): the parser, the
/// schema and the catalog read one list, so they cannot drift apart. The schema covers the
/// container skeleton (<c>&lt;routes&gt;</c>/<c>&lt;context&gt;</c>, <c>&lt;bean&gt;</c>,
/// <c>&lt;route&gt;</c>) plus every registered element with its attributes, enum hints and
/// content model. Package contributions appear automatically; the endpoint children of the
/// structured form (§7.2) are open content until a component catalog supplies the scheme
/// elements. One namespace, <c>urn:redb:route:1.0</c> (Р21); foreign-namespace attributes are
/// tolerated (<c>anyAttribute ##other lax</c>, Р9), foreign elements are not.
/// </summary>
public static class XmlRouteSchema
{
    private const string Ns = XmlRouteLoader.Namespace;
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ElementRegistry, XmlSchemaSet> Cache = new();

    private static readonly Lazy<XmlSchemaSet> Core = new(() => For(ElementRegistry.CreateDefault()));

    /// <summary>The compiled schema of the core element set — generated and compiled once.</summary>
    public static XmlSchemaSet CoreSet => Core.Value;

    /// <summary>Validates against an already-compiled set (the loader's hot path).</summary>
    public static IReadOnlyList<(int Line, int Column, string Message)> Validate(
        XDocument document, XmlSchemaSet schema)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schema);
        var problems = new List<(int, int, string)>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schema };
        settings.ValidationEventHandler += (_, e) =>
            problems.Add((e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, e.Message));
        using var reader = XmlReader.Create(document.CreateReader(), settings);
        while (reader.Read()) { }
        return problems;
    }

    /// <summary>The compiled schema for a registry (cached per registry instance).</summary>
    public static XmlSchemaSet For(ElementRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return Cache.GetValue(registry, r =>
        {
            var set = new XmlSchemaSet();
            using var reader = Generate(r).CreateReader();
            set.Add(Ns, reader);
            set.Compile();
            return set;
        });
    }

    /// <summary>
    /// Validates a document against the registry's schema. Returns one positioned message per
    /// problem; an empty list means the document is schema-valid.
    /// </summary>
    public static IReadOnlyList<(int Line, int Column, string Message)> Validate(
        XDocument document, ElementRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(document);
        var schema = For(registry);
        var problems = new List<(int, int, string)>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schema };
        settings.ValidationEventHandler += (_, e) =>
            problems.Add((e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, e.Message));
        using var reader = XmlReader.Create(document.CreateReader(), settings);
        while (reader.Read()) { }
        return problems;
    }

    /// <summary>
    /// Validates against a schema that also knows the component catalog: the §7.2 endpoint
    /// children become a STRICT set of scheme elements with typed, enum-hinted options.
    /// </summary>
    public static IReadOnlyList<(int Line, int Column, string Message)> Validate(
        XDocument document, ElementRegistry registry, IReadOnlyList<CatalogComponent> catalog)
    {
        ArgumentNullException.ThrowIfNull(document);
        var set = new XmlSchemaSet();
        using (var reader = Generate(registry, catalog).CreateReader())
            set.Add(Ns, reader);
        set.Compile();
        var problems = new List<(int, int, string)>();
        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = set };
        settings.ValidationEventHandler += (_, e) =>
            problems.Add((e.Exception?.LineNumber ?? 0, e.Exception?.LinePosition ?? 0, e.Message));
        using var validating = XmlReader.Create(document.CreateReader(), settings);
        while (validating.Read()) { }
        return problems;
    }

    /// <summary>Generates the schema document for a registry — also the file the tooling ships.</summary>
    public static XDocument Generate(ElementRegistry registry, IReadOnlyList<CatalogComponent>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var contributions = registry.Contributions
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();
        var stepNames = contributions
            .Where(c => c.Kind is XmlElementKind.Step or XmlElementKind.Scope or XmlElementKind.Branching)
            .Select(c => c.Name)
            .ToList();

        var schema = new XElement(Xs + "schema",
            new XAttribute("targetNamespace", Ns),
            new XAttribute("elementFormDefault", "qualified"),
            new XAttribute(XNamespace.Xmlns + "xs", Xs),
            new XAttribute(XNamespace.Xmlns + "r", Ns));

        // The step group: every registered step/scope/branching element.
        schema.Add(new XElement(Xs + "group",
            new XAttribute("name", "step"),
            new XElement(Xs + "choice",
                stepNames.Select(n => new XElement(Xs + "element", new XAttribute("ref", "r:" + n))))));

        foreach (var contribution in contributions)
            schema.Add(GlobalElement(contribution.Spec, catalog));

        schema.Add(BeanElement());
        schema.Add(RouteElement(catalog));
        schema.Add(RoutesElement(contributions));
        schema.Add(ContextElement(contributions));
        return new XDocument(schema);
    }

    // ── the container skeleton (owned by the loader, versioned with it) ──────

    private static XElement RoutesElement(IReadOnlyList<IXmlElementContribution> contributions)
    {
        var containerHandlers = new[] { "onException", "intercept", "interceptFrom", "interceptSendToEndpoint", "onCompletion" }
            .Where(n => contributions.Any(c => c.Name == n));
        var topLevel = contributions
            .Where(c => c.Kind == XmlElementKind.TopLevel)
            .Select(c => c.Name);
        return new XElement(Xs + "element", new XAttribute("name", "routes"),
            new XElement(Xs + "complexType",
                new XElement(Xs + "choice",
                    new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                    new XElement(Xs + "element", new XAttribute("ref", "r:bean")),
                    new XElement(Xs + "element", new XAttribute("ref", "r:route")),
                    containerHandlers.Select(n => new XElement(Xs + "element", new XAttribute("ref", "r:" + n))),
                    topLevel.Select(n => new XElement(Xs + "element", new XAttribute("ref", "r:" + n)))),
                new XElement(Xs + "attribute", new XAttribute("name", "version"), new XAttribute("type", "xs:string")),
                ForeignAttributes()));
    }

    private static XElement ContextElement(IReadOnlyList<IXmlElementContribution> contributions)
        => new(Xs + "element", new XAttribute("name", "context"),
            new XElement(Xs + "complexType",
                new XElement(Xs + "choice",
                    new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                    new XElement(Xs + "element", new XAttribute("name", "components"),
                        new XElement(Xs + "complexType",
                            new XElement(Xs + "sequence",
                                new XElement(Xs + "element", new XAttribute("name", "component"),
                                    new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                                    new XElement(Xs + "complexType",
                                        Attribute(new AttributeSpec("type", AttributeType.TypeName, Required: true)),
                                        ForeignAttributes()))))),
                    new XElement(Xs + "element", new XAttribute("ref", "r:bean")),
                    new XElement(Xs + "element", new XAttribute("name", "onInit"),
                        new XElement(Xs + "complexType", StepGroup(), ForeignAttributes())),
                    contributions
                        .Where(c => c.Kind == XmlElementKind.ContextLevel)
                        .Select(c => new XElement(Xs + "element", new XAttribute("ref", "r:" + c.Name))))));

    private static XElement BeanElement()
        => new(Xs + "element", new XAttribute("name", "bean"),
            new XElement(Xs + "complexType",
                new XElement(Xs + "choice",
                    new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                    // value= for a scalar, one nested anonymous <bean> for a property that holds
                    // an object (a certificate, credentials); the parser rejects both at once.
                    new XElement(Xs + "element", new XAttribute("name", "property"),
                        new XElement(Xs + "complexType",
                            new XElement(Xs + "choice",
                                new XAttribute("minOccurs", "0"),
                                new XElement(Xs + "element", new XAttribute("ref", "r:bean"))),
                            Attribute(new AttributeSpec("key", AttributeType.String, Required: true)),
                            Attribute(new AttributeSpec("value", AttributeType.String)),
                            ForeignAttributes())),
                    new XElement(Xs + "element", new XAttribute("name", "constructorArg"),
                        new XElement(Xs + "complexType",
                            new XElement(Xs + "choice",
                                new XAttribute("minOccurs", "0"),
                                new XElement(Xs + "element", new XAttribute("ref", "r:bean"))),
                            Attribute(new AttributeSpec("value", AttributeType.String)),
                            ForeignAttributes()))),
                // name is required on a registered bean and absent on a nested anonymous one —
                // context-dependent, so the parser enforces it and the schema stays permissive.
                Attribute(new AttributeSpec("name", AttributeType.String)),
                Attribute(new AttributeSpec("type", AttributeType.TypeName, Required: true)),
                // A public static creator instead of a constructor; constructorArg values are its
                // arguments (X509CertificateLoader.LoadPkcs12FromFile is the shape this serves).
                Attribute(new AttributeSpec("factoryMethod", AttributeType.String)),
                ForeignAttributes()));

    private static XElement RouteElement(IReadOnlyList<CatalogComponent>? catalog)
        => new(Xs + "element", new XAttribute("name", "route"),
            new XElement(Xs + "complexType",
                new XElement(Xs + "sequence",
                    new XElement(Xs + "element", new XAttribute("name", "from"),
                        new XElement(Xs + "complexType",
                            EndpointContent(catalog),
                            Attribute(new AttributeSpec("uri", AttributeType.Uri)),
                            ForeignAttributes())),
                    StepGroup()),
                Attribute(new AttributeSpec("id", AttributeType.String)),
                Attribute(new AttributeSpec("description", AttributeType.String)),
                Attribute(new AttributeSpec("autoStart", AttributeType.Bool)),
                Attribute(new AttributeSpec("cluster", AttributeType.Bool)),
                Attribute(new AttributeSpec("messageHistory", AttributeType.Bool)),
                Attribute(new AttributeSpec("processingTimeout", AttributeType.Duration)),
                Attribute(new AttributeSpec("routePolicy", AttributeType.Reference)),
                Attribute(new AttributeSpec("enabled", AttributeType.String)),
                ForeignAttributes()));

    // ── one element from its spec ────────────────────────────────────────────

    private static XElement GlobalElement(ElementSpec spec, IReadOnlyList<CatalogComponent>? catalog)
        => new(Xs + "element", new XAttribute("name", spec.Name), ComplexType(spec, catalog));

    private static XElement ComplexType(ElementSpec spec, IReadOnlyList<CatalogComponent>? catalog = null)
    {
        var type = new XElement(Xs + "complexType");
        if (spec.AllowsTextContent)
            type.Add(new XAttribute("mixed", "true"));

        var contentParticles = new List<XElement>();
        foreach (var child in spec.Children)
            contentParticles.Add(new XElement(Xs + "element",
                new XAttribute("name", child.Name), ComplexType(child)));
        if (spec.AllowsSteps)
            contentParticles.Add(new XElement(Xs + "group", new XAttribute("ref", "r:step")));
        if (spec.TakesEndpoint)
            contentParticles.AddRange(EndpointParticles(catalog));

        if (contentParticles.Count > 0)
        {
            type.Add(new XElement(Xs + "choice",
                new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                contentParticles));
        }

        foreach (var attribute in spec.Attributes)
            type.Add(Attribute(attribute));
        // Р12: id/description on every element (unless the spec already claims the names).
        if (spec.Attributes.All(a => a.Name != "id"))
            type.Add(Attribute(new AttributeSpec("id", AttributeType.String)));
        if (spec.Attributes.All(a => a.Name != "description"))
            type.Add(Attribute(new AttributeSpec("description", AttributeType.String)));
        type.Add(ForeignAttributes());
        return type;
    }

    private static XElement StepGroup()
        => new(Xs + "group", new XAttribute("ref", "r:step"),
            new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"));

    /// <summary>
    /// The §7.2 endpoint children: open lax content without a catalog; with one — the STRICT
    /// set of scheme elements, each with its typed options, path/synonym attributes and open
    /// family/text-option children.
    /// </summary>
    private static IReadOnlyList<XElement> EndpointParticles(IReadOnlyList<CatalogComponent>? catalog)
    {
        if (catalog is null)
        {
            return [new XElement(Xs + "any",
                new XAttribute("namespace", "##targetNamespace"),
                new XAttribute("processContents", "lax"))];
        }
        return [.. catalog
            .OrderBy(c => c.Scheme, StringComparer.Ordinal)
            .Select(SchemeElement)];
    }

    private static XElement EndpointContent(IReadOnlyList<CatalogComponent>? catalog)
        => new(Xs + "choice",
            new XAttribute("minOccurs", "0"),
            EndpointParticles(catalog));

    private static XElement SchemeElement(CatalogComponent component)
    {
        var type = new XElement(Xs + "complexType",
            new XAttribute("mixed", "true"),
            // Family entries (<param name=… value=…/>) and long text options are open children.
            new XElement(Xs + "choice",
                new XAttribute("minOccurs", "0"), new XAttribute("maxOccurs", "unbounded"),
                new XElement(Xs + "any",
                    new XAttribute("namespace", "##targetNamespace"),
                    new XAttribute("processContents", "lax"))));
        // One declaration per attribute name, or the schema does not compile at all ("the
        // attribute 'queue' already exists"): a component whose path synonym is ALSO an option
        // — seda's queue, amqp's path — would declare it twice. The option wins, it carries the
        // type and the enum values; the synonym only names the path.
        var declared = new HashSet<string>(component.Options.Select(o => CamelCase(o.Name)), StringComparer.Ordinal);
        if (!component.PathIsText)
        {
            if (declared.Add("path"))
                type.Add(Attribute(new AttributeSpec("path", AttributeType.String)));
            if (component.PathSynonym is { Length: > 0 } synonym && declared.Add(synonym))
                type.Add(Attribute(new AttributeSpec(synonym, AttributeType.String)));
        }
        foreach (var option in component.Options)
        {
            type.Add(Attribute(new AttributeSpec(
                CamelCase(option.Name),
                option.Type switch
                {
                    "bool" => AttributeType.Bool,
                    "int" => AttributeType.Int,
                    "long" => AttributeType.Long,
                    "double" => AttributeType.Double,
                    "duration" => AttributeType.Duration,
                    "enum" => AttributeType.Enum,
                    _ => AttributeType.String,
                },
                EnumValues: option.EnumValues)));
        }
        // Unknown unqualified attributes stay legal — the URI form's UnmappedParameters rule.
        type.Add(new XElement(Xs + "anyAttribute",
            new XAttribute("namespace", "##any"),
            new XAttribute("processContents", "lax")));
        return new XElement(Xs + "element", new XAttribute("name", component.Scheme), type);
    }

    private static string CamelCase(string name)
        => name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name[1..] : name;

    private static XElement Attribute(AttributeSpec spec)
    {
        var attribute = new XElement(Xs + "attribute", new XAttribute("name", spec.Name));
        if (spec.Required)
            attribute.Add(new XAttribute("use", "required"));
        if (spec.Type is AttributeType.Enum && spec.EnumValues is { Count: > 0 } values)
        {
            attribute.Add(new XElement(Xs + "simpleType",
                new XElement(Xs + "restriction", new XAttribute("base", "xs:string"),
                    values.Select(v => new XElement(Xs + "enumeration", new XAttribute("value", v))))));
        }
        else
        {
            attribute.Add(new XAttribute("type", BuiltInType(spec.Type)));
        }
        return attribute;
    }

    private static string BuiltInType(AttributeType type) => type switch
    {
        AttributeType.Bool => "xs:boolean",
        AttributeType.Int => "xs:int",
        AttributeType.Long => "xs:long",
        AttributeType.Double => "xs:double",
        // Duration is hh:mm:ss(.fff), not the ISO xs:duration — a string to the schema.
        _ => "xs:string",
    };

    private static XElement ForeignAttributes()
        => new(Xs + "anyAttribute",
            new XAttribute("namespace", "##other"),
            new XAttribute("processContents", "lax"));
}
