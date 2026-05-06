using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapEndpointOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var opts = new LdapEndpointOptions();

        opts.Server.Should().Be("localhost");
        opts.Port.Should().Be(389);
        opts.Ssl.Should().BeFalse();
        opts.StartTls.Should().BeFalse();
        opts.ConnectionFactory.Should().BeNull();
        opts.ConnectTimeout.Should().Be(5000);
        opts.OperationTimeout.Should().Be(30000);
        opts.BindDn.Should().BeNull();
        opts.BindPassword.Should().BeNull();
        opts.Filter.Should().BeNull();
        opts.Scope.Should().Be("subtree");
        opts.Attributes.Should().BeNull();
        opts.PageSize.Should().Be(500);
        opts.SizeLimit.Should().Be(0);
        opts.TimeLimit.Should().Be(0);
        opts.PollInterval.Should().Be(60000);
        opts.ChangeTrackingMode.Should().Be("modifyTimestamp");
        opts.InitialLoad.Should().BeFalse();
        opts.DetectDeletions.Should().BeFalse();
        opts.FullSyncInterval.Should().Be(10);
        opts.ProtocolVersion.Should().Be(3);
        opts.FollowReferrals.Should().BeTrue();
        opts.MaxConnections.Should().Be(10);
        opts.SkipCertificateValidation.Should().BeFalse();
        opts.ClientCertPath.Should().BeNull();
        opts.ClientCertPassword.Should().BeNull();
    }

    // ── Validate ──

    [Fact]
    public void Validate_ValidOptions_DoesNotThrow()
    {
        var opts = new LdapEndpointOptions();
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EmptyServer_Throws()
    {
        var opts = new LdapEndpointOptions { Server = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*server*");
    }

    [Fact]
    public void Validate_WhitespaceServer_Throws()
    {
        var opts = new LdapEndpointOptions { Server = "  " };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validate_ZeroPort_Throws()
    {
        var opts = new LdapEndpointOptions { Port = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*Port*");
    }

    [Fact]
    public void Validate_NegativePort_Throws()
    {
        var opts = new LdapEndpointOptions { Port = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_PortTooHigh_Throws()
    {
        var opts = new LdapEndpointOptions { Port = 70000 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ZeroPageSize_Throws()
    {
        var opts = new LdapEndpointOptions { PageSize = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PageSize*");
    }

    [Fact]
    public void Validate_NegativePageSize_Throws()
    {
        var opts = new LdapEndpointOptions { PageSize = -5 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validate_ZeroMaxConnections_Throws()
    {
        var opts = new LdapEndpointOptions { MaxConnections = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*MaxConnections*");
    }

    [Fact]
    public void Validate_ZeroConnectTimeout_Throws()
    {
        var opts = new LdapEndpointOptions { ConnectTimeout = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*ConnectTimeout*");
    }

    [Fact]
    public void Validate_ZeroOperationTimeout_Throws()
    {
        var opts = new LdapEndpointOptions { OperationTimeout = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*OperationTimeout*");
    }

    [Fact]
    public void Validate_ZeroPollInterval_Throws()
    {
        var opts = new LdapEndpointOptions { PollInterval = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*PollInterval*");
    }

    [Fact]
    public void Validate_ZeroFullSyncInterval_Throws()
    {
        var opts = new LdapEndpointOptions { FullSyncInterval = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*FullSyncInterval*");
    }

    // ── ParseScope ──

    [Theory]
    [InlineData("base", LdapSearchScope.Base)]
    [InlineData("onelevel", LdapSearchScope.OneLevel)]
    [InlineData("one", LdapSearchScope.OneLevel)]
    [InlineData("subtree", LdapSearchScope.Subtree)]
    [InlineData("sub", LdapSearchScope.Subtree)]
    [InlineData("SUBTREE", LdapSearchScope.Subtree)]
    [InlineData("unknown", LdapSearchScope.Subtree)]
    [InlineData(null, LdapSearchScope.Subtree)]
    public void ParseScope_ReturnsExpected(string? scope, LdapSearchScope expected)
    {
        var opts = new LdapEndpointOptions { Scope = scope! };
        opts.ParseScope().Should().Be(expected);
    }

    // ── ParseAttributes ──

    [Fact]
    public void ParseAttributes_Null_ReturnsNull()
    {
        var opts = new LdapEndpointOptions { Attributes = null };
        opts.ParseAttributes().Should().BeNull();
    }

    [Fact]
    public void ParseAttributes_Empty_ReturnsNull()
    {
        var opts = new LdapEndpointOptions { Attributes = "" };
        opts.ParseAttributes().Should().BeNull();
    }

    [Fact]
    public void ParseAttributes_Csv_SplitsCorrectly()
    {
        var opts = new LdapEndpointOptions { Attributes = "cn, mail, sAMAccountName" };
        opts.ParseAttributes().Should().BeEquivalentTo(new[] { "cn", "mail", "sAMAccountName" });
    }

    // ── ParseChangeTrackingMode ──

    [Theory]
    [InlineData("modifyTimestamp", LdapChangeTrackingMode.ModifyTimestamp)]
    [InlineData("modifytimestamp", LdapChangeTrackingMode.ModifyTimestamp)]
    [InlineData("usn", LdapChangeTrackingMode.Usn)]
    [InlineData("persistent", LdapChangeTrackingMode.Persistent)]
    [InlineData("unknown", LdapChangeTrackingMode.ModifyTimestamp)]
    public void ParseChangeTrackingMode_ReturnsExpected(string mode, LdapChangeTrackingMode expected)
    {
        var opts = new LdapEndpointOptions { ChangeTrackingMode = mode };
        opts.ParseChangeTrackingMode().Should().Be(expected);
    }

    // ── EffectivePort ──

    [Fact]
    public void EffectivePort_NonSsl_ReturnsPort()
    {
        var opts = new LdapEndpointOptions { Port = 389, Ssl = false };
        opts.EffectivePort.Should().Be(389);
    }

    [Fact]
    public void EffectivePort_SslWithDefaultPort_Returns636()
    {
        var opts = new LdapEndpointOptions { Port = 389, Ssl = true };
        opts.EffectivePort.Should().Be(636);
    }

    [Fact]
    public void EffectivePort_SslWithCustomPort_ReturnsCustomPort()
    {
        var opts = new LdapEndpointOptions { Port = 3636, Ssl = true };
        opts.EffectivePort.Should().Be(3636);
    }
}
