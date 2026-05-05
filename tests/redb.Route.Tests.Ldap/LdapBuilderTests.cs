using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapBuilderTests
{
    // ── Factory methods ─────────────────────────────────────────

    [Theory]
    [InlineData("SEARCH")]
    [InlineData("COMPARE")]
    [InlineData("ADD")]
    [InlineData("MODIFY")]
    [InlineData("DELETE")]
    [InlineData("RENAME")]
    [InlineData("BIND")]
    [InlineData("WATCH")]
    public void Factory_StartsWithLdapScheme(string op)
    {
        var builder = op switch
        {
            "SEARCH" => LdapDsl.Search("dc=test"),
            "COMPARE" => LdapDsl.Compare("cn=alice,dc=test"),
            "ADD" => LdapDsl.Add("dc=test"),
            "MODIFY" => LdapDsl.Modify("cn=alice,dc=test"),
            "DELETE" => LdapDsl.Delete("cn=alice,dc=test"),
            "RENAME" => LdapDsl.Rename("cn=alice,dc=test"),
            "BIND" => LdapDsl.Bind("dc=test"),
            "WATCH" => LdapDsl.Watch("dc=test"),
            _ => throw new ArgumentException()
        };
        builder.Build().Should().StartWith($"ldap:{op}:");
    }

    // ── Connection params ───────────────────────────────────────

    [Fact]
    public void Server_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Server("dc.company.com").Build();
        uri.Should().Contain("server=dc.company.com");
    }

    [Fact]
    public void Port_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Port(636).Build();
        uri.Should().Contain("port=636");
    }

    [Fact]
    public void Ssl_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Ssl().Build();
        uri.Should().Contain("ssl=true");
    }

    [Fact]
    public void StartTls_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").StartTls().Build();
        uri.Should().Contain("startTls=true");
    }

    [Fact]
    public void ConnectionFactory_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").ConnectionFactory("myLdap").Build();
        uri.Should().Contain("connectionFactory=myLdap");
    }

    [Fact]
    public void ConnectTimeout_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").ConnectTimeout(3000).Build();
        uri.Should().Contain("connectTimeout=3000");
    }

    [Fact]
    public void OperationTimeout_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").OperationTimeout(10000).Build();
        uri.Should().Contain("operationTimeout=10000");
    }

    // ── Auth params ─────────────────────────────────────────────

    [Fact]
    public void BindDn_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").BindDn("cn=admin,dc=test").Build();
        uri.Should().Contain("bindDn=");
    }

    [Fact]
    public void BindPassword_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").BindPassword("secret").Build();
        uri.Should().Contain("bindPassword=secret");
    }

    // ── Search params ───────────────────────────────────────────

    [Fact]
    public void Filter_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Filter("(objectClass=user)").Build();
        uri.Should().Contain("filter=");
    }

    [Fact]
    public void Scope_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Scope(LdapSearchScope.OneLevel).Build();
        uri.Should().Contain("scope=onelevel");
    }

    [Fact]
    public void Attributes_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Attributes("cn", "mail", "uid").Build();
        uri.Should().Contain("attributes=");
        uri.Should().Contain("cn");
    }

    [Fact]
    public void PageSize_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").PageSize(100).Build();
        uri.Should().Contain("pageSize=100");
    }

    [Fact]
    public void SizeLimit_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").SizeLimit(1000).Build();
        uri.Should().Contain("sizeLimit=1000");
    }

    [Fact]
    public void TimeLimit_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").TimeLimit(30).Build();
        uri.Should().Contain("timeLimit=30");
    }

    // ── Consumer params ─────────────────────────────────────────

    [Fact]
    public void PollInterval_SetsParam()
    {
        var uri = LdapDsl.Watch("dc=test").PollInterval(30000).Build();
        uri.Should().Contain("pollInterval=30000");
    }

    [Fact]
    public void ChangeTracking_SetsParam()
    {
        var uri = LdapDsl.Watch("dc=test").ChangeTracking(LdapChangeTrackingMode.Usn).Build();
        uri.Should().Contain("changeTrackingMode=usn");
    }

    [Fact]
    public void InitialLoad_SetsParam()
    {
        var uri = LdapDsl.Watch("dc=test").InitialLoad().Build();
        uri.Should().Contain("initialLoad=true");
    }

    [Fact]
    public void DetectDeletions_SetsParam()
    {
        var uri = LdapDsl.Watch("dc=test").DetectDeletions().Build();
        uri.Should().Contain("detectDeletions=true");
    }

    [Fact]
    public void FullSyncInterval_SetsParam()
    {
        var uri = LdapDsl.Watch("dc=test").FullSyncInterval(20).Build();
        uri.Should().Contain("fullSyncInterval=20");
    }

    // ── Protocol params ─────────────────────────────────────────

    [Fact]
    public void ProtocolVersion_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").ProtocolVersion(2).Build();
        uri.Should().Contain("protocolVersion=2");
    }

    [Fact]
    public void Referrals_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").Referrals(false).Build();
        uri.Should().Contain("followReferrals=false");
    }

    // ── Pool params ─────────────────────────────────────────────

    [Fact]
    public void MaxConnections_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").MaxConnections(5).Build();
        uri.Should().Contain("maxConnections=5");
    }

    // ── SSL params ──────────────────────────────────────────────

    [Fact]
    public void SkipCertificateValidation_SetsParam()
    {
        var uri = LdapDsl.Search("dc=test").SkipCertificateValidation().Build();
        uri.Should().Contain("skipCertificateValidation=true");
    }

    [Fact]
    public void ClientCert_SetsParams()
    {
        var uri = LdapDsl.Search("dc=test").ClientCert("/path/to/cert.pfx", "certpass").Build();
        uri.Should().Contain("clientCertPath=");
        uri.Should().Contain("clientCertPassword=certpass");
    }

    // ── Conversion ──────────────────────────────────────────────

    [Fact]
    public void ImplicitConversion_ReturnsUri()
    {
        string uri = LdapDsl.Search("dc=test").Server("host").Ssl();
        uri.Should().StartWith("ldap:SEARCH:dc=test?");
    }

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = LdapDsl.Search("dc=test").Server("host").Filter("(cn=*)");
        builder.ToString().Should().Be(builder.Build());
    }

    // ── Full chain ──────────────────────────────────────────────

    [Fact]
    public void FullChain_Search_BuildsCorrectUri()
    {
        var uri = LdapDsl.Search("dc=redb,dc=test")
            .Server("dc.company.com")
            .Port(636)
            .Ssl()
            .BindDn("cn=admin,dc=redb,dc=test")
            .BindPassword("secret")
            .Filter("(objectClass=inetOrgPerson)")
            .Scope(LdapSearchScope.Subtree)
            .Attributes("cn", "mail", "uid")
            .PageSize(100)
            .Build();

        uri.Should().StartWith("ldap:SEARCH:dc=redb,dc=test?");
        uri.Should().Contain("server=dc.company.com");
        uri.Should().Contain("port=636");
        uri.Should().Contain("ssl=true");
        uri.Should().Contain("pageSize=100");
        uri.Should().Contain("scope=subtree");
    }

    [Fact]
    public void FullChain_Watch_BuildsCorrectUri()
    {
        var uri = LdapDsl.Watch("ou=users,dc=redb,dc=test")
            .Server("dc.company.com")
            .Ssl()
            .PollInterval(30000)
            .ChangeTracking(LdapChangeTrackingMode.Usn)
            .InitialLoad()
            .Build();

        uri.Should().StartWith("ldap:WATCH:ou=users,dc=redb,dc=test?");
        uri.Should().Contain("pollInterval=30000");
        uri.Should().Contain("changeTrackingMode=usn");
        uri.Should().Contain("initialLoad=true");
    }

    // ── Round-trip ──────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ParseAndReconstruct()
    {
        var original = LdapDsl.Search("dc=test").Server("host").PageSize(100).Build();
        var parsed = EndpointUriParser.Parse(original);
        parsed.Scheme.Should().Be("ldap");
        parsed.RawParameters["server"].Should().Be("host");
        parsed.RawParameters["pageSize"].Should().Be("100");
    }
}
