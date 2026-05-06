using Elastic.Clients.Elasticsearch;
using Elastic.Transport;

namespace redb.Route.Elasticsearch;

/// <summary>
/// Connection factory for Elasticsearch. Register via DI or in the route registry
/// and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>).
/// </summary>
public sealed class ElasticsearchConnectionFactory
{
    /// <summary>Comma-separated list of Elasticsearch node URIs.</summary>
    public string Nodes { get; set; } = "http://localhost:9200";

    /// <summary>API key authentication (base64-encoded).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Basic auth username.</summary>
    public string Username { get; set; } = "";

    /// <summary>Basic auth password.</summary>
    public string Password { get; set; } = "";

    /// <summary>SHA-256 certificate fingerprint for TLS verification.</summary>
    public string CertificateFingerprint { get; set; } = "";

    /// <summary>Enable debug mode. (default: false)</summary>
    public bool EnableDebugMode { get; set; }

    /// <summary>Enable node sniffing for cluster discovery. (default: false)</summary>
    public bool EnableSniffing { get; set; }

    /// <summary>Request timeout in milliseconds. (default: 30000)</summary>
    public int RequestTimeout { get; set; } = 30_000;

    /// <summary>Ping timeout in milliseconds. (default: 2000)</summary>
    public int PingTimeout { get; set; } = 2000;

    /// <summary>Dead node backoff timeout in milliseconds. (default: 60000)</summary>
    public int DeadTimeout { get; set; } = 60_000;

    /// <summary>Max dead node backoff timeout in milliseconds. (default: 600000)</summary>
    public int MaxDeadTimeout { get; set; } = 600_000;

    /// <summary>Maximum retries per request. (default: 3)</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Creates an <see cref="ElasticsearchClient"/> from this factory's settings.
    /// </summary>
    public ElasticsearchClient Build()
    {
        var nodeUris = Nodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => new Uri(n))
            .ToArray();

        ElasticsearchClientSettings settings;

        if (nodeUris.Length == 1)
        {
            settings = new ElasticsearchClientSettings(nodeUris[0]);
        }
        else if (EnableSniffing)
        {
            settings = new ElasticsearchClientSettings(new SniffingNodePool(nodeUris));
        }
        else
        {
            settings = new ElasticsearchClientSettings(new StaticNodePool(nodeUris));
        }

        // Auth
        if (!string.IsNullOrWhiteSpace(ApiKey))
            settings.Authentication(new ApiKey(ApiKey));
        else if (!string.IsNullOrWhiteSpace(Username))
            settings.Authentication(new BasicAuthentication(Username, Password ?? ""));

        // TLS
        if (!string.IsNullOrWhiteSpace(CertificateFingerprint))
            settings.CertificateFingerprint(CertificateFingerprint);

        // Timeouts
        settings.RequestTimeout(TimeSpan.FromMilliseconds(RequestTimeout));
        settings.PingTimeout(TimeSpan.FromMilliseconds(PingTimeout));
        settings.DeadTimeout(TimeSpan.FromMilliseconds(DeadTimeout));
        settings.MaxDeadTimeout(TimeSpan.FromMilliseconds(MaxDeadTimeout));
        settings.MaximumRetries(MaxRetries);

        if (EnableDebugMode)
            settings.EnableDebugMode();

        return new ElasticsearchClient(settings);
    }
}
