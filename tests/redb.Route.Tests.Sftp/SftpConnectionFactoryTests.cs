using redb.Route.Core;
using redb.Route.Sftp;

namespace redb.Route.Tests.Sftp;

/// <summary>
/// SFTP password, private-key passphrase and proxy password must be able to live in the registry
/// instead of the endpoint URI, so they never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class SftpConnectionFactoryTests
{
    private const string Passphrase = "keyPassphr4se";
    private const string ProxyPw = "proxyP4ss";

    private static SftpComponent Wire(string name, SftpConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new SftpComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesSecrets_WhenUriCarriesNone()
    {
        var component = Wire("partner-sftp", new SftpConnectionFactory
        {
            Host = "sftp.partner.com",
            Port = 2222,
            Username = "svc-drop",
            PrivateKeyPath = "/etc/redb/keys/partner.pem",
            PrivateKeyPassphrase = Passphrase,
            ProxyPassword = ProxyPw
        });

        var uri = EndpointUriParser.Parse("sftp://inbox?connectionFactory=partner-sftp");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Passphrase).And.NotContain(ProxyPw);
        uri.ToString().Should().NotContain(Passphrase).And.NotContain(ProxyPw);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        // Validate() demands a username and at least one auth method — supplied by the factory.
        var component = Wire("f", new SftpConnectionFactory
        {
            Host = "from-factory",
            Username = "factory-user",
            Port = 2222,
            PrivateKeyPath = "/etc/redb/keys/partner.pem"
        });

        var uri = EndpointUriParser.Parse(
            "sftp://inbox?connectionFactory=f&host=from-uri&username=uri-user");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["host"].Should().Be("from-uri");
        uri.RawParameters["username"].Should().Be("uri-user");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new SftpComponent();
        context.AddComponent(component);

        // Falls back to URI parameters, which must therefore satisfy Validate() on their own.
        var uri = EndpointUriParser.Parse(
            "sftp://inbox?connectionFactory=absent&host=direct&username=u&password=p");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dsl_EmitsConnectionFactory()
    {
        var uri = redb.Route.Sftp.Sftp.Directory("/inbox").ConnectionFactory("partner-sftp").Build();

        uri.Should().Contain("connectionFactory=partner-sftp");
        uri.Should().NotContain("privateKeyPassphrase=");
    }
}
