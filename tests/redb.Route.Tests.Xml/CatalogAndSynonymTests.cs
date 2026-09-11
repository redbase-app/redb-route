using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Diagnostics;
using redb.Route.Xml;

namespace redb.Route.Tests.Xml;

/// <summary>Fast/Safe processing mode of the test connector.</summary>
public enum CatMode { Fast, Safe }

/// <summary>The options of the test connector — an ordinary <see cref="EndpointOptions"/>.</summary>
public sealed class CatEndpointOptions : EndpointOptions
{
    public string? GroupId { get; set; }
    public int Retries { get; set; } = 3;
    [Sensitive] public string? Password { get; set; }
    public CatMode Mode { get; set; } = CatMode.Fast;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public override void Validate() { }
}

/// <summary>
/// The test connector of the Ф4 anti-copy-paste criterion: Options, a Scheme, and ONE line of
/// §7.2 metadata — and it must appear in the structured form, the catalog and the generated
/// XSD without a single line of XML-specific code.
/// </summary>
public sealed class CatComponent : ComponentBase
{
    public override string Scheme => "cat";
    public override string? StructuredPathSynonym => "queue";
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("registration-only test connector");
}

/// <summary>A connector whose path is a text (the sql shape).</summary>
public sealed class SqlishComponent : ComponentBase
{
    public override string Scheme => "sqlish";
    public override bool PathIsText => true;
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("registration-only test connector");
}

/// <summary>Options for the dog family — named after the BASE component.</summary>
public sealed class DogEndpointOptions : EndpointOptions
{
    public bool Secure { get; set; }
    public override void Validate() { }
}

/// <summary>The base of the Https shape.</summary>
public class DogComponent : ComponentBase
{
    public override string Scheme => "dog";
    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("registration-only test connector");
}

/// <summary>
/// The Https shape: a DERIVED component (HttpsComponent : HttpComponent) with no options
/// class of its own — it must inherit the base's options in the catalog.
/// </summary>
public sealed class DogsComponent : DogComponent
{
    public override string Scheme => "dogs";
}

/// <summary>
/// Route-XML Ф4 §4 + §7.2 metadata: the component catalog is reflection over what a connector
/// already has, the path synonym is read FROM the component, and the catalog-aware XSD makes
/// the structured endpoint children strict and enum-hinted.
/// </summary>
public class CatalogAndSynonymTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public CatalogAndSynonymTests()
    {
        _context.AddComponent(new CatComponent());
        _context.AddComponent(new SqlishComponent());
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<CatalogComponent> Catalog()
        => ComponentCatalog.Build([new CatComponent(), new SqlishComponent()]);

    // ── the catalog is reflection, not a hand-kept list ──────────────────────

    [Fact]
    public void DerivedComponent_InheritsTheBaseOptions_TheHttpsShape()
    {
        // The live catalog run over the worker caught this: HttpsComponent : HttpComponent
        // has no HttpsEndpointOptions — the naming walk must climb the base chain.
        var catalog = ComponentCatalog.Build([new DogsComponent()]);

        var dogs = catalog.Single(c => c.Scheme == "dogs");
        dogs.OptionsType.Should().Be(typeof(DogEndpointOptions).FullName);
        dogs.Options.Should().Contain(o => o.Name == "Secure");
    }

    [Fact]
    public void Catalog_DescribesTheConnector_FromItsOwnCode()
    {
        var cat = Catalog().Single(c => c.Scheme == "cat");

        cat.PathSynonym.Should().Be("queue");
        cat.PathIsText.Should().BeFalse();
        cat.OptionsType.Should().Be(typeof(CatEndpointOptions).FullName);
        cat.Package.Should().Be(typeof(CatComponent).Assembly.GetName().Name);

        var options = cat.Options.ToDictionary(o => o.Name);
        options["Retries"].Should().BeEquivalentTo(new CatalogOption("Retries", "int", "3", false, null));
        options["Password"].Sensitive.Should().BeTrue("the [Sensitive] mark must reach the property panel");
        options["Mode"].Type.Should().Be("enum");
        options["Mode"].EnumValues.Should().Equal("Fast", "Safe");
        options["Mode"].Default.Should().Be("Fast");
        options["Timeout"].Type.Should().Be("duration");
        options["Timeout"].Default.Should().Be("00:00:05");
    }

    [Fact]
    public void CatalogJson_IsCamelCased_AndMarksSecrets()
    {
        var json = ComponentCatalog.ToJson(Catalog());

        json.Should().Contain("\"scheme\": \"cat\"")
            .And.Contain("\"pathSynonym\": \"queue\"")
            .And.Contain("\"sensitive\": true");
    }

    // ── the synonym comes from the component, not from a list ────────────────

    [Fact]
    public void StructuredForm_UsesTheComponentsPathSynonym()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="syn">
                <from uri="direct://syn-in"/>
                <to><cat queue="orders" retries="5"/></to>
              </route>
            </routes>
            """);

        var text = RouteDescriber.Describe(Examples.ExampleHarness.Definitions(_context));

        text.Should().Contain("to uri=cat://orders?retries=5",
            "queue= is the component's declared synonym for the path part");
    }

    [Fact]
    public void SynonymAndPath_Together_IsASchemaError()
    {
        var act = () => _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="syn-double">
                <from uri="direct://syn-double-in"/>
                <to><cat path="a" queue="b"/></to>
              </route>
            </routes>
            """);

        act.Should().Throw<XmlRouteException>().WithMessage("*carries its path more than once*");
    }

    [Fact]
    public async Task PathIsText_TakesThePathFromContent_AndRefusesTheAttribute()
    {
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="txt">
                <from uri="direct://txt-in"/>
                <to><sqlish><![CDATA[SELECT 1]]></sqlish></to>
              </route>
            </routes>
            """);
        RouteDescriber.Describe(Examples.ExampleHarness.Definitions(_context))
            .Should().Contain("to uri=sqlish://SELECT 1");

        await using var other = new RouteContext();
        other.AddComponent(new SqlishComponent());
        var act = () => other.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="txt-bad">
                <from uri="direct://txt-bad-in"/>
                <to><sqlish path="SELECT 1"/></to>
              </route>
            </routes>
            """);
        act.Should().Throw<XmlRouteException>()
            .WithMessage("*path of <sqlish> is a text*element content*");
    }

    // ── the catalog-aware XSD: strict scheme elements with typed options ─────

    [Fact]
    public void CatalogAwareXsd_ValidatesTheKnownScheme_AndRejectsTheUnknown()
    {
        var registry = ElementRegistry.CreateDefault();
        var catalog = Catalog();

        XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="ok">
                <from uri="direct://in"/>
                <to><cat queue="orders" retries="5" mode="Safe"/></to>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), registry, catalog)
            .Should().BeEmpty();

        XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad">
                <from uri="direct://in"/>
                <to><dog kennel="x"/></to>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), registry, catalog)
            .Should().NotBeEmpty("a scheme outside the catalog is not a valid endpoint child");
    }

    [Fact]
    public void CatalogAwareXsd_EnforcesEnumOptions()
    {
        var problems = XmlRouteSchema.Validate(XDocument.Parse("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="bad-mode">
                <from uri="direct://in"/>
                <to><cat queue="q" mode="Sideways"/></to>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog());

        problems.Should().Contain(p => p.Message.Contains("Sideways"),
            "enum options become xs:enumeration — the editor offers Fast/Safe");
    }
}
