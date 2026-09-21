using Confluent.Kafka;

namespace redb.Route.Kafka;

/// <summary>
/// Connection factory for Kafka. Register via DI or in the route registry
/// and reference by name in endpoint URIs (<c>connectionFactory=myFactory</c>).
/// Centralises broker addresses and security settings so they can be shared
/// across multiple Kafka endpoints without duplicating URI parameters.
/// </summary>
public sealed class KafkaConnectionFactory
{
    /// <summary>Comma-separated list of Kafka broker addresses (e.g., "broker1:9092,broker2:9092").</summary>
    public string Brokers { get; set; } = "localhost:9092";

    // ── Security ──

    /// <summary>Security protocol: Plaintext, Ssl, SaslPlaintext, SaslSsl.</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";

    /// <summary>SASL mechanism: Plain, ScramSha256, ScramSha512, OAuthBearer.</summary>
    public string? SaslMechanism { get; set; }

    /// <summary>SASL username.</summary>
    public string? SaslUsername { get; set; }

    /// <summary>SASL password.</summary>
    [redb.Route.Core.Sensitive]
    public string? SaslPassword { get; set; }

    // ── SSL/TLS ──

    /// <summary>CA certificate file path for SSL.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>Client certificate file path (for mTLS).</summary>
    public string? SslCertificateLocation { get; set; }

    /// <summary>Client private key file path (for mTLS).</summary>
    public string? SslKeyLocation { get; set; }

    /// <summary>Client private key passphrase.</summary>
    [redb.Route.Core.Sensitive]
    public string? SslKeyPassword { get; set; }

    /// <summary>SSL endpoint identification algorithm (e.g., "https" for hostname verification). Empty = disabled.</summary>
    public string? SslEndpointIdentificationAlgorithm { get; set; }

    // ── Producer defaults ──

    /// <summary>Default acknowledgment level: None, Leader, All. Default All, as in the Kafka 3 client and Camel 4.</summary>
    public string Acks { get; set; } = "All";

    /// <summary>Default number of retries for failed sends.</summary>
    public int Retries { get; set; } = 3;

    // ── Consumer defaults ──

    /// <summary>Default consumer group ID.</summary>
    public string? GroupId { get; set; }

    /// <summary>Default auto-offset reset: Latest, Earliest, Error.</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    // ── Consumer cluster tuning ──

    /// <summary>Static group instance ID for consumer. Avoids rebalance storms during rolling deploys (K8s).</summary>
    public string? GroupInstanceId { get; set; }

    /// <summary>Consumer session timeout in ms (default: 45000). Lower = faster dead-consumer detection.</summary>
    public int? SessionTimeoutMs { get; set; }

    /// <summary>Consumer heartbeat interval in ms (default: 3000). Should be &lt; SessionTimeoutMs/3.</summary>
    public int? HeartbeatIntervalMs { get; set; }

    /// <summary>Max time between polls in ms (default: 300000). Prevents endless processing from triggering rebalance.</summary>
    public int? MaxPollIntervalMs { get; set; }

    /// <summary>Partition assignment strategy: Range, RoundRobin, CooperativeSticky. CooperativeSticky avoids stop-the-world rebalance.</summary>
    public string? PartitionAssignmentStrategy { get; set; }

    /// <summary>
    /// Isolation level: ReadUncommitted, ReadCommitted. ReadCommitted hides records of aborted
    /// transactions written by EOS producers elsewhere; this connector itself is at-least-once
    /// (see docs/KAFKA_TRANSACTIONS_TODO.md).
    /// </summary>
    public string? IsolationLevel { get; set; }

    // ── Producer tuning ──

    /// <summary>Batching delay in ms (default: 5). Higher = more batching. 0 = no batching.</summary>
    public int? LingerMs { get; set; }

    /// <summary>Max batch size in bytes (default: 1000000).</summary>
    public int? BatchSize { get; set; }

