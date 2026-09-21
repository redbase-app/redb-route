using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Xml;
using redb.Route.Xml.CoreElements;

namespace redb.Route.Tests.Xml;

/// <summary>
/// Route-XML Ф4 §2–3: the XSD is generated from the element registry, so the parser, the schema
/// and the catalog read one list and cannot drift apart — pinned by tests, not by discipline.
/// </summary>
public class XmlRouteSchemaTests
{
    private static readonly ElementRegistry Registry = ElementRegistry.CreateDefault();

    private static IReadOnlyList<(int Line, int Column, string Message)> Validate(string xml)
        => XmlRouteSchema.Validate(XDocument.Parse(xml, LoadOptions.SetLineInfo), Registry);

    // ── the catalog-aware schema ─────────────────────────────────────────────

    /// <summary>
    /// The shipped catalog XSD must COMPILE, or the editor binds a schema it cannot use. A
    /// component whose path synonym is also one of its options (seda's queue, amqp's path)
    /// declared that attribute twice — «the attribute 'queue' already exists», twelve times
    /// over the real catalog (found before 4.1.0, 2026-09-20).
    /// </summary>
    [Fact]
    public void CatalogSchema_WithAPathSynonymThatIsAlsoAnOption_Compiles()
    {
        var component = new CatalogComponent(
            "seda", [], "redb.Route", "redb.Route.Components.SedaEndpointOptions", "queue", false,
            [
                new CatalogOption("Queue", "string", null, false, null),
                new CatalogOption("Size", "int", "1000", false, null),
            ]);

        var schema = XmlRouteSchema.Generate(Registry, [component]).ToString();

        var errors = new List<string>();
        var set = new System.Xml.Schema.XmlSchemaSet();
        set.ValidationEventHandler += (_, e) => errors.Add(e.Message);
        set.Add("urn:redb:route:1.0", System.Xml.XmlReader.Create(new StringReader(schema)));
        set.Compile();

        errors.Should().BeEmpty("a generated schema that does not compile is no schema at all");
    }

    // ── the one-list guarantee ───────────────────────────────────────────────

    [Fact]
    public void AttributeType_KeepsItsNumericValues_AcrossReleases()
    {
        // Package contributions (Cache, Http, redb.Route.Core, ...) are compiled against this enum
        // and store the NUMBERS. Inserting a member in the middle shifts them: a released package's
        // Bool would read as whatever took its slot. New members go to the end, never between.
        ((int)AttributeType.String).Should().Be(0);
        ((int)AttributeType.Expression).Should().Be(1);
        ((int)AttributeType.Uri).Should().Be(2);
        ((int)AttributeType.Reference).Should().Be(3);
        ((int)AttributeType.TypeName).Should().Be(4);
        ((int)AttributeType.Bool).Should().Be(5);
        ((int)AttributeType.Int).Should().Be(6);
        ((int)AttributeType.Long).Should().Be(7);
        ((int)AttributeType.Double).Should().Be(8);
        ((int)AttributeType.Duration).Should().Be(9);
        ((int)AttributeType.Enum).Should().Be(10);
        ((int)AttributeType.TypeNameList).Should().Be(11);
    }

    [Fact]
    public void EveryCoreContribution_HasAPinnedSpec_WithMatchingNameAndKind()
    {
        foreach (var contribution in CoreContributions.All)
        {
            CoreContributions.Specs.Should().ContainKey(contribution.Name,
                $"the core element <{contribution.Name}> must declare its shape for the schema and the catalog");
            var spec = contribution.Spec;
            spec.Name.Should().Be(contribution.Name);
            spec.Kind.Should().Be(contribution.Kind,
                $"<{contribution.Name}>: the registered kind and the spec kind must agree");
        }
    }

    [Fact]
    public void EverySpecEntry_BelongsToARegisteredContribution()
    {
        var registered = CoreContributions.All.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        CoreContributions.Specs.Keys.Should().OnlyContain(name => registered.Contains(name),
            "a spec without a contribution is dead weight the schema would advertise falsely");
    }

    [Fact]
    public void TheSchema_Compiles_AndGenerationIsDeterministic()
    {
        XmlRouteSchema.For(Registry).Should().NotBeNull();
        XmlRouteSchema.Generate(Registry).ToString()
            .Should().Be(XmlRouteSchema.Generate(Registry).ToString());
    }

    // ── validation behavior the phase promises ───────────────────────────────

    [Fact]
    public void EveryShippedExample_IsSchemaValid()
    {
        foreach (var file in Directory.GetFiles(Examples.ExampleHarness.Dir, "*.route.xml"))
        {
            var problems = XmlRouteSchema.Validate(
                XDocument.Load(file, LoadOptions.SetLineInfo), Registry);
            problems.Should().BeEmpty($"{Path.GetFileName(file)} must validate against the generated schema");
        }
    }

