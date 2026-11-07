namespace redb.Route.Ldap;

/// <summary>
/// Typed LDAP directory entry (DN + attributes).
/// Returned by Search and Watch operations.
/// </summary>
public sealed class LdapEntry
{
    /// <summary>Distinguished Name of the entry.</summary>
    public string Dn { get; set; } = "";

    /// <summary>
    /// Attribute dictionary. Values are <c>string</c> for single-valued,
    /// <c>string[]</c> for multi-valued, or <c>byte[]</c> for binary attributes.
    /// </summary>
    public Dictionary<string, object> Attributes { get; set; } = new();

    /// <summary>Change type for consumer entries: "added", "modified", or "deleted".</summary>
    public string? ChangeType { get; set; }

    /// <summary>Gets a single-valued string attribute.</summary>
    public string? GetString(string attr) =>
        Attributes.TryGetValue(attr, out var v) ? v?.ToString() : null;

    /// <summary>Gets a multi-valued string attribute.</summary>
    public string[]? GetStringArray(string attr) =>
        Attributes.TryGetValue(attr, out var v) ? v as string[] : null;

    /// <summary>Gets a binary attribute value (first element if multi-valued).</summary>
    public byte[]? GetBytes(string attr)
    {
        if (!Attributes.TryGetValue(attr, out var v)) return null;
        if (v is byte[] single) return single;
        if (v is byte[][] multi) return multi.Length > 0 ? multi[0] : null;
        return null;
    }

    /// <summary>Gets a multi-valued binary attribute.</summary>
    public byte[][]? GetBytesArray(string attr) =>
        Attributes.TryGetValue(attr, out var v) ? v as byte[][] : null;
}
