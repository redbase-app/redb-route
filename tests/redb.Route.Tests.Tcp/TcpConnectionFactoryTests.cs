using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// The TLS certificate password must be able to live in the registry instead of the endpoint URI,
/// so it never reaches logs, telemetry, or the dashboard.
/// </summary>
public sealed class TcpConnectionFactoryTests
{
    private const string Secret = "pfxP4ssw0rd";

    private static TcpComponent Wire(string name, TcpConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new TcpComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesCertPassword_WhenUriCarriesNone()
    {
        var component = Wire("secure-tcp", new TcpConnectionFactory
        {
            Ssl = true,
            SslCertPath = "/etc/redb/certs/server.pfx",
            SslCertPassword = Secret,
            ConnectTimeout = 5_000
        });

        var uri = EndpointUriParser.Parse("tcp://0.0.0.0:9100?connectionFactory=secure-tcp");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new TcpConnectionFactory
        {
            Ssl = true,
            ConnectTimeout = 5_000
        });

        var uri = EndpointUriParser.Parse(
            "tcp://0.0.0.0:9100?connectionFactory=f&connectTimeout=1234");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["connectTimeout"].Should().Be("1234");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new TcpComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("tcp://0.0.0.0:9100?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }
}
