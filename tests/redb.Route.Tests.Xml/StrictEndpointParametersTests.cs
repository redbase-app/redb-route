using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Components;
using redb.Route.Components.Bean;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// A connector refuses a parameter it has no option for unless it is lenient
/// (<c>EndpointOptions.IsLenient</c>). Route-XML follows the same rule everywhere it reads an
/// endpoint before the route starts: the catalog carries it, the catalog schema refuses the
/// unknown attribute in the editor, the package gate refuses the unknown name at <c>check --bin</c>
/// — and a foreign-namespace attribute, which the format tolerates as metadata, is never an option.
/// </summary>
public sealed class StrictEndpointParametersTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-strict-" + Guid.NewGuid().ToString("N"));

    public StrictEndpointParametersTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>timer: is strict, bean: is lenient — both from the core, no connector package needed.</summary>
    private static readonly IReadOnlyList<CatalogComponent> Catalog =
        ComponentCatalog.Build([new TimerComponent(), new BeanComponent(), new DirectComponent()]);

    private static Type? Resolve(string typeName) => typeof(TimerComponent).Assembly.GetType(typeName);

    // ── the catalog ──────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_CarriesTheEnginesLenientFlag()
    {
        Catalog.Single(c => c.Scheme == "timer").Lenient.Should().BeFalse();
        Catalog.Single(c => c.Scheme == "bean").Lenient.Should().BeTrue();
        ComponentCatalog.ToJson(Catalog).Should().Contain("\"lenient\": false");
    }

    // ── the catalog schema ───────────────────────────────────────────────────

    private static IReadOnlyList<(int Line, int Column, string Message)> ValidateWithCatalog(string endpoint)
        => XmlRouteSchema.Validate(XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0" xmlns:ui="urn:example:ui">
              <route id="r">
                <from>{endpoint}</from>
                <to uri="direct://out"/>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog);

    [Fact]
    public void Schema_RefusesAnUnknownAttribute_OnAStrictConnector()
        => ValidateWithCatalog("""<timer path="tick" perod="1000"/>""")
            .Should().ContainSingle(p => p.Message.Contains("perod"));

    [Fact]
    public void Schema_KeepsAnUnknownAttribute_OnALenientConnector_AndAForeignOneEverywhere()
    {
        ValidateWithCatalog("""<bean path="b" anything="1"/>""").Should().BeEmpty();
        ValidateWithCatalog("""<timer path="tick" period="1000" ui:xy="10,20"/>""").Should().BeEmpty();
    }

    // ── the loader ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ForeignNamespaceAttribute_OnAStructuredEndpoint_IsNotAParameter()
    {
        // An editor mark (the graph's layout, a review note) in its own namespace: tolerated by
        // the format, and before this fix passed to the connector as 'xy' — which a strict
        // connector refuses when the route starts.
        _context.AddXmlRoutesFromContent("""
            <routes xmlns="urn:redb:route:1.0" xmlns:ui="urn:example:ui">
              <route id="strict-foreign">
                <from><timer path="strict-foreign" period="3600000" delay="3600000" ui:xy="10,20"/></from>
                <to uri="direct://strict-foreign-out"/>
              </route>
            </routes>
            """);

        var start = () => _context.Start();

        await start.Should().NotThrowAsync();
    }

    // ── the package gate ─────────────────────────────────────────────────────

    private string Project(string routeXml)
    {
        var dir = RouteProjectScaffold.Create(_root, "Strict" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), routeXml);
        return dir;
    }

    [Fact]
    public void Gate_RefusesAnUnknownName_InBothForms_InTheEnginesWords()
    {
        var dir = Project("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="uri-form">
                <from uri="timer://a?perod=1000"/>
                <to uri="direct://out"/>
              </route>
              <route id="structured-form">
                <from><timer path="b" period="1000" delya="5"/></from>
                <to uri="direct://out"/>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0", Resolve, catalog: Catalog).Errors.ToList();

        errors.Should().Contain(e => e.File == "routes/main.route.xml(3,6)"
                                     && e.Message.Contains("'perod' is not an option of the Timer endpoint")
                                     && e.Message.Contains("Did you mean 'period'?"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(7,")
                                     && e.Message.Contains("'delya' is not an option of the Timer endpoint"));
    }

    [Fact]
    public void Gate_PassesKnownNames_LenientConnectors_ForeignAttributes_AndRuntimeNames()
    {
        var dir = Project("""
            <routes xmlns="urn:redb:route:1.0" xmlns:ui="urn:example:ui">
              <route id="ok">
                <from><timer path="a" period="1000" ui:xy="1,2"/></from>
                <to uri="bean:#b?method=M&amp;anything=1"/>
                <toD uri="{{target.uri}}"/>
                <to uri="direct://out?{{extra}}=1"/>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "p", "1.0.0", Resolve, catalog: Catalog).Errors
            .Should().NotContain(e => e.Message.Contains("is not an option"));
    }

    [Fact]
    public void Gate_WithoutACatalog_LeavesParametersToTheEngine()
    {
        var dir = Project("""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from uri="timer://a?perod=1000"/>
                <to uri="direct://out"/>
              </route>
            </routes>
            """);

        RoutePackage.Check(dir, "p", "1.0.0", Resolve).Errors
            .Should().NotContain(e => e.Message.Contains("is not an option"));
    }
}
