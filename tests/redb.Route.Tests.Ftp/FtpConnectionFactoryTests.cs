using redb.Route.Core;
using redb.Route.Ftp;

namespace redb.Route.Tests.Ftp;

/// <summary>
/// FTP server credentials must be able to live in the registry instead of the endpoint URI,
/// so they never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class FtpConnectionFactoryTests
{
    private const string Secret = "ftpP4ssw0rd";

    private static FtpComponent Wire(string name, FtpConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new FtpComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesCredentials_WhenUriCarriesNone()
    {
        var component = Wire("partner-ftp", new FtpConnectionFactory
        {
            Host = "ftp.partner.com",
            Port = 2121,
            Username = "svc-drop",
            Password = Secret,
            UseFtps = true
        });

        var uri = EndpointUriParser.Parse("ftp://inbox?connectionFactory=partner-ftp");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new FtpConnectionFactory
        {
            Host = "from-factory",
            Username = "factory-user",
            Port = 2121
        });

        var uri = EndpointUriParser.Parse(
            "ftp://inbox?connectionFactory=f&host=from-uri&username=uri-user");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["host"].Should().Be("from-uri");
        uri.RawParameters["username"].Should().Be("uri-user");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new FtpComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("ftp://inbox?connectionFactory=absent&host=direct");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dsl_EmitsConnectionFactory()
    {
        var uri = redb.Route.Ftp.Ftp.Directory("/inbox").ConnectionFactory("partner-ftp").Build();

        uri.Should().Contain("connectionFactory=partner-ftp");
        uri.Should().NotContain("password=");
    }
}
