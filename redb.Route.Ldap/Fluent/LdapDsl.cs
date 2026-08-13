using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.Ldap;

/// <summary>
/// Fluent API for LDAP endpoints.
/// <example><code>
/// .To(LdapDsl.Search("dc=example,dc=com").Server("dc.company.com").Ssl().Filter("(objectClass=user)"))
/// .From(LdapDsl.Watch("ou=users,dc=example,dc=com").Server("dc.company.com").Ssl().PollInterval(60000))
/// </code></example>
/// </summary>
public static class LdapDsl
{
    // ── Read ──

    /// <summary>Search entries by filter and base DN.</summary>
    public static LdapBuilder Search(string baseDn) => new(LdapOperationType.SEARCH, baseDn);

    /// <summary>Search entries by filter and dynamic base DN.</summary>
    public static LdapBuilder Search(IExpression baseDn) => new(LdapOperationType.SEARCH, baseDn.ToTemplateString());

    /// <summary>Compare an attribute value at a specific DN.</summary>
    public static LdapBuilder Compare(string dn) => new(LdapOperationType.COMPARE, dn);

    /// <summary>Compare an attribute value at a dynamic DN.</summary>
    public static LdapBuilder Compare(IExpression dn) => new(LdapOperationType.COMPARE, dn.ToTemplateString());

    // ── Write ──

    /// <summary>Create a new entry under the specified parent DN.</summary>
    public static LdapBuilder Add(string parentDn) => new(LdapOperationType.ADD, parentDn);

    /// <summary>Create a new entry under a dynamic parent DN.</summary>
    public static LdapBuilder Add(IExpression parentDn) => new(LdapOperationType.ADD, parentDn.ToTemplateString());

    /// <summary>Modify attributes of an entry at the specified DN.</summary>
    public static LdapBuilder Modify(string dn) => new(LdapOperationType.MODIFY, dn);

    /// <summary>Modify attributes of an entry at a dynamic DN.</summary>
    public static LdapBuilder Modify(IExpression dn) => new(LdapOperationType.MODIFY, dn.ToTemplateString());

    /// <summary>Delete an entry at the specified DN.</summary>
    public static LdapBuilder Delete(string dn) => new(LdapOperationType.DELETE, dn);

    /// <summary>Delete an entry at a dynamic DN.</summary>
    public static LdapBuilder Delete(IExpression dn) => new(LdapOperationType.DELETE, dn.ToTemplateString());

    /// <summary>Rename/move an entry at the specified DN.</summary>
    public static LdapBuilder Rename(string dn) => new(LdapOperationType.RENAME, dn);

    /// <summary>Rename/move an entry at a dynamic DN.</summary>
    public static LdapBuilder Rename(IExpression dn) => new(LdapOperationType.RENAME, dn.ToTemplateString());

    // ── Auth ──

    /// <summary>Verify user credentials via LDAP Bind.</summary>
    public static LdapBuilder Bind(string baseDn) => new(LdapOperationType.BIND, baseDn);

    /// <summary>Verify user credentials via LDAP Bind (dynamic).</summary>
    public static LdapBuilder Bind(IExpression baseDn) => new(LdapOperationType.BIND, baseDn.ToTemplateString());

    // ── Consumer ──

    /// <summary>Poll directory for changes (consumer).</summary>
    public static LdapBuilder Watch(string baseDn) => new(LdapOperationType.WATCH, baseDn);

    /// <summary>Poll directory for changes (consumer, dynamic base DN).</summary>
    public static LdapBuilder Watch(IExpression baseDn) => new(LdapOperationType.WATCH, baseDn.ToTemplateString());
}

/// <summary>
/// Fluent builder for LDAP endpoint URIs.
/// </summary>
public sealed class LdapBuilder
{
    private readonly LdapOperationType _operation;
    private readonly string _baseDn;

    // Connection
    private string? _server;
    private string? _port;
    private bool _ssl;
    private bool _startTls;
    private string? _connectionFactory;
    private string? _connectTimeout;
    private string? _operationTimeout;

    // Auth
    private string? _bindDn;
    private string? _bindPassword;

