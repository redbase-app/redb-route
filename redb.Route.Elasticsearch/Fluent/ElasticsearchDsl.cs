using System.Text;
using System.Web;
using redb.Route.Abstractions;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Fluent API for Elasticsearch endpoints.
/// <example><code>
/// // Consumer: poll for new documents
/// .From(Es.Index("my-index")
///     .Nodes("http://localhost:9200")
///     .Query("{\"term\":{\"status\":\"active\"}}")
///     .Sort("timestamp:desc")
///     .DeleteAfterRead()
///     .Delay(10_000))
///
/// // Producer: index a document
/// .To(Es.Index("my-index")
///     .Nodes("http://localhost:9200")
///     .Refresh("wait_for"))
///
/// // Producer: search
/// .To(Es.Index("my-index", ElasticsearchOperationType.Search)
///     .Nodes("http://localhost:9200"))
/// </code></example>
/// </summary>
public static class Es
{
    /// <summary>Creates an Elasticsearch endpoint builder for the given index name.</summary>
    public static ElasticsearchBuilder Index(string indexName)
        => new(indexName, null);

    /// <summary>Creates an Elasticsearch endpoint builder with a specific producer operation.</summary>
    public static ElasticsearchBuilder Index(string indexName, ElasticsearchOperationType operation)
        => new(indexName, operation);
}

/// <summary>
/// Fluent builder for Elasticsearch endpoint URIs.
/// </summary>
public sealed class ElasticsearchBuilder
{
    private readonly string _indexName;
    private readonly ElasticsearchOperationType? _operation;

    // Connection
    private string? _nodes;
    private string? _apiKey;
    private string? _username;
    private string? _password;
    private string? _certificateFingerprint;
    private string? _connectionFactory;
    private bool _enableDebugMode;

    // Timeouts
    private string? _requestTimeout;
    private string? _pingTimeout;
    private string? _deadTimeout;
    private string? _maxDeadTimeout;
    private string? _maxRetries;

    // Common
    private string? _pipeline;
    private string? _routing;
    private string? _documentId;
    private string? _refresh;
    private string? _bulkSize;

    // Consumer
    private string? _delay;
    private string? _initialDelay;
    private string? _query;
    private string? _size;
    private string? _scrollTimeout;
    private string? _sort;
    private bool _deleteAfterRead;
    private bool? _trackTotalHits;
    private string? _sourceIncludes;
    private string? _sourceExcludes;

