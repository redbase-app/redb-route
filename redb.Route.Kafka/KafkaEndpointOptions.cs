using Confluent.Kafka;
using redb.Route.Core;

namespace redb.Route.Kafka;

/// <summary>
/// Typed options for <see cref="KafkaEndpoint"/>. Bound from URI query parameters via reflection.
/// </summary>
public sealed class KafkaEndpointOptions : EndpointOptions
{
    // ── General ──

    /// <summary>Comma-separated list of Kafka broker addresses (required).</summary>
    public string Brokers { get; set; } = string.Empty;

    /// <summary>Security protocol for broker communication.</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";

    /// <summary>SASL mechanism (Plain, ScramSha256, ScramSha512, etc.).</summary>
    public string? SaslMechanism { get; set; }

    /// <summary>SASL username.</summary>
    public string? SaslUsername { get; set; }

    /// <summary>SASL password.</summary>
    public string? SaslPassword { get; set; }

    // ── SSL/TLS ──

    /// <summary>CA certificate file path for SSL.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>Client certificate file path (for mTLS).</summary>
    public string? SslCertificateLocation { get; set; }

    /// <summary>Client private key file path (for mTLS).</summary>
    public string? SslKeyLocation { get; set; }

    /// <summary>Client private key passphrase.</summary>
    public string? SslKeyPassword { get; set; }

    /// <summary>Name of <see cref="KafkaConnectionFactory"/> in the route registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Consumer ──

    /// <summary>Consumer group identifier (required for consumers).</summary>
    public string? GroupId { get; set; }

    /// <summary>Where to start consuming when no committed offset exists.</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    /// <summary>Max messages per batch (0 = single-message mode).</summary>
    public int MaxPollRecords { get; set; }

    /// <summary>Poll timeout for batch collection in milliseconds.</summary>
    public int PollTimeoutMs { get; set; } = 1000;

    /// <summary>Stop batch processing on first error.</summary>
    public bool BreakOnFirstError { get; set; }

    /// <summary>Seek to "beginning" or "end" on first start.</summary>
    public string? SeekTo { get; set; }

    /// <summary>Interpret topic name as a regex pattern.</summary>
    public bool TopicIsPattern { get; set; }

    /// <summary>Static group instance ID. Avoids rebalance storms in K8s rolling deploys.</summary>
    public string? GroupInstanceId { get; set; }

    /// <summary>Session timeout in ms. Lower = faster dead-consumer detection.</summary>
    public int? SessionTimeoutMs { get; set; }

    /// <summary>Heartbeat interval in ms. Should be &lt; SessionTimeoutMs/3.</summary>
    public int? HeartbeatIntervalMs { get; set; }

    /// <summary>Max time between polls in ms. Prevents rebalance during slow processing.</summary>
    public int? MaxPollIntervalMs { get; set; }

    /// <summary>Partition assignment strategy: Range, RoundRobin, CooperativeSticky.</summary>
    public string? PartitionAssignmentStrategy { get; set; }

    /// <summary>Isolation level: ReadUncommitted, ReadCommitted. Must be ReadCommitted for exactly-once.</summary>
    public string? IsolationLevel { get; set; }

    // ── Producer ──

    /// <summary>Acknowledgment level: None, Leader, All.</summary>
    public string Acks { get; set; } = "Leader";

    /// <summary>Number of retries for failed sends.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Write delivery metadata to exchange headers after send.</summary>
    public bool RecordMetadata { get; set; }

    /// <summary>Header name or expression to extract the partition key from.</summary>
    public string? Key { get; set; }

    /// <summary>Explicit partition number to send to (bypasses partitioner).</summary>
    public int? PartitionNumber { get; set; }

    /// <summary>Enable transactional producer (exactly-once semantics).</summary>
    public bool Transacted { get; set; }

    /// <summary>Prefix for the transactional.id producer setting.</summary>
    public string TransactionIdPrefix { get; set; } = "redb-kafka";

    // ── Producer tuning ──

    /// <summary>Batching delay in ms. Higher = more batching throughput.</summary>
    public int? LingerMs { get; set; }

    /// <summary>Max batch size in bytes.</summary>
    public int? BatchSize { get; set; }

    /// <summary>Compression: None, Gzip, Snappy, Lz4, Zstd.</summary>
    public string? CompressionType { get; set; }

    /// <summary>Total delivery timeout in ms (includes retries).</summary>
    public int? MessageTimeoutMs { get; set; }

    // ── Advanced ──

