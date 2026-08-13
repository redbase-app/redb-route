using System.Text;
using redb.Route.Abstractions;

namespace redb.Route.IbmMq;

/// <summary>
/// Fluent API for IBM MQ endpoints.
/// <example><code>
/// .From(Wmq.Queue("DEV.QUEUE.1").Host("mq-host").QueueManager("QM1").Channel("DEV.APP.SVRCONN"))
/// .To(Wmq.Queue("ORDERS.OUT").Host("mq-host").Persistent())
/// .From(Wmq.Topic("EVENTS/ORDER").Host("mq-host").QueueManager("QM1"))
/// </code></example>
/// </summary>
public static class Wmq
{
    /// <summary>Creates an IBM MQ queue endpoint.</summary>
    public static IbmMqBuilder Queue(string name) => new(name, IbmMqDestinationType.Queue);

    /// <summary>Creates an IBM MQ topic endpoint.</summary>
    public static IbmMqBuilder Topic(string name) => new(name, IbmMqDestinationType.Topic);
}

/// <summary>
/// Fluent builder for IBM MQ endpoint URIs.
/// </summary>
public sealed class IbmMqBuilder
{
    private readonly string _destination;
    private readonly IbmMqDestinationType _destType;

    // Connection
    private string? _connectionFactory;
    private string? _host;
    private int? _port;
    private string? _channel;
    private string? _queueManager;
    private string? _user;
    private string? _password;
    private string? _clientId;

    // Consumer
    private IbmMqReceiveMode? _receiveMode;
    private int? _concurrentConsumers;
    private int? _waitInterval;
    private int? _batchSize;
    private int? _backoutThreshold;
    private string? _backoutQueue;
    private string? _selector;
    private bool? _convert;

    // Producer
    private IbmMqPersistence? _persistence;
    private string? _persistenceExpr;
    private string? _priority;
    private string? _expiry;
    private IbmMqTargetClient? _targetClient;
    private IbmMqMessageType? _messageType;
    private string? _messageTypeExpr;

    // Transactions
    private bool _transacted;

    // Dead Letter
    private string? _deadLetterQueue;
    private int? _maxRedeliveries;

    // RPC
    private bool _replyTo;
    private string? _replyToQueue;
    private string? _replyToQueueManager;
    private int? _timeout;
    private IbmMqCorrelationPattern? _correlationPattern;

    // SSL
    private string? _sslCipherSpec;
    private string? _sslCertLabel;
    private string? _sslPeerName;
    private string? _sslKeyRepository;
    private int? _sslKeyResetCount;

    // Advanced
    private int? _ccsid;
    private bool? _mqmdWriteEnabled;
    private bool? _mqmdReadEnabled;