    [Fact]
    public void UnknownElement_FailsValidation()
    {
        var problems = Validate("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad">
                <from uri="direct://in"/>
                <nosuch/>
              </route>
            </routes>
            """);

        problems.Should().NotBeEmpty();
        problems.Should().Contain(p => p.Message.Contains("nosuch"));
    }

    [Fact]
    public void ForeignNamespaceAttribute_IsAccepted()
    {
        var problems = Validate("""
            <routes xmlns="urn:redb:route:1.0" xmlns:x="urn:someone:else">
              <route id="ok">
                <from uri="direct://in"/>
                <setHeader name="a" value="1" x:hint="editor-only"/>
              </route>
            </routes>
            """);

        problems.Should().BeEmpty("Р9: a foreign-namespace attribute is tolerated (anyAttribute ##other lax)");
    }

    [Fact]
    public void EnumAttribute_OffersAndEnforcesItsValues()
    {
        var problems = Validate("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-level">
                <from uri="direct://in"/>
                <log level="Loud">text</log>
              </route>
            </routes>
            """);

        problems.Should().Contain(p => p.Message.Contains("Loud"),
            "a log level outside the enumeration must fail, which is what powers editor value hints");
    }

    [Fact]
    public void RequiredAttribute_IsEnforced()
    {
        var problems = Validate("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-throw">
                <from uri="direct://in"/>
                <throwException message="no type"/>
              </route>
            </routes>
            """);

        problems.Should().Contain(p => p.Message.Contains("type"));
    }

    [Fact]
    public void StructuredEndpointChild_IsOpenContent_UntilTheCatalogArrives()
    {
        var problems = Validate("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="structured">
                <from uri="direct://in"/>
                <to><kafka path="orders" groupId="svc"/></to>
              </route>
            </routes>
            """);

        problems.Should().BeEmpty("a scheme element inside an address step is lax content in the base schema");
    }

    [Fact]
    public void ExternalContribution_AppearsInTheSchema_Automatically()
    {
        var registry = ElementRegistry.CreateDefault([new StampSpecContribution()]);
        var problems = XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ext">
                <from uri="direct://in"/>
                <stamp value="x"/>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), registry);

        problems.Should().BeEmpty("a registered package element validates like a core one");
        XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ext"><from uri="direct://in"/><stamp value="x"/></route>
            </routes>
            """, LoadOptions.SetLineInfo), Registry)
            .Should().NotBeEmpty("without the package, its element is rejected by the schema too");
    }

    // ── the §3.4 load-time pass: schema findings join the parse, deduped ─────

    [Fact]
    public void AttributeTypo_IsCaughtAtLoad_ByTheSchemaPass()
    {
        using var context = new Route.Core.RouteContext();
        var act = () => context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="typo">
                <from uri="direct://in"/>
                <removeBody bogus="1"/>
              </route>
            </routes>
            """, "typo.xml");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*typo.xml(4,*[schema]*bogus*",
                "an unqualified attribute typo is exactly what the parser leaves to the schema");
    }

    [Fact]
    public void ParserAndSchema_DoNotDoubleReportTheSameLine()
    {
        using var context = new Route.Core.RouteContext();
        var act = () => context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="dedup">
                <from uri="direct://in"/>
                <setHeaderr name="x" value="1"/>
              </route>
            </routes>
            """);

        var errors = act.Should().Throw<XmlRouteException>().Which.Errors;
        errors.Should().ContainSingle("the parser's did-you-mean wins the line; the schema stays quiet on it")
            .Which.Should().Contain("Did you mean <setHeader>?").And.NotContain("[schema]");
    }

    [Fact]
    public async Task TheSwitch_TurnsTheSchemaPassOff()
    {
        await using var context = new Route.Core.RouteContext();
        var act = () => context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="off">
                <from uri="direct://in"/>
                <removeBody bogus="1"/>
              </route>
            </routes>
            """, options: new XmlRouteLoaderOptions { ValidateAgainstSchema = false });

        act.Should().NotThrow("a document carrying a newer schema's extensions may opt out");
    }

    // ── the §3.2 version policy on the namespace ─────────────────────────────

    [Fact]
    public void NewerMinorVersion_SaysWhatToUpdate()
    {
        using var context = new Route.Core.RouteContext();
        var act = () => context.AddXmlRoutesFromContent(
            """<routes xmlns="urn:redb:route:1.5"><route/></routes>""");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*needs format 1.5*supports up to 1.0*update the redb.Route.Xml package*");
    }

    [Fact]
    public void DifferentMajorVersion_IsADifferentFormat()
    {
        using var context = new Route.Core.RouteContext();
        var act = () => context.AddXmlRoutesFromContent(
            """<routes xmlns="urn:redb:route:2.0"><route/></routes>""");

        act.Should().Throw<XmlRouteException>()
            .WithMessage("*format 2.0*major version 1*different format*");
    }

    private sealed class StampSpecContribution : IXmlElementContribution
    {
        public string Name => "stamp";
        public XmlElementKind Kind => XmlElementKind.Step;
        public ElementSpec Spec => ElementSpec.Leaf("stamp", new AttributeSpec("value", AttributeType.String));
        public Abstractions.IRouteDefinition Apply(
            XElement element, Abstractions.IRouteDefinition current, XmlParseContext context)
            => current.SetHeader("stamped", context.Attr(element, "value") ?? "yes");
    }
}