    internal ElasticsearchBuilder(string indexName, ElasticsearchOperationType? operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        _indexName = indexName;
        _operation = operation;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Comma-separated node URIs.</summary>
    public ElasticsearchBuilder Nodes(string nodes) { _nodes = nodes; return this; }
    /// <summary>Node URIs from an expression.</summary>
    public ElasticsearchBuilder Nodes(IExpression nodes) { _nodes = nodes.ToTemplateString(); return this; }

    /// <summary>API key authentication.</summary>
    public ElasticsearchBuilder ApiKey(string key) { _apiKey = key; return this; }
    /// <summary>API key from expression.</summary>
    public ElasticsearchBuilder ApiKey(IExpression key) { _apiKey = key.ToTemplateString(); return this; }

    /// <summary>Basic auth username.</summary>
    public ElasticsearchBuilder Username(string user) { _username = user; return this; }

    /// <summary>Basic auth password.</summary>
    public ElasticsearchBuilder Password(string pass) { _password = pass; return this; }

    /// <summary>TLS certificate fingerprint.</summary>
    public ElasticsearchBuilder CertificateFingerprint(string fp) { _certificateFingerprint = fp; return this; }

    /// <summary>Named connection factory from registry.</summary>
    public ElasticsearchBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Enable debug mode for request/response logging.</summary>
    public ElasticsearchBuilder DebugMode() { _enableDebugMode = true; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  TIMEOUTS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Request timeout in milliseconds.</summary>
    public ElasticsearchBuilder RequestTimeout(int ms) { _requestTimeout = ms.ToString(); return this; }

    /// <summary>Ping timeout in milliseconds.</summary>
    public ElasticsearchBuilder PingTimeout(int ms) { _pingTimeout = ms.ToString(); return this; }

    /// <summary>Dead node backoff in milliseconds.</summary>
    public ElasticsearchBuilder DeadTimeout(int ms) { _deadTimeout = ms.ToString(); return this; }

    /// <summary>Max dead node backoff in milliseconds.</summary>
    public ElasticsearchBuilder MaxDeadTimeout(int ms) { _maxDeadTimeout = ms.ToString(); return this; }

    /// <summary>Maximum retries per request.</summary>
    public ElasticsearchBuilder MaxRetries(int n) { _maxRetries = n.ToString(); return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Ingest pipeline name.</summary>
    public ElasticsearchBuilder Pipeline(string name) { _pipeline = name; return this; }

    /// <summary>Custom routing value.</summary>
    public ElasticsearchBuilder Routing(string value) { _routing = value; return this; }
    /// <summary>Routing from expression.</summary>
    public ElasticsearchBuilder Routing(IExpression value) { _routing = value.ToTemplateString(); return this; }

    /// <summary>Dynamic document ID expression.</summary>
    public ElasticsearchBuilder DocumentId(string expr) { _documentId = expr; return this; }
    /// <summary>Dynamic document ID from expression.</summary>
    public ElasticsearchBuilder DocumentId(IExpression expr) { _documentId = expr.ToTemplateString(); return this; }

    /// <summary>Refresh policy: "true", "false", or "wait_for".</summary>
    public ElasticsearchBuilder Refresh(string policy) { _refresh = policy; return this; }

    /// <summary>Max documents per bulk request.</summary>
    public ElasticsearchBuilder BulkSize(int n) { _bulkSize = n.ToString(); return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Polling delay in milliseconds. Default 5000.</summary>
    public ElasticsearchBuilder Delay(int ms) { _delay = ms.ToString(); return this; }
    /// <summary>Polling delay from expression.</summary>
    public ElasticsearchBuilder Delay(IExpression ms) { _delay = ms.ToTemplateString(); return this; }

    /// <summary>Initial delay before first poll in milliseconds.</summary>
    public ElasticsearchBuilder InitialDelay(int ms) { _initialDelay = ms.ToString(); return this; }

    /// <summary>JSON query DSL string.</summary>
    public ElasticsearchBuilder Query(string json) { _query = json; return this; }

    /// <summary>Hits per search page. Default 100.</summary>
    public ElasticsearchBuilder Size(int n) { _size = n.ToString(); return this; }

    /// <summary>Scroll timeout (e.g. "1m"). Enables Scroll API.</summary>
    public ElasticsearchBuilder ScrollTimeout(string timeout) { _scrollTimeout = timeout; return this; }

    /// <summary>Sort fields: "field:asc,field2:desc".</summary>
    public ElasticsearchBuilder Sort(string sort) { _sort = sort; return this; }

    /// <summary>Delete documents after successful consumption.</summary>
    public ElasticsearchBuilder DeleteAfterRead(bool v = true) { _deleteAfterRead = v; return this; }

    /// <summary>Track total hit count.</summary>
    public ElasticsearchBuilder TrackTotalHits(bool v = true) { _trackTotalHits = v; return this; }

    /// <summary>Source fields to include.</summary>
    public ElasticsearchBuilder SourceIncludes(string fields) { _sourceIncludes = fields; return this; }

    /// <summary>Source fields to exclude.</summary>
    public ElasticsearchBuilder SourceExcludes(string fields) { _sourceExcludes = fields; return this; }

    // ═══════════════════════════════════════════════════════════════════
    //  BUILD
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Builds the Elasticsearch endpoint URI string using the full scheme.</summary>
    public string Build() => BuildUri("elasticsearch");

    /// <summary>Builds the short-form URI using <c>es://</c>.</summary>
    public string BuildShort() => BuildUri("es");

    private string BuildUri(string scheme)
    {
        var sb = new StringBuilder();
        sb.Append(scheme).Append("://");

        if (_operation.HasValue)
        {
            sb.Append(_operation.Value);
            sb.Append(':');
        }
        sb.Append(_indexName);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (!string.IsNullOrEmpty(value)) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Connection
        AppendIf("nodes", _nodes);
        AppendIf("apiKey", _apiKey);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("certificateFingerprint", _certificateFingerprint);
        AppendIf("connectionFactory", _connectionFactory);
        AppendBool("enableDebugMode", _enableDebugMode);

        // Timeouts
        AppendIf("requestTimeout", _requestTimeout);
        AppendIf("pingTimeout", _pingTimeout);
        AppendIf("deadTimeout", _deadTimeout);
        AppendIf("maxDeadTimeout", _maxDeadTimeout);
        AppendIf("maxRetries", _maxRetries);

        // Common
        AppendIf("pipeline", _pipeline);
        AppendIf("routing", _routing);
        AppendIf("documentId", _documentId);
        AppendIf("refresh", _refresh);
        AppendIf("bulkSize", _bulkSize);

        // Consumer
        AppendIf("delay", _delay);
        AppendIf("initialDelay", _initialDelay);
        AppendIf("query", _query);
        AppendIf("size", _size);
        AppendIf("scrollTimeout", _scrollTimeout);
        AppendIf("sort", _sort);
        AppendBool("deleteAfterRead", _deleteAfterRead);
        AppendBoolExplicit("trackTotalHits", _trackTotalHits);
        AppendIf("sourceIncludes", _sourceIncludes);
        AppendIf("sourceExcludes", _sourceExcludes);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string (full scheme).</summary>
    public static implicit operator string(ElasticsearchBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