    internal IbmMqBuilder(string destination, IbmMqDestinationType destType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        _destination = destination;
        _destType = destType;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Use a named connection factory registered in DI.</summary>
    public IbmMqBuilder ConnectionFactory(string name) { _connectionFactory = name; return this; }

    /// <summary>Queue manager hostname. Default "localhost".</summary>
    public IbmMqBuilder Host(string host) { _host = host; return this; }

    /// <summary>MQ listener port. Default 1414.</summary>
    public IbmMqBuilder Port(int port) { _port = port; return this; }

    /// <summary>Server-connection channel. Default "DEV.APP.SVRCONN".</summary>
    public IbmMqBuilder Channel(string channel) { _channel = channel; return this; }

    /// <summary>Queue manager name. Default "QM1".</summary>
    public IbmMqBuilder QueueManager(string qm) { _queueManager = qm; return this; }

    /// <summary>Authentication user.</summary>
    public IbmMqBuilder User(string user) { _user = user; return this; }

    /// <summary>Authentication password.</summary>
    public IbmMqBuilder Password(string password) { _password = password; return this; }

    /// <summary>Client identifier for connection tracking.</summary>
    public IbmMqBuilder ClientId(string id) { _clientId = id; return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>
    /// Receive strategy: <see cref="IbmMqReceiveMode.Poll"/> (synchronous MQGET-WAIT, default) or
    /// <see cref="IbmMqReceiveMode.Listener"/> (event-driven XMS push, &lt;50&#160;ms latency).
    /// </summary>
    public IbmMqBuilder ReceiveMode(IbmMqReceiveMode mode) { _receiveMode = mode; return this; }

    /// <summary>Shortcut for <c>ReceiveMode(IbmMqReceiveMode.Listener)</c> — event-driven XMS receive.</summary>
    public IbmMqBuilder Listener() { _receiveMode = IbmMqReceiveMode.Listener; return this; }

    /// <summary>Number of concurrent consumers. Default 1.</summary>
    public IbmMqBuilder ConcurrentConsumers(int count) { _concurrentConsumers = count; return this; }

    /// <summary>MQGET wait interval in milliseconds. Default 5000.</summary>
    public IbmMqBuilder WaitInterval(int ms) { _waitInterval = ms; return this; }

    /// <summary>Batch size for bulk receive. 0 = single message.</summary>
    public IbmMqBuilder BatchSize(int size) { _batchSize = size; return this; }

    /// <summary>Backout threshold before routing to backout queue.</summary>
    public IbmMqBuilder BackoutThreshold(int threshold) { _backoutThreshold = threshold; return this; }

    /// <summary>Backout (poison message) queue name.</summary>
    public IbmMqBuilder BackoutQueue(string queue) { _backoutQueue = queue; return this; }

    /// <summary>Message selector expression.</summary>
    public IbmMqBuilder Selector(string selector) { _selector = selector; return this; }

    /// <summary>Apply MQGMO_CONVERT on receive. Default true.</summary>
    public IbmMqBuilder Convert(bool convert = true) { _convert = convert; return this; }

    // ── Producer ──────────────────────────────────────────────────────

    /// <summary>Set messages as persistent.</summary>
    public IbmMqBuilder Persistent() { _persistence = IbmMqPersistence.Persistent; return this; }

    /// <summary>Set messages as non-persistent.</summary>
    public IbmMqBuilder NonPersistent() { _persistence = IbmMqPersistence.NonPersistent; return this; }

    /// <summary>Message persistence mode.</summary>
    public IbmMqBuilder Persistence(IbmMqPersistence mode) { _persistence = mode; return this; }

    /// <summary>Message persistence from an expression (resolves to enum name or numeric value).</summary>
    public IbmMqBuilder Persistence(IExpression expr) { _persistenceExpr = expr.ToTemplateString(); return this; }

    /// <summary>Message priority (0–9). -1 = queue default.</summary>
    public IbmMqBuilder Priority(int priority) { _priority = priority.ToString(); return this; }

    /// <summary>Message priority from an expression.</summary>
    public IbmMqBuilder Priority(IExpression expr) { _priority = expr.ToTemplateString(); return this; }

    /// <summary>Message expiry in tenths of a second. -1 = unlimited.</summary>
    public IbmMqBuilder Expiry(int tenthsOfSecond) { _expiry = tenthsOfSecond.ToString(); return this; }

    /// <summary>Message expiry from an expression.</summary>
    public IbmMqBuilder Expiry(IExpression expr) { _expiry = expr.ToTemplateString(); return this; }

    /// <summary>Target client mode — Jms (with RFH2) or Mq (raw MQMD).</summary>
    public IbmMqBuilder TargetClient(IbmMqTargetClient client) { _targetClient = client; return this; }

    /// <summary>Message type for outgoing messages.</summary>
    public IbmMqBuilder MessageType(IbmMqMessageType type) { _messageType = type; return this; }

    /// <summary>Message type from an expression (resolves to enum name or numeric value).</summary>
    public IbmMqBuilder MessageType(IExpression expr) { _messageTypeExpr = expr.ToTemplateString(); return this; }

    // ── Transactions ──────────────────────────────────────────────────

    /// <summary>Enable local MQ transactions.</summary>
    public IbmMqBuilder Transacted() { _transacted = true; return this; }

    // ── Dead Letter ───────────────────────────────────────────────────

    /// <summary>Dead-letter queue for failed messages.</summary>
    public IbmMqBuilder DeadLetterQueue(string queue) { _deadLetterQueue = queue; return this; }

    /// <summary>Max redelivery attempts before dead-lettering.</summary>
    public IbmMqBuilder MaxRedeliveries(int count) { _maxRedeliveries = count; return this; }

    // ── RPC ───────────────────────────────────────────────────────────

    /// <summary>Enable request/reply pattern.</summary>
    public IbmMqBuilder ReplyTo() { _replyTo = true; return this; }

    /// <summary>Reply-to queue name (if not using dynamic temporary queue).</summary>
    public IbmMqBuilder ReplyToQueue(string queue) { _replyToQueue = queue; _replyTo = true; return this; }

    /// <summary>Reply-to queue manager.</summary>
    public IbmMqBuilder ReplyToQueueManager(string qm) { _replyToQueueManager = qm; return this; }

    /// <summary>RPC timeout in seconds. Default 30.</summary>
    public IbmMqBuilder Timeout(int seconds) { _timeout = seconds; return this; }

    /// <summary>RPC correlation pattern — MsgId or CorrelId.</summary>
    public IbmMqBuilder CorrelationPattern(IbmMqCorrelationPattern pattern) { _correlationPattern = pattern; return this; }

    // ── SSL ───────────────────────────────────────────────────────────

    /// <summary>SSL CipherSpec name.</summary>
    public IbmMqBuilder SslCipherSpec(string cipher) { _sslCipherSpec = cipher; return this; }

    /// <summary>SSL certificate label.</summary>
    public IbmMqBuilder SslCertLabel(string label) { _sslCertLabel = label; return this; }

    /// <summary>SSL peer name (DN pattern).</summary>
    public IbmMqBuilder SslPeerName(string peerName) { _sslPeerName = peerName; return this; }

    /// <summary>SSL key repository path (without .kdb extension).</summary>
    public IbmMqBuilder SslKeyRepository(string path) { _sslKeyRepository = path; return this; }

    /// <summary>SSL key reset count in bytes.</summary>
    public IbmMqBuilder SslKeyResetCount(int bytes) { _sslKeyResetCount = bytes; return this; }

    // ── Advanced ──────────────────────────────────────────────────────

    /// <summary>Coded Character Set ID. Default 1208 (UTF-8).</summary>
    public IbmMqBuilder CCSID(int ccsid) { _ccsid = ccsid; return this; }

    /// <summary>Allow writing MQMD fields from exchange headers.</summary>
    public IbmMqBuilder MqmdWriteEnabled(bool enabled = true) { _mqmdWriteEnabled = enabled; return this; }

    /// <summary>Read all MQMD fields into exchange headers. Default true.</summary>
    public IbmMqBuilder MqmdReadEnabled(bool enabled = true) { _mqmdReadEnabled = enabled; return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the IBM MQ endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("wmq:");
        sb.Append(_destination);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(Uri.EscapeDataString(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }
        void AppendInt(string key, int? value) { if (value.HasValue) Append(key, value.Value.ToString()); }
        void AppendIntOrExpression(string key, string? value)
        {
            if (value is null) return;
            if (value.Contains("${"))
                Append(key + "Expression", value);
            else
                Append(key, value);
        }

        // Destination type (only if topic — queue is default)
        if (_destType == IbmMqDestinationType.Topic)
            Append("destinationType", "Topic");

        // Connection
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("host", _host);
        AppendInt("port", _port);
        AppendIf("channel", _channel);
        AppendIf("queueManager", _queueManager);
        AppendIf("user", _user);
        AppendIf("password", _password);
        AppendIf("clientId", _clientId);

        // Consumer
        if (_receiveMode.HasValue)
            Append("receiveMode", _receiveMode.Value.ToString());
        AppendInt("concurrentConsumers", _concurrentConsumers);
        AppendInt("waitInterval", _waitInterval);
        AppendInt("batchSize", _batchSize);
        AppendInt("backoutThreshold", _backoutThreshold);
        AppendIf("backoutQueue", _backoutQueue);
        AppendIf("selector", _selector);
        AppendBoolExplicit("convert", _convert);

        // Producer
        if (_persistence.HasValue)
            Append("persistence", _persistence.Value.ToString());
        if (_persistenceExpr is not null)
            Append("persistenceExpression", _persistenceExpr);
        AppendIntOrExpression("priority", _priority);
        AppendIntOrExpression("expiry", _expiry);
        if (_targetClient.HasValue)
            Append("targetClient", _targetClient.Value.ToString());
        if (_messageType.HasValue)
            Append("messageType", _messageType.Value.ToString());
        if (_messageTypeExpr is not null)
            Append("messageTypeExpression", _messageTypeExpr);

        // Transactions
        AppendBool("transacted", _transacted);

        // Dead Letter
        AppendIf("deadLetterQueue", _deadLetterQueue);
        AppendInt("maxRedeliveries", _maxRedeliveries);

        // RPC
        AppendBool("replyTo", _replyTo);
        AppendIf("replyToQueue", _replyToQueue);
        AppendIf("replyToQueueManager", _replyToQueueManager);
        AppendInt("timeout", _timeout);
        if (_correlationPattern.HasValue)
            Append("correlationPattern", _correlationPattern.Value.ToString());

        // SSL
        AppendIf("sslCipherSpec", _sslCipherSpec);
        AppendIf("sslCertLabel", _sslCertLabel);
        AppendIf("sslPeerName", _sslPeerName);
        AppendIf("sslKeyRepository", _sslKeyRepository);
        AppendInt("sslKeyResetCount", _sslKeyResetCount);

        // Advanced
        AppendInt("cCSID", _ccsid);
        AppendBoolExplicit("mqmdWriteEnabled", _mqmdWriteEnabled);
        AppendBoolExplicit("mqmdReadEnabled", _mqmdReadEnabled);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(IbmMqBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
