using StackExchange.Redis;

namespace redb.Route.Redis;

/// <summary>
/// Connection factory for Redis. Register via DI or in the route registry
/// and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>).
/// </summary>
public sealed class RedisConnectionFactory
{
    /// <summary>Redis connection string (e.g., "localhost:6379" or "host1:6379,host2:6379").</summary>
    public string ConnectionString { get; set; } = "localhost:6379";

    /// <summary>Default database index (default: 0).</summary>
    public int Database { get; set; }

    /// <summary>Redis password (optional).</summary>
    public string? Password { get; set; }

    /// <summary>Redis ACL username (Redis 6+). Use together with Password for ACL authentication.</summary>
    public string? User { get; set; }

    /// <summary>Client name for identification in Redis INFO.</summary>
    public string ClientName { get; set; } = "redb.Route";

    // ── Timeouts ──

    /// <summary>Connection timeout in milliseconds (default: 5000).</summary>
    public int ConnectTimeout { get; set; } = 5000;

    /// <summary>Synchronous operation timeout in milliseconds (default: 5000).</summary>
    public int SyncTimeout { get; set; } = 5000;

    /// <summary>Async operation timeout in milliseconds (default: 5000).</summary>
    public int AsyncTimeout { get; set; } = 5000;

    /// <summary>Number of connection retries (default: 3).</summary>
    public int ConnectRetry { get; set; } = 3;

    /// <summary>Keep-alive interval in seconds (default: 60).</summary>
    public int KeepAlive { get; set; } = 60;

    /// <summary>Abort on connection fail (default: false — allows reconnect).</summary>
    public bool AbortOnConnectFail { get; set; }

    /// <summary>Allow admin commands (default: false).</summary>
    public bool AllowAdmin { get; set; }

    // ── SSL ──

    /// <summary>Enable SSL encryption.</summary>
    public bool Ssl { get; set; }

    /// <summary>SSL host for certificate validation.</summary>
    public string? SslHost { get; set; }

    /// <summary>SSL/TLS protocol versions (e.g. "Tls12", "Tls13"). Default = system default.</summary>
    public string? SslProtocols { get; set; }

    /// <summary>Enable certificate revocation checking (default: true).</summary>
    public bool CheckCertificateRevocation { get; set; } = true;

    // ── Advanced ──

    /// <summary>Include detailed diagnostics in exceptions.</summary>
    public bool IncludeDetailInExceptions { get; set; } = true;

    /// <summary>Include performance counters in exceptions.</summary>
    public bool IncludePerformanceCountersInExceptions { get; set; }

    // ── Sentinel / Cluster / HA ──

    /// <summary>Redis Sentinel service name. Required for Sentinel-based HA setups.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Tie-breaker key for master detection in multi-master scenarios.</summary>
    public string? TieBreaker { get; set; }

    /// <summary>
    /// Reconnect retry policy: "exponential" or "linear" (default: exponential).
    /// Controls backoff during reconnection attempts.
    /// </summary>
    public string ReconnectRetryPolicy { get; set; } = "exponential";

    /// <summary>Channel prefix for Pub/Sub (optional).</summary>
    public string? ChannelPrefix { get; set; }

    /// <summary>Creates <see cref="ConfigurationOptions"/> from this factory's settings.</summary>
    public ConfigurationOptions Build()
    {
        var config = ConfigurationOptions.Parse(ConnectionString);

        config.DefaultDatabase = Database;
        config.ClientName = ClientName;
        config.ConnectTimeout = ConnectTimeout;
        config.SyncTimeout = SyncTimeout;
        config.AsyncTimeout = AsyncTimeout;
        config.ConnectRetry = ConnectRetry;
        config.KeepAlive = KeepAlive;
        config.AbortOnConnectFail = AbortOnConnectFail;
        config.AllowAdmin = AllowAdmin;
        config.Ssl = Ssl;
        config.IncludeDetailInExceptions = IncludeDetailInExceptions;
        config.IncludePerformanceCountersInExceptions = IncludePerformanceCountersInExceptions;
        config.CheckCertificateRevocation = CheckCertificateRevocation;

        if (!string.IsNullOrEmpty(Password))
            config.Password = Password;

        if (!string.IsNullOrEmpty(User))
            config.User = User;

        if (!string.IsNullOrEmpty(SslHost))
            config.SslHost = SslHost;

        if (!string.IsNullOrEmpty(SslProtocols) && Enum.TryParse<System.Security.Authentication.SslProtocols>(SslProtocols, true, out var protocols))
            config.SslProtocols = protocols;

        if (!string.IsNullOrEmpty(ServiceName))
            config.ServiceName = ServiceName;

        if (!string.IsNullOrEmpty(TieBreaker))
            config.TieBreaker = TieBreaker;

        config.ReconnectRetryPolicy = ReconnectRetryPolicy?.Trim().ToLowerInvariant() switch
        {
            "linear" => new LinearRetry(ConnectTimeout),
            _ => new ExponentialRetry(Math.Min(ConnectTimeout, 5000)),
        };

        if (!string.IsNullOrEmpty(ChannelPrefix))
            config.ChannelPrefix = new RedisChannel(ChannelPrefix, RedisChannel.PatternMode.Literal);

        return config;
    }
}
