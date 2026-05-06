using redb.Route.Core;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapProducerTests
{
    private readonly LdapComponent _component = new();

    private LdapEndpoint CreateEndpoint(string uriStr)
    {
        var uri = EndpointUriParser.Parse(uriStr);
        return (LdapEndpoint)_component.CreateEndpoint(uri);
    }

    [Fact]
    public void Ctor_NullEndpoint_Throws()
    {
        var opts = new LdapEndpointOptions();
        var act = () => new LdapProducer(null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        var act = () => new LdapProducer(ep, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Process_BeforeStart_Throws()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        var producer = new LdapProducer(ep, new LdapEndpointOptions());
        var exchange = new Exchange(new Message("test"));

        var act = () => producer.Process(exchange);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not been started*");
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("ldap:SEARCH:dc=redb,dc=test?server=localhost");
        var producer = new LdapProducer(ep, new LdapEndpointOptions());

        var act = () => producer.Stop();
        await act.Should().NotThrowAsync();
    }
}
