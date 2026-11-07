using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapEndpointTests
{
    private readonly LdapComponent _component = new();

    private LdapEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (LdapEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void OperationType_ExtractedFromPath()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        ep.OperationType.Should().Be(LdapOperationType.SEARCH);
    }

    [Fact]
    public void BaseDn_ExtractedFromPath()
    {
        var ep = CreateEndpoint("ldap:SEARCH:ou=users,dc=redb,dc=test?server=localhost");
        ep.BaseDn.Should().Be("ou=users,dc=redb,dc=test");
    }

    [Fact]
    public void CreateProducer_ReturnsLdapProducer()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        ep.CreateProducer().Should().BeOfType<LdapProducer>();
    }

    [Fact]
    public void CreateConsumer_Watch_ReturnsLdapConsumer()
    {
        var ep = CreateEndpoint("ldap:WATCH:dc=redb,dc=test?server=localhost");
        var processor = Substitute.For<IProcessor>();
        ep.CreateConsumer(processor).Should().BeOfType<LdapConsumer>();
    }

    [Fact]
    public void CreateConsumer_NonWatch_Throws()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        var processor = Substitute.For<IProcessor>();

        var act = () => ep.CreateConsumer(processor);
        act.Should().Throw<InvalidOperationException>().WithMessage("*WATCH*");
    }

    [Fact]
    public void Component_IsLdapComponent()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        ep.Component.Should().BeSameAs(_component);
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        ep.Dispose();
        var act = () => ep.Dispose();
        act.Should().NotThrow();
    }
}
