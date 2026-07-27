using redb.Route.Core;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Endpoint options for Elasticsearch transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>elasticsearch://index-name?nodes=http://localhost:9200</c>
/// </para>
/// <para>
/// Short alias: <c>es://index-name?nodes=http://localhost:9200</c>
/// </para>
/// <para>
/// For producer operations: <c>es:Search:index-name?nodes=http://localhost:9200&amp;query={"match_all":{}}</c>
/// </para>
/// </summary>
public class ElasticsearchEndpointOptions : EndpointOptions
{
    // ═══════════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Comma-separated list of Elasticsearch node URIs.
    /// Example: <c>http://localhost:9200</c> or <c>http://node1:9200,http://node2:9200</c>.
    /// </summary>
    public string Nodes { get; set; } = "http://localhost:9200";

    /// <summary>API key authentication (base64-encoded).</summary>
    [Sensitive]
    public string ApiKey { get; set; } = "";

    /// <summary>Basic auth username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Basic auth password.</summary>
    [Sensitive]
    public string Password { get; set; } = "";

    /// <summary>SHA-256 certificate fingerprint for TLS verification.</summary>
    public string CertificateFingerprint { get; set; } = "";

    /// <summary>
    /// Named connection factory reference. If set, looks up an <see cref="ElasticsearchConnectionFactory"/>
    /// from the DI registry by this name to build the client.
    /// </summary>
    public string ConnectionFactory { get; set; } = "";

    /// <summary>Enable debug mode — log raw requests/responses. (default: false)</summary>
    public bool EnableDebugMode { get; set; }

    // ═══════════════════════════════════════════════════════════════════
    //  TIMEOUTS / RESILIENCE
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Request timeout in milliseconds. (default: 30000)</summary>
    public int RequestTimeout { get; set; } = 30_000;

    /// <summary>Ping timeout in milliseconds. (default: 2000)</summary>
    public int PingTimeout { get; set; } = 2000;

    /// <summary>Dead node backoff timeout in milliseconds. (default: 60000)</summary>
    public int DeadTimeout { get; set; } = 60_000;

    /// <summary>Max dead node backoff timeout in milliseconds. (default: 600000)</summary>
    public int MaxDeadTimeout { get; set; } = 600_000;

    /// <summary>Maximum number of retries per request. (default: 3)</summary>
    public int MaxRetries { get; set; } = 3;

    // ═══════════════════════════════════════════════════════════════════
    //  COMMON
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Producer operation type. Parsed from URI path prefix or query param.
    /// (default: <see cref="ElasticsearchOperationType.Index"/>)
    /// </summary>
    public ElasticsearchOperationType Operation { get; set; } = ElasticsearchOperationType.Index;

    /// <summary>Ingest pipeline name to apply on index/bulk operations.</summary>
    public string Pipeline { get; set; } = "";

    /// <summary>Custom routing value.</summary>
    public string Routing { get; set; } = "";

    /// <summary>
    /// Dynamic document ID expression. Supports <c>${header.redbEs.DocumentId}</c> etc.
    /// If not set, Elasticsearch auto-generates an ID (for Index) or the header is used.
    /// </summary>
    public DynamicValue<string>? DocumentId { get; set; }

    /// <summary>Refresh policy: "true", "false", or "wait_for". (default: "false")</summary>
    public string Refresh { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  BULK SETTINGS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Max documents per bulk request. (default: 100)</summary>
    public int BulkSize { get; set; } = 100;

    // ═══════════════════════════════════════════════════════════════════
    //  CONSUMER (polling search)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Polling delay between search cycles in milliseconds. (default: 5000)</summary>
    public int Delay { get; set; } = 5000;

    /// <summary>Initial delay before first poll in milliseconds. (default: 1000)</summary>
    public int InitialDelay { get; set; } = 1000;

    /// <summary>
    /// JSON query DSL string. If empty, uses <c>match_all</c>.
    /// Example: <c>{"term":{"status":"active"}}</c>
    /// </summary>
    public string Query { get; set; } = "";

    /// <summary>Number of hits per search page. (default: 100, max 10000)</summary>
    public int Size { get; set; } = 100;

    /// <summary>
    /// Scroll timeout for Scroll API (e.g. "1m", "5m"). If empty, uses search_after pagination.
    /// </summary>
    public string ScrollTimeout { get; set; } = "";

    /// <summary>
    /// Sort field(s) for ordering. Format: <c>timestamp:desc,_id:asc</c>.
    /// Required for search_after pagination. If empty, defaults to <c>_doc:asc</c>.
    /// </summary>
    public string Sort { get; set; } = "";

    /// <summary>Delete document after successful processing. (default: false)</summary>
    public bool DeleteAfterRead { get; set; }

    /// <summary>Track total hit count. (default: true)</summary>
    public bool TrackTotalHits { get; set; } = true;

    /// <summary>
    /// Comma-separated list of fields to include in _source.
    /// Example: <c>title,author,timestamp</c>
    /// </summary>
    public string SourceIncludes { get; set; } = "";

    /// <summary>
    /// Comma-separated list of fields to exclude from _source.
    /// Example: <c>large_blob,internal_field</c>
    /// </summary>
    public string SourceExcludes { get; set; } = "";

    // ═══════════════════════════════════════════════════════════════════
    //  VALIDATION
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Nodes) && string.IsNullOrWhiteSpace(ConnectionFactory))
            throw new ArgumentOutOfRangeException(nameof(Nodes), "Nodes or ConnectionFactory is required");

        if (Delay < 100)
            throw new ArgumentOutOfRangeException(nameof(Delay), "Delay must be >= 100ms");

        if (Size < 1 || Size > 10_000)
            throw new ArgumentOutOfRangeException(nameof(Size), "Size must be 1-10000");

        if (BulkSize < 1)
            throw new ArgumentOutOfRangeException(nameof(BulkSize), "BulkSize must be >= 1");

        if (RequestTimeout < 1000)
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), "RequestTimeout must be >= 1000ms");

        if (MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "MaxRetries must be >= 0");
    }
}
