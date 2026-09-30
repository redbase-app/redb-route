using FluentAssertions;
using redb.Route.Ldap;

namespace redb.Route.Tests.Ldap;

/// <summary>
/// TLS options carry the names most connectors use: trustAllCertificates. The former name is refused with the new one named, not
/// bound silently.
/// </summary>
public class RenamedTlsOptionTests
{
    [Fact]
    public void The_former_name_is_refused_with_the_new_one()
    {
        var act = () => new LdapEndpointOptions().BindFromUri(new Dictionary<string, string> { ["skipCertificateValidation"] = "true" });

        act.Should().Throw<ArgumentException>().WithMessage("*'skipCertificateValidation' is renamed 'trustAllCertificates'*");
    }

    [Fact]
    public void The_new_name_binds()
        => new LdapEndpointOptions().BindFromUri(new Dictionary<string, string> { ["trustAllCertificates"] = "true" });
}
