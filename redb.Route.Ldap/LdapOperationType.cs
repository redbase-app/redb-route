namespace redb.Route.Ldap;

/// <summary>
/// LDAP operation types supported by the connector.
/// The first path segment of the URI determines the operation.
/// </summary>
public enum LdapOperationType
{
    // ── Read ──

    /// <summary>Search entries by filter, base DN, and scope.</summary>
    SEARCH,

    /// <summary>Compare an attribute value at a specific DN.</summary>
    COMPARE,

    // ── Write ──

    /// <summary>Create a new entry (DN + attributes).</summary>
    ADD,

    /// <summary>Modify attributes of an existing entry.</summary>
    MODIFY,

    /// <summary>Delete an entry by DN.</summary>
    DELETE,

    /// <summary>Rename/move an entry (ModifyDN).</summary>
    RENAME,

    // ── Auth ──

    /// <summary>Verify user credentials via LDAP Bind.</summary>
    BIND,

    // ── Consumer ──

    /// <summary>Poll directory for changes (consumer-only).</summary>
    WATCH
}
