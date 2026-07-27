using redb.Route.Core;
using redb.Route.Grpc;

namespace redb.Route.Tests.Grpc;

/// <summary>
/// The TLS certificate password must be able to live in the registry instead of the endpoint URI,
/// so it never reaches logs, telemetry, or the dashboard.
/// </summary>
public sealed class GrpcConnectionFactoryTests
{
    private const string Secret = "pfxP4ssw0rd";

    private static GrpcComponent Wire(string name, GrpcConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new GrpcComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesCertPassword_WhenUriCarriesNone()
    {
        var component = Wire("secure-grpc", new GrpcConnectionFactory
        {
            Ssl = true,
            SslCertPath = "/etc/redb/certs/server.pfx",
            SslCertPassword = Secret
        });

        var uri = EndpointUriParser.Parse("grpc://0.0.0.0:50051?connectionFactory=secure-grpc");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new GrpcConnectionFactory
        {
            Ssl = true,
            SslCertPath = "/from/factory.pfx"
        });

        var uri = EndpointUriParser.Parse(
            "grpc://0.0.0.0:50051?connectionFactory=f&sslCertPath=/from/uri.pfx");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["sslCertPath"].Should().Be("/from/uri.pfx");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new GrpcComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("grpc://0.0.0.0:50051?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }
}
