namespace redb.Route.Ldap;

/// <summary>
/// Change tracking mode for the LDAP consumer (WATCH operation).
/// </summary>
public enum LdapChangeTrackingMode
{
    /// <summary>Track changes via modifyTimestamp attribute. Works with any LDAP v3 server.</summary>
    ModifyTimestamp,

    /// <summary>Track changes via uSNChanged attribute. Active Directory only.</summary>
    Usn,

    /// <summary>Use LDAP Persistent Search control (RFC 2589). Supported by OpenLDAP, 389DS.</summary>
    Persistent
}