    /// <summary>Compression type: None, Gzip, Snappy, Lz4, Zstd.</summary>
    public string? CompressionType { get; set; }

    /// <summary>Total delivery timeout in ms (default: 120000). Includes retries.</summary>
    public int? MessageTimeoutMs { get; set; }

    // ── Reconnect ──

    /// <summary>Reconnect backoff initial in ms (default: 100).</summary>
    public int? ReconnectBackoffMs { get; set; }

    /// <summary>Reconnect backoff max in ms (default: 10000).</summary>
    public int? ReconnectBackoffMaxMs { get; set; }

    // ── Advanced ──

    /// <summary>Client ID for broker identification.</summary>
    public string ClientId { get; set; } = "redb.Route";

    /// <summary>Request timeout in ms (default: 30000).</summary>
    public int RequestTimeoutMs { get; set; } = 30_000;

    /// <summary>Metadata max age in ms before refresh (default: 300000).</summary>
    public int MetadataMaxAgeMs { get; set; } = 300_000;

    /// <summary>Socket timeout in ms (default: 60000).</summary>
    public int SocketTimeoutMs { get; set; } = 60_000;

    /// <summary>Max in-flight requests per connection (default: 5). Set to 1 for strict ordering.</summary>
    public int MaxInFlight { get; set; } = 5;

    /// <summary>
    /// Additional librdkafka properties not exposed as typed settings.
    /// Keys are librdkafka config names (e.g. "socket.keepalive.enable").
    /// Applied last — overrides any typed property.
    /// </summary>
    public Dictionary<string, string> AdditionalProperties { get; set; } = new();

