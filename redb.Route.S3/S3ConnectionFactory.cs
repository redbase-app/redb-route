using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace redb.Route.S3;

/// <summary>
/// Connection factory for S3/MinIO. Register via DI or in the route registry
/// and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>).
/// <para>
/// Supports AWS S3, MinIO, Dell ECS, Ceph, and other S3-compatible stores.
/// </para>
/// </summary>
public sealed class S3ConnectionFactory
{
    /// <summary>
    /// Custom service URL for S3-compatible stores (MinIO, Dell ECS, Ceph).
    /// Example: <c>http://localhost:9000</c>.
    /// </summary>
    public string ServiceUrl { get; set; } = "";

    /// <summary>AWS region. (default: "us-east-1")</summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>AWS access key ID.</summary>
    public string AccessKey { get; set; } = "";

    /// <summary>AWS secret access key.</summary>
    public string SecretKey { get; set; } = "";

    /// <summary>AWS session token (for temporary STS credentials).</summary>
    public string SessionToken { get; set; } = "";

    /// <summary>AWS named profile from ~/.aws/credentials.</summary>
    public string ProfileName { get; set; } = "";

    /// <summary>
    /// Use path-style URLs (required for MinIO and most S3-compatible stores).
    /// (default: false)
    /// </summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>
    /// Use the default AWS credentials provider chain.
    /// (default: false)
    /// </summary>
    public bool UseDefaultCredentialsProvider { get; set; }

    /// <summary>Trust all SSL certificates. (default: false)</summary>
    public bool TrustAllCertificates { get; set; }

    // ── Timeouts ──

    /// <summary>Connection timeout in milliseconds. (default: 30000)</summary>
    public int ConnectionTimeout { get; set; } = 30_000;

    /// <summary>Socket/read timeout in milliseconds. (default: 60000)</summary>
    public int SocketTimeout { get; set; } = 60_000;

    /// <summary>Maximum concurrent connections. (default: 50)</summary>
    public int MaxConnections { get; set; } = 50;

    /// <summary>Number of retry attempts. (default: 3)</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>Retry mode: "standard" or "adaptive". (default: "standard")</summary>
    public string RetryMode { get; set; } = "standard";

    // ── Proxy ──

    /// <summary>HTTP proxy host.</summary>
    public string ProxyHost { get; set; } = "";

    /// <summary>HTTP proxy port.</summary>
    public int ProxyPort { get; set; }

    /// <summary>
    /// Creates an <see cref="AmazonS3Config"/> and an <see cref="IAmazonS3"/> client
    /// from this factory's settings.
    /// </summary>
    public IAmazonS3 Build()
    {
        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(Region),
            ForcePathStyle = ForcePathStyle,
            Timeout = TimeSpan.FromMilliseconds(ConnectionTimeout),
            MaxConnectionsPerServer = MaxConnections,
            MaxErrorRetry = RetryCount,
        };

        if (!string.IsNullOrEmpty(ServiceUrl))
        {
            config.ServiceURL = ServiceUrl;
            // When using custom service URL, region endpoint must be null
            config.RegionEndpoint = null;
            config.AuthenticationRegion = Region;
        }

        if (!string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0)
        {
            config.ProxyHost = ProxyHost;
            config.ProxyPort = ProxyPort;
        }

        var credentials = ResolveCredentials();
        return new AmazonS3Client(credentials, config);
    }

    /// <summary>
    /// Creates an <see cref="AmazonS3Config"/> from this factory's settings
    /// without building the client.
    /// </summary>
    internal AmazonS3Config BuildConfig()
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = ForcePathStyle,
            Timeout = TimeSpan.FromMilliseconds(ConnectionTimeout),
            MaxConnectionsPerServer = MaxConnections,
            MaxErrorRetry = RetryCount,
        };

        if (!string.IsNullOrEmpty(ServiceUrl))
        {
            config.ServiceURL = ServiceUrl;
            config.AuthenticationRegion = Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(Region);
        }

        if (!string.IsNullOrEmpty(ProxyHost) && ProxyPort > 0)
        {
            config.ProxyHost = ProxyHost;
            config.ProxyPort = ProxyPort;
        }

        return config;
    }

    private AWSCredentials ResolveCredentials()
    {
        if (UseDefaultCredentialsProvider)
#pragma warning disable CS0618 // FallbackCredentialsFactory is deprecated but still functional
            return FallbackCredentialsFactory.GetCredentials();
#pragma warning restore CS0618

        if (!string.IsNullOrEmpty(ProfileName))
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(ProfileName, out var profileCreds))
                return profileCreds;
            throw new InvalidOperationException($"AWS profile '{ProfileName}' not found.");
        }

        if (!string.IsNullOrEmpty(SessionToken))
            return new SessionAWSCredentials(AccessKey, SecretKey, SessionToken);

        return new BasicAWSCredentials(AccessKey, SecretKey);
    }
}