    // Search
    private string? _filter;
    private string? _scope;
    private string? _attributes;
    private string? _pageSize;
    private string? _sizeLimit;
    private string? _timeLimit;

    // Consumer
    private string? _pollInterval;
    private string? _changeTrackingMode;
    private bool _initialLoad;
    private bool _detectDeletions;
    private string? _fullSyncInterval;

    // Protocol
    private string? _protocolVersion;
    private bool? _followReferrals;

    // Pool
    private string? _maxConnections;

    // SSL
    private bool _skipCertValidation;
    private string? _clientCertPath;
    private string? _clientCertPassword;

    internal LdapBuilder(LdapOperationType operation, string baseDn)
    {
        _operation = operation;
        _baseDn = baseDn;
    }

    // ── Connection ──

    /// <summary>Set the LDAP server hostname or IP.</summary>
    public LdapBuilder Server(string host) { _server = host; return this; }

    /// <summary>Set the LDAP server hostname dynamically.</summary>
    public LdapBuilder Server(IExpression host) { _server = host.ToTemplateString(); return this; }

    /// <summary>Set the LDAP port.</summary>
    public LdapBuilder Port(int port) { _port = port.ToString(); return this; }

    /// <summary>Set the LDAP port dynamically.</summary>
    public LdapBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    /// <summary>Enable LDAPS (SSL/TLS, typically port 636).</summary>
    public LdapBuilder Ssl() { _ssl = true; return this; }

    /// <summary>Enable STARTTLS upgrade on the plain port.</summary>
    public LdapBuilder StartTls() { _startTls = true; return this; }

    /// <summary>Use a named connection factory from the registry.</summary>
    public LdapBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Use a named connection factory dynamically.</summary>
    public LdapBuilder ConnectionFactory(IExpression name) { _connectionFactory = name.ToTemplateString(); return this; }

    /// <summary>Set connection timeout in milliseconds.</summary>
    public LdapBuilder ConnectTimeout(int ms) { _connectTimeout = ms.ToString(); return this; }

    /// <summary>Set operation timeout in milliseconds.</summary>
    public LdapBuilder OperationTimeout(int ms) { _operationTimeout = ms.ToString(); return this; }

    // ── Auth ──

    /// <summary>Set the Bind DN for service account authentication.</summary>
    public LdapBuilder BindDn(string dn) { _bindDn = dn; return this; }

    /// <summary>Set the Bind DN dynamically.</summary>
    public LdapBuilder BindDn(IExpression dn) { _bindDn = dn.ToTemplateString(); return this; }

    /// <summary>Set the Bind password.</summary>
    public LdapBuilder BindPassword(string password) { _bindPassword = password; return this; }

    /// <summary>Set the Bind password dynamically.</summary>
    public LdapBuilder BindPassword(IExpression password) { _bindPassword = password.ToTemplateString(); return this; }

    // ── Search ──

    /// <summary>Set the LDAP search filter.</summary>
    public LdapBuilder Filter(string filter) { _filter = filter; return this; }

    /// <summary>Set the LDAP search filter dynamically.</summary>
    public LdapBuilder Filter(IExpression filter) { _filter = filter.ToTemplateString(); return this; }

    /// <summary>Set the search scope.</summary>
    public LdapBuilder Scope(LdapSearchScope scope) { _scope = scope.ToString().ToLowerInvariant(); return this; }

    /// <summary>Set the attributes to return from search.</summary>
    public LdapBuilder Attributes(params string[] attrs) { _attributes = string.Join(",", attrs); return this; }

    /// <summary>Set the page size for paged search results.</summary>
    public LdapBuilder PageSize(int size) { _pageSize = size.ToString(); return this; }

    /// <summary>Set the page size dynamically.</summary>
    public LdapBuilder PageSize(IExpression size) { _pageSize = size.ToTemplateString(); return this; }

    /// <summary>Set the server-side maximum number of entries.</summary>
    public LdapBuilder SizeLimit(int limit) { _sizeLimit = limit.ToString(); return this; }

    /// <summary>Set the server-side time limit in seconds.</summary>
    public LdapBuilder TimeLimit(int seconds) { _timeLimit = seconds.ToString(); return this; }

