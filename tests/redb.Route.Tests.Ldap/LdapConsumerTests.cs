using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapConsumerTests
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
        var proc = Substitute.For<IProcessor>();
        var act = () => new LdapConsumer(null!, proc, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProcessor_Throws()
    {
        var ep = CreateEndpoint("ldap:WATCH:dc=redb,dc=test?server=localhost");
        var opts = new LdapEndpointOptions();
        var act = () => new LdapConsumer(ep, null!, opts);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullOptions_Throws()
    {
        var ep = CreateEndpoint("ldap:WATCH:dc=redb,dc=test?server=localhost");
        var proc = Substitute.For<IProcessor>();
        var act = () => new LdapConsumer(ep, proc, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ProcessedCount_InitiallyZero()
    {
        var ep = CreateEndpoint("ldap:WATCH:dc=redb,dc=test?server=localhost");
        var proc = Substitute.For<IProcessor>();
        var opts = new LdapEndpointOptions();
        var consumer = new LdapConsumer(ep, proc, opts);
        consumer.ProcessedCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_BeforeStart_DoesNotThrow()
    {
        var ep = CreateEndpoint("ldap:WATCH:dc=redb,dc=test?server=localhost");
        var proc = Substitute.For<IProcessor>();
        var opts = new LdapEndpointOptions();
        var consumer = new LdapConsumer(ep, proc, opts);

        var act = () => consumer.Stop();
        await act.Should().NotThrowAsync();
    }
}
