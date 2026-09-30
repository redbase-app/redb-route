using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Xml;
using redb.Route.Xml.Packaging;

namespace redb.Route.Tests.Xml;

/// <summary>A broker-like connector: a named connection factory, or the connection on the URI.</summary>
public sealed class ProbeConnComponent : ComponentBase
{
    public override string Scheme => "probeconn";

    public override IEndpoint CreateEndpoint(EndpointUri uri)
        => throw new NotSupportedException("catalog-only test component");
}

/// <summary>The options of <see cref="ProbeConnComponent"/>, declared the way RabbitMQ declares its own.</summary>
public sealed class ProbeConnEndpointOptions : EndpointOptions
{
    [ConnectionFactoryReference]
    public string? ConnectionFactory { get; set; }

    [ConnectionParameter]
    public string? Host { get; set; }

    [ConnectionParameter]
    public int Port { get; set; }

    public string? Queue { get; set; }

    public override void Validate()
    {
    }
}

/// <summary>
/// A named connection factory is the whole connection (abead3e8): a connection parameter written
/// beside it is refused when the endpoint is created. The catalog carries the declarations, and
/// the package gate reports the conflict with its position, URI form and structured form alike.
/// </summary>
public sealed class ConnectionFactoryGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "redb-xml-connfactory-" + Guid.NewGuid().ToString("N"));

    public ConnectionFactoryGateTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static readonly IReadOnlyList<CatalogComponent> Catalog = ComponentCatalog.Build([new ProbeConnComponent()]);

    private static Type? Resolve(string typeName) => typeof(ProbeConnComponent).Assembly.GetType(typeName);

    [Fact]
    public void Catalog_CarriesTheConnectionDeclarations()
    {
        var options = Catalog.Single().Options;

        options.Single(o => o.Name == "ConnectionFactory").ConnectionFactoryReference.Should().BeTrue();
        options.Single(o => o.Name == "Host").ConnectionParameter.Should().BeTrue();
        options.Single(o => o.Name == "Queue").ConnectionParameter.Should().BeFalse();
        ComponentCatalog.ToJson(Catalog).Should().Contain("\"connectionParameter\": true")
            .And.Contain("\"connectionFactoryReference\": true");
    }

    [Fact]
    public void Gate_RefusesAConnectionParameterBesideTheFactory_InBothForms()
    {
        var dir = RouteProjectScaffold.Create(_root, "Conn");
        File.WriteAllText(Path.Combine(dir, "routes", "main.route.xml"), """
            <routes xmlns="urn:redb:route:1.0">
              <route id="uri-form">
                <from uri="probeconn://orders?connectionFactory=main&amp;host=broker&amp;queue=q"/>
                <to uri="direct://out"/>
              </route>
              <route id="structured-form">
                <from uri="direct://in"/>
                <to><probeconn path="audit" connectionFactory="main" port="5671"/></to>
              </route>
              <route id="fine">
                <from uri="probeconn://a?connectionFactory=main&amp;queue=q"/>
                <to uri="probeconn://b?host=broker&amp;port=5672"/>
              </route>
            </routes>
            """);

        var errors = RoutePackage.Check(dir, "p", "1.0.0", Resolve, catalog: Catalog).Errors.ToList();

        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(3,")
            && e.Message.Contains("connectionFactory 'main' sets the whole connection, so host cannot be given on the URI as well"));
        errors.Should().Contain(e => e.File.StartsWith("routes/main.route.xml(8,")
            && e.Message.Contains("connectionFactory 'main' sets the whole connection, so port cannot be given"));
        errors.Should().NotContain(e => e.File.StartsWith("routes/main.route.xml(11,") || e.File.StartsWith("routes/main.route.xml(12,"),
            "a factory alone, or the connection alone on the URI, is fine");
    }
}
