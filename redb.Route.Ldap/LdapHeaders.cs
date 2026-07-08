namespace redb.Route.Ldap;

/// <summary>
/// Header constants for LDAP exchanges. All keys use the <c>redbLdap.</c> prefix.
/// </summary>
public static class LdapHeaders
{
    /// <summary>Common prefix for all LDAP headers.</summary>
    public const string Prefix = "redbLdap.";

    // ── Common ──

    /// <summary>Distinguished Name of the entry.</summary>
    public const string Dn = "redbLdap.Dn";

    /// <summary>Base DN used for the operation.</summary>
    public const string BaseDn = "redbLdap.BaseDn";

    /// <summary>LDAP filter expression.</summary>
    public const string Filter = "redbLdap.Filter";

    /// <summary>UTC timestamp of the exchange.</summary>
    public const string Timestamp = "redbLdap.Timestamp";

    // ── Search ──

    /// <summary>Number of entries returned by a search.</summary>
    public const string ResultCount = "redbLdap.ResultCount";

    /// <summary>Search execution time in milliseconds.</summary>
    public const string SearchTime = "redbLdap.SearchTime";

    /// <summary>Opaque cookie for paged search continuation.</summary>
    public const string PageCookie = "redbLdap.PageCookie";

    /// <summary>LDAP server hostname.</summary>
    public const string Server = "redbLdap.Server";

    /// <summary>LDAP server port.</summary>
    public const string Port = "redbLdap.Port";

    /// <summary>Whether SSL/TLS was used for the connection.</summary>
    public const string Ssl = "redbLdap.Ssl";

    /// <summary>Search scope used (Base, OneLevel, Subtree).</summary>
    public const string Scope = "redbLdap.Scope";

    // ── Modify ──

    /// <summary>Number of modifications applied.</summary>
    public const string ModCount = "redbLdap.ModCount";

    // ── Delete ──

    /// <summary>Whether the entry was successfully deleted.</summary>
    public const string Deleted = "redbLdap.Deleted";

    // ── Bind (auth) ──

    /// <summary>DN used for authentication.</summary>
    public const string AuthDn = "redbLdap.AuthDn";

    /// <summary>Password for authentication (cleared after use).</summary>
    public const string AuthPassword = "redbLdap.AuthPassword";

    /// <summary>Bind result code.</summary>
    public const string BindResult = "redbLdap.BindResult";

    // ── Compare ──

    /// <summary>Attribute name to compare.</summary>
    public const string CompareAttribute = "redbLdap.CompareAttribute";

    /// <summary>Expected attribute value to compare against.</summary>
    public const string CompareValue = "redbLdap.CompareValue";

    /// <summary>Comparison result (true/false).</summary>
    public const string CompareResult = "redbLdap.CompareResult";

    // ── Rename ──

    /// <summary>New RDN for rename operation.</summary>
    public const string NewRdn = "redbLdap.NewRdn";

    /// <summary>New parent DN for move operation.</summary>
    public const string NewParentDn = "redbLdap.NewParentDn";

    /// <summary>Original DN before rename.</summary>
    public const string OldDn = "redbLdap.OldDn";

    /// <summary>Resulting DN after rename.</summary>
    public const string NewDn = "redbLdap.NewDn";

    // ── Consumer (Watch) ──

    /// <summary>Type of change detected: added, modified, or deleted.</summary>
    public const string ChangeType = "redbLdap.ChangeType";

    /// <summary>Opaque marker for change tracking position (USN or timestamp).</summary>
    public const string ChangeMarker = "redbLdap.ChangeMarker";

    /// <summary>Checks whether a header key belongs to this connector.</summary>
    public static bool IsRedbHeader(string key) =>
        key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
}
