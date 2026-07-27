using redb.Route.Core;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

/// <summary>
/// The named-connection-factory path: service credentials live in the registry, not in the
/// endpoint URI, so they can never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class LdapConnectionFactoryTests
{
    private static LdapComponent Wire(string name, LdapConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new LdapComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesCredentials_WhenUriCarriesNone()
    {
        var component = Wire("honest-ldap", new LdapConnectionFactory
        {
            Server = "ldap.corp.local",
            Port = 636,
            Ssl = true,
            BindDn = "cn=svc-reader,dc=corp,dc=local",
            BindPassword = "s3cr3tSvcPw",
            MaxConnections = 4
        });

        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=corp,dc=local?connectionFactory=honest-ldap");
        var endpoint = (LdapEndpoint)component.CreateEndpoint(uri);

        endpoint.ResolvedFactory.Should().NotBeNull();
        endpoint.EndpointOptions.Server.Should().Be("ldap.corp.local");
        endpoint.EndpointOptions.Port.Should().Be(636);
        endpoint.EndpointOptions.Ssl.Should().BeTrue();
        endpoint.EndpointOptions.BindDn.Should().Be("cn=svc-reader,dc=corp,dc=local");
        endpoint.EndpointOptions.BindPassword.Should().Be("s3cr3tSvcPw");
        endpoint.EndpointOptions.MaxConnections.Should().Be(4);

        // The whole point: the secret exists nowhere in the URI, masked or not.
        uri.ToUriString().Should().NotContain("s3cr3tSvcPw");
        uri.ToString().Should().NotContain("s3cr3tSvcPw");
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new LdapConnectionFactory
        {
            Server = "from-factory",
            BindDn = "cn=factory",
            Port = 636
        });

        var uri = EndpointUriParser.Parse(
            "ldap:SEARCH:dc=corp?connectionFactory=f&server=from-uri&bindDn=cn=uri");
        var endpoint = (LdapEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Server.Should().Be("from-uri");
        endpoint.EndpointOptions.BindDn.Should().Be("cn=uri");
        // untouched by the URI → still comes from the factory
        endpoint.EndpointOptions.Port.Should().Be(636);
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters_WithoutThrowing()
    {
        var context = new RouteContext();
        var component = new LdapComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=corp?connectionFactory=absent&server=direct");
        var endpoint = (LdapEndpoint)component.CreateEndpoint(uri);

        endpoint.ResolvedFactory.Should().BeNull();
        endpoint.EndpointOptions.Server.Should().Be("direct");
    }

    [Fact]
    public void NoFactoryConfigured_LeavesUriOptionsUntouched()
    {
        // Component without a Context at all — the factory branch must be skipped safely.
        var component = new LdapComponent();

        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=corp?server=plain&bindDn=cn=x&bindPassword=p");
        var endpoint = (LdapEndpoint)component.CreateEndpoint(uri);

        endpoint.ResolvedFactory.Should().BeNull();
        endpoint.EndpointOptions.Server.Should().Be("plain");
        endpoint.EndpointOptions.BindDn.Should().Be("cn=x");
        endpoint.EndpointOptions.BindPassword.Should().Be("p");
    }
}
