using System.Reflection;
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
    [Sensitive]
    public string? SaslPassword { get; set; }

    // ── SSL/TLS ──

    /// <summary>CA certificate file path for SSL.</summary>
    public string? SslCaLocation { get; set; }

    /// <summary>Client certificate file path (for mTLS).</summary>
    public string? SslCertificateLocation { get; set; }

    /// <summary>Client private key file path (for mTLS).</summary>
    public string? SslKeyLocation { get; set; }

    /// <summary>Client private key passphrase.</summary>
    [Sensitive]
    public string? SslKeyPassword { get; set; }

    /// <summary>
    /// SSL endpoint identification: "https" verifies the broker hostname against its certificate,
    /// "" (empty) disables the check. Unset = librdkafka default. Was factory-only before волна A7,
    /// so a URI-configured TLS endpoint could not control hostname verification at all.
    /// </summary>
    public string? SslEndpointIdentificationAlgorithm { get; set; }

    /// <summary>Name of <see cref="KafkaConnectionFactory"/> in the route registry.</summary>
    public string? ConnectionFactory { get; set; }

    // ── Consumer ──

    /// <summary>Consumer group identifier (required for consumers).</summary>
    public string? GroupId { get; set; }

    /// <summary>Where to start consuming when no committed offset exists.</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    /// <summary>
    /// Framework-level auto-commit: when <c>true</c> (default), the consumer commits the offset
    /// inline right after a successful <c>Process</c> — at-least-once settle, mirroring the
    /// RabbitMQ consumer's post-process ack. When a Kafka-transactional producer of the same cluster
    /// (<see cref="TransactionalIdPrefix"/>) in the route's <c>.Transacted()</c> block committed the
    /// offset in its transaction, the inline commit is skipped: exactly-once within Kafka.
    /// Set <c>false</c> to never commit inline, nor let a transaction commit the offset.
    /// <para>Note: this is NOT librdkafka's <c>enable.auto.commit</c> (a background timer that can
    /// commit un-processed offsets); the underlying client stays at manual commit always.</para>
    /// </summary>
    public bool EnableAutoCommit { get; set; } = true;

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

    /// <summary>
    /// Isolation level: ReadUncommitted, ReadCommitted. Unset, librdkafka's default ReadCommitted: records of
    /// aborted Kafka transactions (<see cref="TransactionalIdPrefix"/> producers) are not delivered, and records of
    /// open ones wait for their commit.
    /// </summary>
    public string? IsolationLevel { get; set; }

    // ── Producer ──

    /// <summary>
    /// Acknowledgment level: None, Leader, All. Default All, as in the Kafka 3 client and Camel 4: a send is confirmed
    /// once every in-sync replica has it, which also allows the idempotent producer.
    /// </summary>
    public string Acks { get; set; } = "All";

    /// <summary>Number of retries for failed sends.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>
    /// Idempotent producer (<c>enable.idempotence</c>): the broker does not write a retried send twice. Unset, it follows
    /// the effective <c>acks</c>: on with <c>all</c>, off otherwise. <c>true</c> requires <c>acks=all</c>;
    /// <c>transacted=true</c> turns it on.
    /// </summary>
    public bool? EnableIdempotence { get; set; }

    /// <summary>Write delivery metadata to exchange headers after send.</summary>
    public bool RecordMetadata { get; set; }

    /// <summary>Header name or expression to extract the partition key from.</summary>
    public string? Key { get; set; }

    /// <summary>Explicit partition number to send to (bypasses partitioner).</summary>
    public int? PartitionNumber { get; set; }

    /// <summary>
    /// Whether the send joins the enclosing transaction block (<c>.Transacted()</c> /
    /// <c>.BeginTransaction()</c> ... <c>.CommitTransaction()</c>). Unset, it follows the block: deferred until the
    /// database commits inside one, sent at once outside. <c>true</c> requires a block, fails outside one and also
    /// makes the producer idempotent; <c>false</c> sends at once even inside a block. On its own this is at-least-once:
    /// the deferred sends go out one after the other. Kafka transactions are <see cref="TransactionalIdPrefix"/>.
    /// </summary>
    public bool? Transacted { get; set; }

    /// <summary>
    /// Switches the producer to Kafka transactions (<c>transactional.id</c> + <c>InitTransactions</c>). The sends it
    /// defers in a <c>.Transacted()</c> block commit as one Kafka transaction when the block commits — all of them or none
    /// — and when the route started from a Kafka consumer of the same cluster, the consumed offset commits in that same
    /// transaction (<c>SendOffsetsToTransaction</c>): exactly-once within Kafka. A send outside a block is a transaction
    /// of its own. The value is a prefix: the connector appends the machine name, the process id and the producer's
    /// number, so every producer gets an id of its own and nodes deploying the same configuration never fence each
    /// other. Requires <c>acks=all</c> and idempotence (both turned on); unset, the producer is not transactional.
    /// </summary>
    public string? TransactionalIdPrefix { get; set; }

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

        // Волна A1: every enum-valued option is parsed strictly at endpoint creation. A typo used
        // to fall through Enum.TryParse / a silent `_ =>` arm into the default — for
        // securityProtocol that meant a PLAINTEXT connection with no credentials.
        // Note: SASL protocol without an explicit mechanism stays legal — librdkafka defaults to
        // GSSAPI there, which is exactly what a Kerberos cluster wants.
        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SecurityProtocol>(SecurityProtocol, "securityProtocol");
        if (!string.IsNullOrWhiteSpace(SaslMechanism))
        {
            var mechanism = KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SaslMechanism>(SaslMechanism, "saslMechanism");
            KafkaOptionParsers.RequireSaslCredentials(mechanism, SaslUsername, SaslPassword);
        }

        KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.AutoOffsetReset>(AutoOffsetReset, "autoOffsetReset");
        var acks = KafkaOptionParsers.ParseAcks(Acks);
        if (EnableIdempotence == true && acks != Confluent.Kafka.Acks.All)
            throw new ArgumentException(
                $"'enableIdempotence=true' requires 'acks=all', and acks is '{Acks}': the broker cannot drop a " +
                "duplicate it has not confirmed on every replica. Set acks=all, or leave enableIdempotence unset.");
        if (Transacted == true && acks != Confluent.Kafka.Acks.All)
            throw new ArgumentException(
                $"'transacted=true' makes the producer idempotent, which requires 'acks=all', and acks is '{Acks}'. " +
                "Set acks=all, or drop transacted=true.");
        if (TransactionalIdPrefix is not null)
        {
            if (string.IsNullOrWhiteSpace(TransactionalIdPrefix))
                throw new ArgumentException("'transactionalIdPrefix' is empty: give the producer a name, or leave it unset.");
            if (acks != Confluent.Kafka.Acks.All)
                throw new ArgumentException(
                    $"'transactionalIdPrefix' makes the producer transactional, which requires 'acks=all', and acks is " +
                    $"'{Acks}'. Set acks=all, or drop transactionalIdPrefix.");
            if (EnableIdempotence == false)
                throw new ArgumentException(
                    "'transactionalIdPrefix' makes the producer transactional, and a transactional producer is " +
                    "idempotent: 'enableIdempotence=false' contradicts it.");
        }
        if (AdditionalProperties.ContainsKey("transactional.id"))
            throw new ArgumentException(
                "'transactional.id' in additionalProperties puts librdkafka into transactional mode without the " +
                "connector opening transactions, and every send fails with 'Erroneous state'. Use transactionalIdPrefix.");
        if (!string.IsNullOrEmpty(IsolationLevel))
            KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.IsolationLevel>(IsolationLevel, "isolationLevel");
        if (!string.IsNullOrEmpty(PartitionAssignmentStrategy))
            KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.PartitionAssignmentStrategy>(PartitionAssignmentStrategy, "partitionAssignmentStrategy");
        if (!string.IsNullOrEmpty(CompressionType))
            KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.CompressionType>(CompressionType, "compressionType");
        if (!string.IsNullOrWhiteSpace(SeekTo))
            KafkaOptionParsers.ParseSeekTo(SeekTo);

        // A regex subscription and an explicit partition assignment contradict each other:
        // Subscribe(pattern) joins the group, Assign(partition) bypasses it.
        if (TopicIsPattern && PartitionNumber.HasValue)
            throw new ArgumentException(
                "'topicIsPattern' cannot be combined with 'partitionNumber': a regex subscription " +
                "goes through the consumer group, an explicit partition bypasses it.");

        if (MaxPollRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPollRecords), MaxPollRecords, "MaxPollRecords cannot be negative.");

        if (PollTimeoutMs < 0)
            throw new ArgumentOutOfRangeException(nameof(PollTimeoutMs), PollTimeoutMs, "PollTimeoutMs cannot be negative.");

        if (Retries < 0)
            throw new ArgumentOutOfRangeException(nameof(Retries), Retries, "Retries cannot be negative.");

        // The core binder leaves a parameter it cannot place — a name no option has, or a value that does not convert to
        // the option's type — among the unmapped parameters, where nothing reads it: a typo would drop the option without
        // a word (a misspelt transactionalIdPrefix leaves the producer without transactions). Each one is refused, by name
        // only: the value may be a secret.
        if (UnmappedParameters.Count > 0)
            throw new ArgumentException(
                string.Join(" ", UnmappedParameters.Keys.Select(DescribeUnmapped)), UnmappedParameters.Keys.First());
    }

    private static string DescribeUnmapped(string name)
    {
        var option = Array.Find(typeof(KafkaEndpointOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (option is null)
            return $"'{name}' is not an option of the Kafka endpoint, so it would be dropped without a word.";

        var type = Nullable.GetUnderlyingType(option.PropertyType) ?? option.PropertyType;
        return $"'{name}': the value is not a {type.Name}, so the option would be dropped without a word.";
    }

    // ── Config builders (internal) ──

    /// <summary>
    /// Builds a <see cref="ConsumerConfig"/> from the options, optionally using a named factory as
    /// the base. Волна A6.1: the family precedence rule — a parameter the URI actually supplied
    /// (<paramref name="supplied"/> = raw URI parameters) wins over the factory, everything else
    /// keeps the factory's value. The old code was asymmetric: URI brokers were honored, URI
    /// SASL/SSL silently swallowed, and endpoint DEFAULTS stomped explicit factory settings.
    /// </summary>
    internal ConsumerConfig BuildConsumerConfig(KafkaConnectionFactory? factory = null,
        IReadOnlyDictionary<string, string>? supplied = null)
    {
        if (factory is not null)
        {
            var config = factory.BuildConsumerConfig(GroupId);
            if (!string.IsNullOrWhiteSpace(Brokers)) config.BootstrapServers = Brokers;
            if (WasSupplied(supplied, nameof(AutoOffsetReset)))
                config.AutoOffsetReset = ParseAutoOffsetReset();
            ApplySecurityOverrides(config, supplied);
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

    /// <summary>Builds a <see cref="ProducerConfig"/> from the options, optionally using a named factory as base.
    /// Same precedence rule as <see cref="BuildConsumerConfig"/>.</summary>
    internal ProducerConfig BuildProducerConfig(KafkaConnectionFactory? factory = null,
        IReadOnlyDictionary<string, string>? supplied = null)
    {
        ProducerConfig config;
        if (factory is not null)
        {
            config = factory.BuildProducerConfig();
            if (!string.IsNullOrWhiteSpace(Brokers)) config.BootstrapServers = Brokers;
            if (WasSupplied(supplied, nameof(Retries)))
                config.MessageSendMaxRetries = Retries;
            if (WasSupplied(supplied, nameof(Acks)))
                config.Acks = ParseAcks();
            ApplySecurityOverrides(config, supplied);
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

        // Idempotence follows the effective acks unless the endpoint says otherwise (acks=all, the default, turns it on,
        // as in the Kafka 3 client). Set before the tuning, so additionalProperties still has the last word.
        config.EnableIdempotence = EnableIdempotence ?? config.Acks == Confluent.Kafka.Acks.All;

        ApplyProducerTuning(config);

        if (Transacted == true || TransactionalIdPrefix is not null)
        {
            // `transacted=true` is an idempotent producer whose send is deferred to the route's transaction boundary.
            // The transactional.id of a Kafka-transactional producer is set by the producer itself (KafkaProducer),
            // which appends its own identity to TransactionalIdPrefix: every producer instance needs an id of its own.
            config.EnableIdempotence = true;
            config.Acks = Confluent.Kafka.Acks.All; // required for the idempotent producer
        }

        return config;
    }

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
        ApplySslEndpointIdentification(config);
    }

    private void ApplySslEndpointIdentification(ClientConfig config)
    {
        if (SslEndpointIdentificationAlgorithm is null) return;
        config.SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm == string.Empty
            ? Confluent.Kafka.SslEndpointIdentificationAlgorithm.None
            : Confluent.Kafka.SslEndpointIdentificationAlgorithm.Https;
    }

    /// <summary>Admin/metadata client config carrying the endpoint's own security (волна A6.2).</summary>
    internal AdminClientConfig BuildAdminConfig()
    {
        var config = new AdminClientConfig { BootstrapServers = Brokers };
        ApplySecurity(config);
        ApplySsl(config);
        return config;
    }

    private static bool WasSupplied(IReadOnlyDictionary<string, string>? supplied, string name)
        => supplied is not null && supplied.Keys.Any(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// URI-supplied security/SSL parameters applied over the factory base, key by key: a hybrid
    /// like "mechanism from the factory, password from the URI" resolves the way it reads.
    /// </summary>
    private void ApplySecurityOverrides(ClientConfig config, IReadOnlyDictionary<string, string>? supplied)
    {
        if (WasSupplied(supplied, nameof(SecurityProtocol)))
            config.SecurityProtocol =
                KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SecurityProtocol>(SecurityProtocol, "securityProtocol");
        if (WasSupplied(supplied, nameof(SaslMechanism)) && !string.IsNullOrWhiteSpace(SaslMechanism))
            config.SaslMechanism =
                KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.SaslMechanism>(SaslMechanism, "saslMechanism");
        if (WasSupplied(supplied, nameof(SaslUsername))) config.SaslUsername = SaslUsername;
        if (WasSupplied(supplied, nameof(SaslPassword))) config.SaslPassword = SaslPassword;
        if (WasSupplied(supplied, nameof(SslCaLocation))) config.SslCaLocation = SslCaLocation;
        if (WasSupplied(supplied, nameof(SslCertificateLocation))) config.SslCertificateLocation = SslCertificateLocation;
        if (WasSupplied(supplied, nameof(SslKeyLocation))) config.SslKeyLocation = SslKeyLocation;
        if (WasSupplied(supplied, nameof(SslKeyPassword))) config.SslKeyPassword = SslKeyPassword;
        if (WasSupplied(supplied, nameof(SslEndpointIdentificationAlgorithm)))
            ApplySslEndpointIdentification(config);
    }

    private void ApplyConsumerTuning(ConsumerConfig config)
    {
        if (!string.IsNullOrEmpty(GroupInstanceId)) config.GroupInstanceId = GroupInstanceId;
        if (SessionTimeoutMs.HasValue) config.SessionTimeoutMs = SessionTimeoutMs.Value;
        if (HeartbeatIntervalMs.HasValue) config.HeartbeatIntervalMs = HeartbeatIntervalMs.Value;
        if (MaxPollIntervalMs.HasValue) config.MaxPollIntervalMs = MaxPollIntervalMs.Value;
        if (!string.IsNullOrEmpty(PartitionAssignmentStrategy))
            config.PartitionAssignmentStrategy = KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.PartitionAssignmentStrategy>(
                PartitionAssignmentStrategy, "partitionAssignmentStrategy");
        if (!string.IsNullOrEmpty(IsolationLevel))
            config.IsolationLevel = KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.IsolationLevel>(
                IsolationLevel, "isolationLevel");
    }

    private void ApplyProducerTuning(ProducerConfig config)
    {
        if (LingerMs.HasValue) config.LingerMs = LingerMs.Value;
        if (BatchSize.HasValue) config.BatchSize = BatchSize.Value;
        if (MessageTimeoutMs.HasValue) config.MessageTimeoutMs = MessageTimeoutMs.Value;
        if (!string.IsNullOrEmpty(CompressionType))
            config.CompressionType = KafkaOptionParsers.ParseOrThrow<Confluent.Kafka.CompressionType>(
                CompressionType, "compressionType");
        ApplyAdditionalProperties(config);
    }

    private void ApplyAdditionalProperties(ClientConfig config)
    {
        foreach (var kvp in AdditionalProperties)
            config.Set(kvp.Key, kvp.Value);
    }
}
