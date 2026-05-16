using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Ldap;

/// <summary>
/// LDAP transport component for redb.Route.
/// Scheme: <c>ldap</c>.
/// <para>
/// URI format: <c>ldap:SEARCH:dc=example,dc=com?server=host&amp;port=636&amp;ssl=true&amp;filter=(objectClass=user)</c>
/// </para>
/// <para>
/// The first path segment is the <see cref="LdapOperationType"/>.
/// The remaining segments form the base DN or target DN.
/// </para>
/// </summary>
public sealed class LdapComponent : ComponentBase
{
    /// <inheritdoc />
    public override string Scheme => "ldap";

    /// <inheritdoc />
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new LdapEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new LdapEndpoint(uri, this, options);
    }
}
