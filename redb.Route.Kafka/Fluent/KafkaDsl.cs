using System.Text;
using redb.Route.Abstractions;
using redb.Route.Expressions;

namespace redb.Route.Kafka;

/// <summary>
/// Fluent API for Kafka endpoints.
/// <example><code>
/// .From(Kafka.Topic("orders").Brokers("broker1:9092").GroupId("order-group").AutoOffsetReset("Earliest"))
/// .To(Kafka.Topic("notifications").Brokers("broker1:9092").Acks("All"))
/// </code></example>
/// </summary>
public static class Kafka
{
    /// <summary>Creates a Kafka endpoint for the given topic name.</summary>
    public static KafkaBuilder Topic(string name) => new(name);
}

/// <summary>
/// Fluent builder for Kafka endpoint URIs.
/// </summary>
public sealed class KafkaBuilder
{
    private readonly string _topic;

    // Connection
    private string? _brokers;
    private string? _securityProtocol;
    private string? _saslMechanism;
    private string? _saslUsername;
    private string? _saslPassword;
    private string? _sslCaLocation;
    private string? _sslCertificateLocation;
    private string? _sslKeyLocation;
    private string? _sslKeyPassword;
    private string? _connectionFactory;

    // Consumer
    private string? _groupId;
    private string? _autoOffsetReset;
    private string? _enableAutoCommit;
    private string? _maxPollRecords;
    private string? _pollTimeoutMs;
    private bool _breakOnFirstError;
    private string? _seekTo;
    private bool _topicIsPattern;
    private string? _groupInstanceId;
    private string? _sessionTimeoutMs;
    private string? _heartbeatIntervalMs;
    private string? _maxPollIntervalMs;
    private string? _partitionAssignmentStrategy;
    private string? _isolationLevel;

    // Producer
    private string? _acks;
    private string? _retries;
    private bool _recordMetadata;
    private string? _key;
    private string? _partitionNumber;
    private bool _transacted;
    private string? _transactionIdPrefix;
    private string? _lingerMs;
    private string? _batchSize;
    private string? _compressionType;
    private string? _messageTimeoutMs;

