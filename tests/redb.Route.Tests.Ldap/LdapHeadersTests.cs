using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

public sealed class LdapHeadersTests
{
    [Fact]
    public void Prefix_IsCorrect()
    {
        LdapHeaders.Prefix.Should().Be("redbLdap.");
    }

    [Theory]
    [InlineData(nameof(LdapHeaders.Dn), "redbLdap.Dn")]
    [InlineData(nameof(LdapHeaders.BaseDn), "redbLdap.BaseDn")]
    [InlineData(nameof(LdapHeaders.Filter), "redbLdap.Filter")]
    [InlineData(nameof(LdapHeaders.Timestamp), "redbLdap.Timestamp")]
    [InlineData(nameof(LdapHeaders.ResultCount), "redbLdap.ResultCount")]
    [InlineData(nameof(LdapHeaders.SearchTime), "redbLdap.SearchTime")]
    [InlineData(nameof(LdapHeaders.PageCookie), "redbLdap.PageCookie")]
    [InlineData(nameof(LdapHeaders.Server), "redbLdap.Server")]
    [InlineData(nameof(LdapHeaders.Port), "redbLdap.Port")]
    [InlineData(nameof(LdapHeaders.Ssl), "redbLdap.Ssl")]
    [InlineData(nameof(LdapHeaders.Scope), "redbLdap.Scope")]
    [InlineData(nameof(LdapHeaders.ModCount), "redbLdap.ModCount")]
    [InlineData(nameof(LdapHeaders.Deleted), "redbLdap.Deleted")]
    [InlineData(nameof(LdapHeaders.AuthDn), "redbLdap.AuthDn")]
    [InlineData(nameof(LdapHeaders.AuthPassword), "redbLdap.AuthPassword")]
    [InlineData(nameof(LdapHeaders.BindResult), "redbLdap.BindResult")]
    [InlineData(nameof(LdapHeaders.CompareAttribute), "redbLdap.CompareAttribute")]
    [InlineData(nameof(LdapHeaders.CompareValue), "redbLdap.CompareValue")]
    [InlineData(nameof(LdapHeaders.CompareResult), "redbLdap.CompareResult")]
    [InlineData(nameof(LdapHeaders.NewRdn), "redbLdap.NewRdn")]
    [InlineData(nameof(LdapHeaders.NewParentDn), "redbLdap.NewParentDn")]
    [InlineData(nameof(LdapHeaders.OldDn), "redbLdap.OldDn")]
    [InlineData(nameof(LdapHeaders.NewDn), "redbLdap.NewDn")]
    [InlineData(nameof(LdapHeaders.ChangeType), "redbLdap.ChangeType")]
    [InlineData(nameof(LdapHeaders.ChangeMarker), "redbLdap.ChangeMarker")]
    public void Headers_HaveCorrectPrefix(string fieldName, string expected)
    {
        var value = typeof(LdapHeaders).GetField(fieldName)!.GetValue(null) as string;
        value.Should().Be(expected);
        value.Should().StartWith(LdapHeaders.Prefix);
    }
}