    /// <summary>
    /// Additional librdkafka properties not exposed as typed settings.
    /// Applied last — overrides any typed property.
    /// </summary>
    public Dictionary<string, string> AdditionalProperties { get; set; } = new();

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Brokers))
            throw new ArgumentException("The 'brokers' parameter is required for Kafka endpoints.", nameof(Brokers));

        if (MaxPollRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPollRecords), MaxPollRecords, "MaxPollRecords cannot be negative.");

        if (PollTimeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(PollTimeoutMs), PollTimeoutMs, "PollTimeoutMs cannot be negative.");

        if (Retries < 0)
            throw new ArgumentOutOfRangeException(nameof(Retries), Retries, "Retries cannot be negative.");
    }

    // ── Config builders (internal) ──

    /// <summary>Builds a <see cref="ConsumerConfig"/> from the options, optionally using a named factory as base.</summary>
    internal ConsumerConfig BuildConsumerConfig(KafkaConnectionFactory? factory = null)
    {
        if (factory is not null)
        {
            var config = factory.BuildConsumerConfig(GroupId);
            // Endpoint-level overrides
            if (!string.IsNullOrWhiteSpace(Brokers)) config.BootstrapServers = Brokers;
            config.AutoOffsetReset = ParseAutoOffsetReset();
            ApplyConsumerTuning(config);
            ApplyAdditionalProperties(config);
            return config;
        }

        var cfg = new ConsumerConfig
        {
            BootstrapServers = Brokers,
            GroupId = GroupId,
            AutoOffsetReset = ParseAutoOffsetReset(),
            EnableAutoCommit = false, // always manual commit for transactional safety
        };

        ApplySecurity(cfg);
        ApplySsl(cfg);
        ApplyConsumerTuning(cfg);
        ApplyAdditionalProperties(cfg);
        return cfg;
    }

    /// <summary>Builds a <see cref="ProducerConfig"/> from the options, optionally using a named factory as base.</summary>
    internal ProducerConfig BuildProducerConfig(KafkaConnectionFactory? factory = null)
    {
        ProducerConfig config;
        if (factory is not null)
        {
            config = factory.BuildProducerConfig();
            // Endpoint-level overrides
            if (!string.IsNullOrWhiteSpace(Brokers)) config.BootstrapServers = Brokers;
            if (Retries > 0) config.MessageSendMaxRetries = Retries;
        }
        else
        {
            config = new ProducerConfig
            {
                BootstrapServers = Brokers,
                Acks = ParseAcks(),
                MessageSendMaxRetries = Retries,
            };
            ApplySecurity(config);
            ApplySsl(config);
        }

        ApplyProducerTuning(config);

        if (Transacted)
        {
            config.TransactionalId = $"{TransactionIdPrefix}-{Environment.MachineName}-{Environment.ProcessId}";
            config.EnableIdempotence = true;
            config.Acks = Confluent.Kafka.Acks.All; // required for idempotent/transactional producer
        }

        return config;
    }

    private Confluent.Kafka.AutoOffsetReset ParseAutoOffsetReset() =>
        AutoOffsetReset?.Trim().ToLowerInvariant() switch
        {
            "earliest" => Confluent.Kafka.AutoOffsetReset.Earliest,
            "latest" => Confluent.Kafka.AutoOffsetReset.Latest,
            "error" => Confluent.Kafka.AutoOffsetReset.Error,
            _ => Confluent.Kafka.AutoOffsetReset.Latest
        };

    private Confluent.Kafka.Acks ParseAcks() =>
        Acks?.Trim().ToLowerInvariant() switch
        {
            "none" or "0" => Confluent.Kafka.Acks.None,
            "leader" or "1" => Confluent.Kafka.Acks.Leader,
            "all" or "-1" => Confluent.Kafka.Acks.All,
            _ => Confluent.Kafka.Acks.Leader
        };

    private void ApplySecurity(ClientConfig config)
    {
        if (Enum.TryParse<Confluent.Kafka.SecurityProtocol>(SecurityProtocol, true, out var proto))
            config.SecurityProtocol = proto;

        if (!string.IsNullOrWhiteSpace(SaslMechanism))
        {
            if (Enum.TryParse<Confluent.Kafka.SaslMechanism>(SaslMechanism, true, out var mechanism))
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
    }

    private void ApplyConsumerTuning(ConsumerConfig config)
    {
        if (!string.IsNullOrEmpty(GroupInstanceId)) config.GroupInstanceId = GroupInstanceId;
        if (SessionTimeoutMs.HasValue) config.SessionTimeoutMs = SessionTimeoutMs.Value;
        if (HeartbeatIntervalMs.HasValue) config.HeartbeatIntervalMs = HeartbeatIntervalMs.Value;
        if (MaxPollIntervalMs.HasValue) config.MaxPollIntervalMs = MaxPollIntervalMs.Value;
        if (!string.IsNullOrEmpty(PartitionAssignmentStrategy))
        {
            config.PartitionAssignmentStrategy = PartitionAssignmentStrategy?.Trim().ToLowerInvariant() switch
            {
                "roundrobin" => Confluent.Kafka.PartitionAssignmentStrategy.RoundRobin,
                "cooperativesticky" => Confluent.Kafka.PartitionAssignmentStrategy.CooperativeSticky,
                _ => Confluent.Kafka.PartitionAssignmentStrategy.Range
            };
        }
        if (!string.IsNullOrEmpty(IsolationLevel))
        {
            config.IsolationLevel = IsolationLevel?.Trim().ToLowerInvariant() switch
            {
                "readcommitted" or "read_committed" => Confluent.Kafka.IsolationLevel.ReadCommitted,
                _ => Confluent.Kafka.IsolationLevel.ReadUncommitted
            };
        }
    }

    private void ApplyProducerTuning(ProducerConfig config)
    {
        if (LingerMs.HasValue) config.LingerMs = LingerMs.Value;
        if (BatchSize.HasValue) config.BatchSize = BatchSize.Value;
        if (MessageTimeoutMs.HasValue) config.MessageTimeoutMs = MessageTimeoutMs.Value;
        if (!string.IsNullOrEmpty(CompressionType))
        {
            config.CompressionType = CompressionType?.Trim().ToLowerInvariant() switch
            {
                "gzip" => Confluent.Kafka.CompressionType.Gzip,
                "snappy" => Confluent.Kafka.CompressionType.Snappy,
                "lz4" => Confluent.Kafka.CompressionType.Lz4,
                "zstd" => Confluent.Kafka.CompressionType.Zstd,
                _ => Confluent.Kafka.CompressionType.None
            };
        }
        ApplyAdditionalProperties(config);
    }

    private void ApplyAdditionalProperties(ClientConfig config)
    {
        foreach (var kvp in AdditionalProperties)
            config.Set(kvp.Key, kvp.Value);
    }
}
