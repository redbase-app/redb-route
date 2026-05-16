using redb.Route.Core;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapComponentTests
{
    private readonly LdapComponent _sut = new();

    [Fact]
    public void Scheme_ReturnsLdap()
    {
        _sut.Scheme.Should().Be("ldap");
    }

    [Fact]
    public void CreateEndpoint_ValidSearchUri_ReturnsLdapEndpoint()
    {
        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=redb,dc=test?server=localhost&bindDn=cn=admin,dc=redb,dc=test&bindPassword=admin");

        var endpoint = _sut.CreateEndpoint(uri);

        endpoint.Should().BeOfType<LdapEndpoint>();
        var ldap = (LdapEndpoint)endpoint;
        ldap.OperationType.Should().Be(LdapOperationType.SEARCH);
        ldap.BaseDn.Should().Be("dc=redb,dc=test");
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var act = () => _sut.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_WatchUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:WATCH:ou=users,dc=redb,dc=test?server=localhost&bindDn=cn=admin,dc=redb,dc=test&bindPassword=admin");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.WATCH);
        endpoint.BaseDn.Should().Be("ou=users,dc=redb,dc=test");
    }

    [Fact]
    public void CreateEndpoint_AddUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:ADD:ou=users,dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.ADD);
        endpoint.BaseDn.Should().Be("ou=users,dc=redb,dc=test");
    }

    [Fact]
    public void CreateEndpoint_ModifyUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:MODIFY:cn=alice,ou=users,dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.MODIFY);
    }

    [Fact]
    public void CreateEndpoint_DeleteUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:DELETE:cn=alice,ou=users,dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.DELETE);
    }

    [Fact]
    public void CreateEndpoint_CompareUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:COMPARE:cn=alice,ou=users,dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.COMPARE);
    }

    [Fact]
    public void CreateEndpoint_RenameUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:RENAME:cn=alice,ou=users,dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.RENAME);
    }

    [Fact]
    public void CreateEndpoint_BindUri_ParsesCorrectly()
    {
        var uri = EndpointUriParser.Parse("ldap:BIND:dc=redb,dc=test?server=localhost");
        var endpoint = (LdapEndpoint)_sut.CreateEndpoint(uri);

        endpoint.OperationType.Should().Be(LdapOperationType.BIND);
    }

    [Fact]
    public void CreateEndpoint_InvalidPort_Throws()
    {
        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=redb,dc=test?server=localhost&port=0");
        var act = () => _sut.CreateEndpoint(uri);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CreateEndpoint_InvalidPageSize_Throws()
    {
        var uri = EndpointUriParser.Parse("ldap:SEARCH:dc=redb,dc=test?server=localhost&pageSize=0");
        var act = () => _sut.CreateEndpoint(uri);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
