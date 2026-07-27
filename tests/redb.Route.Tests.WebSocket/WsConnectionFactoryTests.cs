using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// The TLS certificate password must be able to live in the registry instead of the endpoint URI,
/// so it never reaches logs, telemetry, or the dashboard.
/// </summary>
public sealed class WsConnectionFactoryTests
{
    private const string Secret = "pfxP4ssw0rd";

    private static WsComponent Wire(string name, WsConnectionFactory factory, string scheme = "ws")
    {
        var context = new RouteContext();
        var component = new WsComponent(scheme);
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesCertPassword_WhenUriCarriesNone()
    {
        var component = Wire("secure-ws", new WsConnectionFactory
        {
            Ssl = true,
            SslCertPath = "/etc/redb/certs/server.pfx",
            SslCertPassword = Secret
        });

        var uri = EndpointUriParser.Parse("ws://0.0.0.0:9000/feed?connectionFactory=secure-ws");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void WssScheme_StillForcesTls_RegardlessOfFactory()
    {
        // The factory says Ssl=false, but the wss scheme must still win.
        var component = Wire("plain", new WsConnectionFactory { Ssl = false }, "wss");

        var uri = EndpointUriParser.Parse("wss://0.0.0.0:9443/feed?connectionFactory=plain");
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Ssl.Should().BeTrue();
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new WsComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("ws://0.0.0.0:9000/feed?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }
}
