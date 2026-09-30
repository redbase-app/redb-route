using System.Xml.Linq;
using FluentAssertions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>
/// An option only one side of an endpoint reads (<c>[EndpointRole]</c>) is refused by the
/// connector on the other side: <c>username</c> is what an http: producer sends, <c>inboundAuth</c>
/// is what an http: consumer requires. Route-XML reads the same declaration: the catalog carries
/// it, the editor schema refuses the option on the wrong side, the package gate reports it with
/// its position — before the route starts.
/// </summary>
public sealed class EndpointRoleOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-role-" + Guid.NewGuid().ToString("N"));

    public EndpointRoleOptionsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new HttpComponent()]);

    private static Type? Resolve(string typeName) => typeof(HttpComponent).Assembly.GetType(typeName);

    [Fact]
    public void Catalog_CarriesTheSideThatReadsAnOption()
    {
        var http = Catalog.Single(c => c.Scheme == "http");

        http.Options.Single(o => o.Name == "Username").Role.Should().Be(EndpointRole.Producer);
        http.Options.Single(o => o.Name == "InboundAuth").Role.Should().Be(EndpointRole.Consumer);
        http.Options.Single(o => o.Name == "Method").Role.Should().BeNull("both sides read it");
        ComponentCatalog.ToJson(Catalog).Should().Contain("\"role\": \"producer\"").And.Contain("\"role\": \"consumer\"");
    }

    private static IReadOnlyList<(int Line, int Column, string Message)> Validate(string from, string to)
        => XmlRouteSchema.Validate(XDocument.Parse($"""
            <routes xmlns="urn:redb:route:1.0">
              <route id="r">
                <from>{from}</from>
                <to>{to}</to>
              </route>
            </routes>
            """, LoadOptions.SetLineInfo), ElementRegistry.CreateDefault(), Catalog);

    [Fact]
    public void Schema_RefusesAnOption_OnTheSideThatDoesNotReadIt_EvenOnALenientConnector()
    {
        var problems = Validate(
            """<http path="0.0.0.0:8080/api" username="admin"/>""",
            """<http path="example.org/x" inboundAuth="Basic"/>""");

        problems.Should().Contain(p => p.Line == 3 && p.Message.Contains("username") && p.Message.Contains("producerOnlyOption"));
        problems.Should().Contain(p => p.Line == 4 && p.Message.Contains("inboundAuth") && p.Message.Contains("consumerOnlyOption"));
    }

    [Fact]
    public void Schema_AcceptsEachOption_OnItsOwnSide()
        => Validate(
                """<http path="0.0.0.0:8080/api" inboundAuth="Basic" inboundUsername="{{u}}"/>""",
                """<http path="example.org/x" username="{{u}}" authScheme="Basic"/>""")
            .Should().BeEmpty();

    [Fact]
    public void Gate_ReportsAnOptionOnTheWrongSide_InBothForms()
    {
        var dir = RouteProjectScaffold.Create(_root, "Role");
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="uri-form">
                <from uri="http://0.0.0.0:8080/in?username=admin"/>
                <to uri="http://example.org/out?inboundAuth=Basic"/>
              </route>
              <route id="structured-form">
                <from><http path="0.0.0.0:8081/in" inboundAuth="Basic" inboundUsername="u" inboundPassword="p"/></from>
                <to><http path="example.org/out" authScheme="Basic" username="u" password="p"/></to>
              </route>
              <route id="structured-wrong">
                <from uri="direct://w"/>
                <to><http path="example.org/w" inboundAuth="Basic"/></to>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0", Resolve, catalog: Catalog).Errors.ToList();

        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(3,")
            && e.Message.Contains("'username' is read only by the producer side of the http endpoint; <from> creates the consumer"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(4,")
            && e.Message.Contains("'inboundAuth' is read only by the consumer side of the http endpoint; <to> creates the producer"));
        errors.Should().NotContain(e => e.File.StartsWith("routes/main.route.xml(7,") || e.File.StartsWith("routes/main.route.xml(8,"),
            "each option is on its own side there");
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(12,")
            && e.Message.Contains("'inboundAuth' is read only by the consumer side of the http endpoint; <to> creates the producer"),
            "the structured form names the step, not the endpoint element");
    }
}
