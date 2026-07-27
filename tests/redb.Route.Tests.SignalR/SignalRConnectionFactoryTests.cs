using redb.Route.Core;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// The hub access token and TLS certificate password must be able to live in the registry instead
/// of the endpoint URI, so they never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class SignalRConnectionFactoryTests
{
    private const string Secret = "hubAcc3ssT0ken";

    private static SignalRComponent Wire(string name, SignalRConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new SignalRComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesAccessToken_WhenUriCarriesNone()
    {
        var component = Wire("chat-hub", new SignalRConnectionFactory
        {
            AccessToken = Secret,
            Ssl = true,
            SslCertPath = "/etc/redb/certs/server.pfx",
            SslCertPassword = "pfxPw"
        });

        var uri = EndpointUriParser.Parse("signalr://0.0.0.0:5000/chatHub?connectionFactory=chat-hub");
        var endpoint = (SignalREndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.AccessToken.Should().Be(Secret);
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new SignalRConnectionFactory { AccessToken = "from-factory" });

        var uri = EndpointUriParser.Parse(
            "signalr://0.0.0.0:5000/chatHub?connectionFactory=f&accessToken=from-uri");
        var endpoint = (SignalREndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.AccessToken.Should().Be("from-uri");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new SignalRComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("signalr://0.0.0.0:5000/chatHub?connectionFactory=absent");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }
}
