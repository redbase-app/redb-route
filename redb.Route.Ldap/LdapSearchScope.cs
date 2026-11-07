namespace redb.Route.Ldap;

/// <summary>
/// LDAP search scope — controls how deep the search goes from the base DN.
/// </summary>
public enum LdapSearchScope
{
    /// <summary>Only the base entry itself.</summary>
    Base,

    /// <summary>One level below the base DN (direct children only).</summary>
    OneLevel,

    /// <summary>The entire subtree below the base DN (default).</summary>
    Subtree
}