    /// <summary>
    /// Builds a <see cref="ConsumerConfig"/> with all factory settings.
    /// Endpoint-specific overrides (GroupId, AutoOffsetReset, etc.) take precedence when applied afterwards.
    /// </summary>
    public ConsumerConfig BuildConsumerConfig(string? groupIdOverride = null)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = Brokers,
            GroupId = groupIdOverride ?? GroupId,
            AutoOffsetReset = ParseAutoOffsetReset(),
            EnableAutoCommit = false,
            ClientId = ClientId,
            MetadataMaxAgeMs = MetadataMaxAgeMs,
            SocketTimeoutMs = SocketTimeoutMs,
            MaxInFlight = MaxInFlight,
        };

        if (!string.IsNullOrEmpty(GroupInstanceId)) config.GroupInstanceId = GroupInstanceId;
        if (SessionTimeoutMs.HasValue) config.SessionTimeoutMs = SessionTimeoutMs.Value;
        if (HeartbeatIntervalMs.HasValue) config.HeartbeatIntervalMs = HeartbeatIntervalMs.Value;
        if (MaxPollIntervalMs.HasValue) config.MaxPollIntervalMs = MaxPollIntervalMs.Value;
        if (ReconnectBackoffMs.HasValue) config.ReconnectBackoffMs = ReconnectBackoffMs.Value;
        if (ReconnectBackoffMaxMs.HasValue) config.ReconnectBackoffMaxMs = ReconnectBackoffMaxMs.Value;

        if (!string.IsNullOrEmpty(PartitionAssignmentStrategy))
            config.PartitionAssignmentStrategy = ParsePartitionAssignment();
        if (!string.IsNullOrEmpty(IsolationLevel))
            config.IsolationLevel = ParseIsolationLevel();

        ApplySecurity(config);
        ApplySsl(config);
        ApplyAdditionalProperties(config);
        return config;
    }

    /// <summary>
    /// Builds a <see cref="ProducerConfig"/> with all factory settings.
    /// </summary>
    public ProducerConfig BuildProducerConfig()
    {
        var config = new ProducerConfig
        {
            BootstrapServers = Brokers,
            Acks = ParseAcks(),
            MessageSendMaxRetries = Retries,
            ClientId = ClientId,
            RequestTimeoutMs = RequestTimeoutMs,
            MetadataMaxAgeMs = MetadataMaxAgeMs,
            SocketTimeoutMs = SocketTimeoutMs,
            MaxInFlight = MaxInFlight,
        };

        if (LingerMs.HasValue) config.LingerMs = LingerMs.Value;
        if (BatchSize.HasValue) config.BatchSize = BatchSize.Value;
        if (MessageTimeoutMs.HasValue) config.MessageTimeoutMs = MessageTimeoutMs.Value;
        if (ReconnectBackoffMs.HasValue) config.ReconnectBackoffMs = ReconnectBackoffMs.Value;
        if (ReconnectBackoffMaxMs.HasValue) config.ReconnectBackoffMaxMs = ReconnectBackoffMaxMs.Value;
        if (!string.IsNullOrEmpty(CompressionType))
            config.CompressionType = ParseCompressionType();

        ApplySecurity(config);
        ApplySsl(config);
        ApplyAdditionalProperties(config);
        return config;
    }

    /// <summary>
    /// Builds an <see cref="AdminClientConfig"/> for metadata queries.
    /// </summary>
    public AdminClientConfig BuildAdminConfig()
    {
        var config = new AdminClientConfig
        {
            BootstrapServers = Brokers,
            ClientId = ClientId,
            SocketTimeoutMs = SocketTimeoutMs,
        };

        ApplySecurity(config);
        ApplySsl(config);
        ApplyAdditionalProperties(config);
        return config;
    }

    // ── Helpers ──

    // Волна A1: strict parses shared with the endpoint options — a typo throws instead of
    // silently keeping the default (for securityProtocol the default is plaintext).
    private Confluent.Kafka.AutoOffsetReset ParseAutoOffsetReset() =>
        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.AutoOffsetReset>(AutoOffsetReset, "autoOffsetReset");

    private Confluent.Kafka.Acks ParseAcks() => KafkaOptionParsers.ParseAcks(Acks);

    private void ApplySecurity(ClientConfig config)
    {
        config.SecurityProtocol =
            KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SecurityProtocol>(SecurityProtocol, "securityProtocol");

        if (!string.IsNullOrWhiteSpace(SaslMechanism))
        {
            var mechanism = KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SaslMechanism>(SaslMechanism, "saslMechanism");
            KafkaOptionParsers.RequireSaslCredentials(mechanism, SaslUsername, SaslPassword);
            config.SaslMechanism = mechanism;
            config.SaslUsername = SaslUsername;
            config.SaslPassword = SaslPassword;
        }
    }

    private void ApplySsl(ClientConfig config)
    {
        if (!string.IsNullOrEmpty(SslCaLocation)) config.SslCaLocation = SslCaLocation;
        if (!string.IsNullOrEmpty(SslCertificateLocation)) config.SslCertificateLocation = SslCertificateLocation;
        if (!string.IsNullOrEmpty(SslKeyLocation)) config.SslKeyLocation = SslKeyLocation;
        if (!string.IsNullOrEmpty(SslKeyPassword)) config.SslKeyPassword = SslKeyPassword;
        if (SslEndpointIdentificationAlgorithm is not null)
            config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm == string.Empty
                ? Confluent.Kafka.SslEndpointIdentificationAlgorithm.None
                : Confluent.Kafka.SslEndpointIdentificationAlgorithm.Https;
    }

    private void ApplyAdditionalProperties(ClientConfig config)
    {
        foreach (var kvp in AdditionalProperties)
            config.Set(kvp.Key, kvp.Value);
    }

    private Confluent.Kafka.PartitionAssignmentStrategy ParsePartitionAssignment() =>
        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.PartitionAssignmentStrategy>(
            PartitionAssignmentStrategy!, "partitionAssignmentStrategy");

    private Confluent.Kafka.IsolationLevel ParseIsolationLevel() =>
        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.IsolationLevel>(IsolationLevel!, "isolationLevel");

    private Confluent.Kafka.CompressionType ParseCompressionType() =>
        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.CompressionType>(CompressionType!, "compressionType");
}