    // ── Consumer ──

    /// <summary>Set the poll interval in milliseconds for the Watch consumer.</summary>
    public LdapBuilder PollInterval(int ms) { _pollInterval = ms.ToString(); return this; }

    /// <summary>Set the poll interval dynamically.</summary>
    public LdapBuilder PollInterval(IExpression ms) { _pollInterval = ms.ToTemplateString(); return this; }

    /// <summary>Set the change tracking mode.</summary>
    public LdapBuilder ChangeTracking(LdapChangeTrackingMode mode = LdapChangeTrackingMode.ModifyTimestamp)
    {
        _changeTrackingMode = mode.ToString().ToLowerInvariant();
        return this;
    }

    /// <summary>Load all matching entries on consumer startup.</summary>
    public LdapBuilder InitialLoad() { _initialLoad = true; return this; }

    /// <summary>Enable deletion detection by comparing known DNs with current results.</summary>
    public LdapBuilder DetectDeletions() { _detectDeletions = true; return this; }

    /// <summary>Set the number of poll cycles between full synchronizations for deletion detection.</summary>
    public LdapBuilder FullSyncInterval(int cycles) { _fullSyncInterval = cycles.ToString(); return this; }

    // ── Protocol ──

    /// <summary>Set the LDAP protocol version (2 or 3).</summary>
    public LdapBuilder ProtocolVersion(int version) { _protocolVersion = version.ToString(); return this; }

    /// <summary>Control referral following (default: true).</summary>
    public LdapBuilder Referrals(bool follow) { _followReferrals = follow; return this; }

    // ── Pool ──

    /// <summary>Set the maximum number of pooled connections.</summary>
    public LdapBuilder MaxConnections(int max) { _maxConnections = max.ToString(); return this; }

    // ── SSL ──

    /// <summary>Skip server certificate validation (development only!).</summary>
    public LdapBuilder SkipCertificateValidation() { _skipCertValidation = true; return this; }

    /// <summary>Set client certificate for mutual TLS.</summary>
    public LdapBuilder ClientCert(string path, string? password = null)
    {
        _clientCertPath = path;
        _clientCertPassword = password;
        return this;
    }

    // ── Build ──

    /// <summary>Builds the LDAP endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("ldap:");
        sb.Append(_operation.ToString());
        sb.Append(':');
        sb.Append(_baseDn);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep);
            sb.Append(key);
            sb.Append('=');
            sb.Append(Uri.EscapeDataString(value));
            sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value) { if (value.HasValue) Append(key, value.Value ? "true" : "false"); }

        // Connection
        AppendIf("server", _server);
        AppendIf("port", _port);
        AppendBool("ssl", _ssl);
        AppendBool("startTls", _startTls);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("connectTimeout", _connectTimeout);
        AppendIf("operationTimeout", _operationTimeout);

        // Auth
        AppendIf("bindDn", _bindDn);
        AppendIf("bindPassword", _bindPassword);

        // Search
        AppendIf("filter", _filter);
        AppendIf("scope", _scope);
        AppendIf("attributes", _attributes);
        AppendIf("pageSize", _pageSize);
        AppendIf("sizeLimit", _sizeLimit);
        AppendIf("timeLimit", _timeLimit);

        // Consumer
        AppendIf("pollInterval", _pollInterval);
        AppendIf("changeTrackingMode", _changeTrackingMode);
        AppendBool("initialLoad", _initialLoad);
        AppendBool("detectDeletions", _detectDeletions);
        AppendIf("fullSyncInterval", _fullSyncInterval);

        // Protocol
        AppendIf("protocolVersion", _protocolVersion);
        AppendBoolExplicit("followReferrals", _followReferrals);

        // Pool
        AppendIf("maxConnections", _maxConnections);

        // SSL
        AppendBool("skipCertificateValidation", _skipCertValidation);
        AppendIf("clientCertPath", _clientCertPath);
        AppendIf("clientCertPassword", _clientCertPassword);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(LdapBuilder b) => b.Build();

    /// <inheritdoc />
    public override string ToString() => Build();
}