    internal KafkaBuilder(string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _topic = topic;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Bootstrap servers (comma-separated host:port). e.g. "broker1:9092,broker2:9092".</summary>
    public KafkaBuilder Brokers(IExpression brokers) { _brokers = brokers.ToTemplateString(); return this; }
    /// <summary>Bootstrap servers (template string, supports <c>${...}</c>). e.g. "broker1:9092,broker2:9092".</summary>
    public KafkaBuilder Brokers(string brokers) => Brokers(new StringExpression(brokers));

    /// <summary>Security protocol: Plaintext, Ssl, SaslPlaintext, SaslSsl.</summary>
    public KafkaBuilder SecurityProtocol(string protocol) { _securityProtocol = protocol; return this; }

    /// <summary>SASL authentication: mechanism + credentials.</summary>
    public KafkaBuilder Sasl(string mechanism, IExpression username, IExpression password)
    {
        _saslMechanism = mechanism; _saslUsername = username.ToTemplateString(); _saslPassword = password.ToTemplateString(); return this;
    }

    /// <summary>Path to CA certificate file for SSL.</summary>
    public KafkaBuilder SslCa(IExpression path) { _sslCaLocation = path.ToTemplateString(); return this; }
    /// <summary>Path to CA certificate file for SSL (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder SslCa(string path) => SslCa(new StringExpression(path));

    /// <summary>Client certificate for mutual TLS.</summary>
    public KafkaBuilder SslCert(IExpression certPath, IExpression? keyPath = null, IExpression? keyPassword = null)
    {
        _sslCertificateLocation = certPath.ToTemplateString(); _sslKeyLocation = keyPath?.ToTemplateString(); _sslKeyPassword = keyPassword?.ToTemplateString(); return this;
    }

    /// <summary>Use a named connection factory registered in DI.</summary>
    public KafkaBuilder ConnectionFactory(IExpression name) { _connectionFactory = name.ToTemplateString(); return this; }
    /// <summary>Use a named connection factory registered in DI (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder ConnectionFactory(string name) => ConnectionFactory(new StringExpression(name));

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Consumer group ID.</summary>
    public KafkaBuilder GroupId(IExpression groupId) { _groupId = groupId.ToTemplateString(); return this; }
    /// <summary>Consumer group ID (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder GroupId(string groupId) => GroupId(new StringExpression(groupId));

    /// <summary>Auto offset reset: Latest, Earliest, Error.</summary>
    public KafkaBuilder AutoOffsetReset(string reset) { _autoOffsetReset = reset; return this; }

    /// <summary>
    /// Framework-level auto-commit (default <c>true</c>): commit the offset inline after a successful
    /// Process. A transactional route (<c>.Transacted()</c>/<c>.CommitTransaction()</c>) takes
    /// precedence and the inline commit is skipped. Set <c>false</c> to commit only at a transaction boundary.
    /// </summary>
    public KafkaBuilder EnableAutoCommit(bool enabled) { _enableAutoCommit = enabled ? "true" : "false"; return this; }

    /// <summary>Max records per poll.</summary>
    public KafkaBuilder MaxPollRecords(int max) { _maxPollRecords = max.ToString(); return this; }
    /// <summary>Max records per poll from an expression.</summary>
    public KafkaBuilder MaxPollRecords(IExpression max) { _maxPollRecords = max.ToTemplateString(); return this; }

    /// <summary>Poll timeout in milliseconds. Default 1000.</summary>
    public KafkaBuilder PollTimeout(int ms) { _pollTimeoutMs = ms.ToString(); return this; }
    /// <summary>Poll timeout from an expression.</summary>
    public KafkaBuilder PollTimeout(IExpression ms) { _pollTimeoutMs = ms.ToTemplateString(); return this; }

    /// <summary>Stop consuming on first error.</summary>
    public KafkaBuilder BreakOnFirstError() { _breakOnFirstError = true; return this; }

    /// <summary>Seek to a specific position: "beginning", "end", or offset number.</summary>
    public KafkaBuilder SeekTo(string position) { _seekTo = position; return this; }

    /// <summary>Treat topic name as a regex pattern.</summary>
    public KafkaBuilder TopicIsPattern() { _topicIsPattern = true; return this; }

    /// <summary>Static group instance ID for cooperative rebalancing.</summary>
    public KafkaBuilder GroupInstanceId(IExpression id) { _groupInstanceId = id.ToTemplateString(); return this; }
    /// <summary>Static group instance ID for cooperative rebalancing (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder GroupInstanceId(string id) => GroupInstanceId(new StringExpression(id));

    /// <summary>Session timeout in milliseconds.</summary>
    public KafkaBuilder SessionTimeout(int ms) { _sessionTimeoutMs = ms.ToString(); return this; }
    /// <summary>Session timeout from an expression.</summary>
    public KafkaBuilder SessionTimeout(IExpression ms) { _sessionTimeoutMs = ms.ToTemplateString(); return this; }

    /// <summary>Heartbeat interval in milliseconds.</summary>
    public KafkaBuilder HeartbeatInterval(int ms) { _heartbeatIntervalMs = ms.ToString(); return this; }
    /// <summary>Heartbeat interval from an expression.</summary>
    public KafkaBuilder HeartbeatInterval(IExpression ms) { _heartbeatIntervalMs = ms.ToTemplateString(); return this; }

    /// <summary>Max poll interval in milliseconds.</summary>
    public KafkaBuilder MaxPollInterval(int ms) { _maxPollIntervalMs = ms.ToString(); return this; }
    /// <summary>Max poll interval from an expression.</summary>
    public KafkaBuilder MaxPollInterval(IExpression ms) { _maxPollIntervalMs = ms.ToTemplateString(); return this; }

    /// <summary>Partition assignment strategy.</summary>
    public KafkaBuilder PartitionAssignmentStrategy(string strategy) { _partitionAssignmentStrategy = strategy; return this; }

    /// <summary>Isolation level: ReadUncommitted, ReadCommitted.</summary>
    public KafkaBuilder IsolationLevel(string level) { _isolationLevel = level; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Acknowledge level: None, Leader, All. Default "Leader".</summary>
    public KafkaBuilder Acks(string acks) { _acks = acks; return this; }

    /// <summary>Number of retries. Default 3.</summary>
    public KafkaBuilder Retries(int count) { _retries = count.ToString(); return this; }
    /// <summary>Retries from an expression.</summary>
    public KafkaBuilder Retries(IExpression count) { _retries = count.ToTemplateString(); return this; }

    /// <summary>Include record metadata (partition, offset) in the exchange after produce.</summary>
    public KafkaBuilder RecordMetadata() { _recordMetadata = true; return this; }

    /// <summary>Message key for partitioning.</summary>
    public KafkaBuilder Key(IExpression key) { _key = key.ToTemplateString(); return this; }
    /// <summary>Message key for partitioning (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder Key(string key) => Key(new StringExpression(key));

    /// <summary>Explicit partition number.</summary>
    public KafkaBuilder Partition(int number) { _partitionNumber = number.ToString(); return this; }
    /// <summary>Partition from an expression.</summary>
    public KafkaBuilder Partition(IExpression number) { _partitionNumber = number.ToTemplateString(); return this; }

    /// <summary>Enable transactional producer with optional ID prefix.</summary>
    public KafkaBuilder Transacted(IExpression? idPrefix = null)
    {
        _transacted = true; _transactionIdPrefix = idPrefix?.ToTemplateString(); return this;
    }
    /// <summary>Enable transactional producer with an ID prefix (template string, supports <c>${...}</c>).</summary>
    public KafkaBuilder Transacted(string idPrefix) => Transacted(new StringExpression(idPrefix));

    /// <summary>Linger time in milliseconds (batch delay).</summary>
    public KafkaBuilder Linger(int ms) { _lingerMs = ms.ToString(); return this; }
    /// <summary>Linger from an expression.</summary>
    public KafkaBuilder Linger(IExpression ms) { _lingerMs = ms.ToTemplateString(); return this; }

    /// <summary>Batch size in bytes.</summary>
    public KafkaBuilder BatchSize(int bytes) { _batchSize = bytes.ToString(); return this; }
    /// <summary>Batch size from an expression.</summary>
    public KafkaBuilder BatchSize(IExpression bytes) { _batchSize = bytes.ToTemplateString(); return this; }

    /// <summary>Compression type: none, gzip, snappy, lz4, zstd.</summary>
    public KafkaBuilder Compression(string type) { _compressionType = type; return this; }

    /// <summary>Message delivery timeout in milliseconds.</summary>
    public KafkaBuilder MessageTimeout(int ms) { _messageTimeoutMs = ms.ToString(); return this; }
    /// <summary>Message timeout from an expression.</summary>
    public KafkaBuilder MessageTimeout(IExpression ms) { _messageTimeoutMs = ms.ToTemplateString(); return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the Kafka endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("kafka:");
        sb.Append(_topic);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }

        // Connection
        AppendIf("brokers", _brokers);
        AppendIf("securityProtocol", _securityProtocol);
        AppendIf("saslMechanism", _saslMechanism);
        AppendIf("saslUsername", _saslUsername);
        AppendIf("saslPassword", _saslPassword);
        AppendIf("sslCaLocation", _sslCaLocation);
        AppendIf("sslCertificateLocation", _sslCertificateLocation);
        AppendIf("sslKeyLocation", _sslKeyLocation);
        AppendIf("sslKeyPassword", _sslKeyPassword);
        AppendIf("connectionFactory", _connectionFactory);

        // Consumer
        AppendIf("groupId", _groupId);
        AppendIf("autoOffsetReset", _autoOffsetReset);
        AppendIf("enableAutoCommit", _enableAutoCommit);
        AppendIf("maxPollRecords", _maxPollRecords);
        AppendIf("pollTimeoutMs", _pollTimeoutMs);
        AppendBool("breakOnFirstError", _breakOnFirstError);
        AppendIf("seekTo", _seekTo);
        AppendBool("topicIsPattern", _topicIsPattern);
        AppendIf("groupInstanceId", _groupInstanceId);
        AppendIf("sessionTimeoutMs", _sessionTimeoutMs);
        AppendIf("heartbeatIntervalMs", _heartbeatIntervalMs);
        AppendIf("maxPollIntervalMs", _maxPollIntervalMs);
        AppendIf("partitionAssignmentStrategy", _partitionAssignmentStrategy);
        AppendIf("isolationLevel", _isolationLevel);

        // Producer
        AppendIf("acks", _acks);
        AppendIf("retries", _retries);
        AppendBool("recordMetadata", _recordMetadata);
        AppendIf("key", _key);
        AppendIf("partitionNumber", _partitionNumber);
        AppendBool("transacted", _transacted);
        AppendIf("transactionIdPrefix", _transactionIdPrefix);
        AppendIf("lingerMs", _lingerMs);
        AppendIf("batchSize", _batchSize);
        AppendIf("compressionType", _compressionType);
        AppendIf("messageTimeoutMs", _messageTimeoutMs);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(KafkaBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
