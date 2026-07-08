using System.Text;
using System.Web;
using redb.Route.Abstractions;

namespace redb.Route.RabbitMQ;

/// <summary>
/// Fluent API for RabbitMQ endpoints.
/// <example><code>
/// .From(Rabbit.Queue("orders").Host("broker1").Exchange("exchange", "topic").RoutingKey("order.*"))
/// .To(Rabbit.Queue("notifications").Host("broker1").Declare())
/// </code></example>
/// </summary>
public static class Rabbit
{
    /// <summary>Creates a RabbitMQ endpoint for the given queue name.</summary>
    public static RabbitBuilder Queue(string name) => new(name);
}

/// <summary>
/// Fluent builder for RabbitMQ endpoint URIs.
/// </summary>
public sealed class RabbitBuilder
{
    private readonly string _queue;

    // Connection
    private string? _host;
    private string? _port;
    private string? _username;
    private string? _password;
    private string? _virtualHost;
    private string? _connectionFactory;
    private string? _clientName;

    // Exchange
    private string? _exchange;
    private string? _exchangeType;
    private bool? _exchangeDurable;
    private bool? _exchangeAutoDelete;
    private bool _declare;

    // Queue
    private bool? _durable;
    private bool _autoDelete;
    private bool _exclusive;
    private string? _routingKey;

    // Message
    private string? _contentType;
    private string? _messageTtl;
    private string? _expires;

    // Consumer
    private string? _concurrentConsumers;
    private string? _prefetchCount;
    private bool _autoAck;
    private bool _transacted;
    private bool? _mandatory;
    private bool _replyTo;
    private string? _timeout;

    // Queue limits
    private string? _maxLength;
    private string? _maxLengthBytes;
    private string? _overflow;
    private string? _deadLetterExchange;
    private string? _deadLetterRoutingKey;
    private string? _queueType;
    private string? _maxPriority;

    // Connection settings
    private bool? _automaticRecovery;
    private bool? _topologyRecoveryEnabled;
    private string? _recoveryInterval;
    private string? _heartbeat;
    private string? _connectionTimeout;
    private string? _socketReadTimeout;
    private string? _socketWriteTimeout;
    private string? _continuationTimeout;
    private string? _consumerDispatchConcurrency;

    // SSL
    private bool _ssl;
    private string? _sslServerName;
    private string? _sslCertPath;
    private string? _sslCertPassphrase;

    internal RabbitBuilder(string queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        _queue = queue;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Broker hostname. Default "localhost".</summary>
    public RabbitBuilder Host(IExpression host) { _host = host.ToTemplateString(); return this; }

    /// <summary>Broker port. Default 5672.</summary>
    public RabbitBuilder Port(int port) { _port = port.ToString(); return this; }
    /// <summary>Broker port from an expression.</summary>
    public RabbitBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    /// <summary>Username. Default "guest".</summary>
    public RabbitBuilder Username(IExpression username) { _username = username.ToTemplateString(); return this; }

    /// <summary>Password. Default "guest".</summary>
    public RabbitBuilder Password(IExpression password) { _password = password.ToTemplateString(); return this; }

    /// <summary>Virtual host. Default "/".</summary>
    public RabbitBuilder VirtualHost(IExpression vhost) { _virtualHost = vhost.ToTemplateString(); return this; }

    /// <summary>Use a named connection factory registered in DI.</summary>
    public RabbitBuilder ConnectionFactory(IExpression name) { _connectionFactory = name.ToTemplateString(); return this; }

    /// <summary>Client connection name. Default "redb.Route".</summary>
    public RabbitBuilder ClientName(IExpression name) { _clientName = name.ToTemplateString(); return this; }

    // ── Exchange ──────────────────────────────────────────────────────

    /// <summary>Exchange name and optionally type (direct, topic, fanout, headers).</summary>
    public RabbitBuilder Exchange(IExpression name, string? type = null)
    {
        _exchange = name.ToTemplateString(); if (type != null) _exchangeType = type; return this;
    }

    /// <summary>Exchange durability. Default true.</summary>
    public RabbitBuilder ExchangeDurable(bool durable = true) { _exchangeDurable = durable; return this; }

    /// <summary>Exchange auto-delete when last consumer unsubscribes.</summary>
    public RabbitBuilder ExchangeAutoDelete() { _exchangeAutoDelete = true; return this; }

    /// <summary>Declare exchange and queue on startup.</summary>
    public RabbitBuilder Declare() { _declare = true; return this; }

    // ── Queue ─────────────────────────────────────────────────────────

    /// <summary>Queue durability. Default true.</summary>
    public RabbitBuilder Durable(bool durable = true) { _durable = durable; return this; }

    /// <summary>Auto-delete queue.</summary>
    public RabbitBuilder AutoDelete() { _autoDelete = true; return this; }

    /// <summary>Exclusive queue (only this connection).</summary>
    public RabbitBuilder Exclusive() { _exclusive = true; return this; }

    /// <summary>Routing key for binding.</summary>
    public RabbitBuilder RoutingKey(IExpression key) { _routingKey = key.ToTemplateString(); return this; }

    // ── Message ───────────────────────────────────────────────────────

    /// <summary>Content type. Default "application/json".</summary>
    public RabbitBuilder ContentType(string type) { _contentType = type; return this; }

    /// <summary>Per-message TTL in milliseconds.</summary>
    public RabbitBuilder MessageTtl(int ms) { _messageTtl = ms.ToString(); return this; }
    /// <summary>Per-message TTL from an expression.</summary>
    public RabbitBuilder MessageTtl(IExpression ms) { _messageTtl = ms.ToTemplateString(); return this; }

    /// <summary>Queue expires (auto-delete) after ms of inactivity. 0 = never.</summary>
    public RabbitBuilder Expires(int ms) { _expires = ms.ToString(); return this; }
    /// <summary>Queue expiry from an expression.</summary>
    public RabbitBuilder Expires(IExpression ms) { _expires = ms.ToTemplateString(); return this; }

    // ── Consumer ──────────────────────────────────────────────────────

    /// <summary>Number of concurrent consumers. Default 1.</summary>
    public RabbitBuilder ConcurrentConsumers(int count) { _concurrentConsumers = count.ToString(); return this; }
    /// <summary>Concurrent consumers from an expression.</summary>
    public RabbitBuilder ConcurrentConsumers(IExpression count) { _concurrentConsumers = count.ToTemplateString(); return this; }

    /// <summary>Prefetch count. Default 10.</summary>
    public RabbitBuilder PrefetchCount(ushort count) { _prefetchCount = count.ToString(); return this; }
    /// <summary>Prefetch count from an expression.</summary>
    public RabbitBuilder PrefetchCount(IExpression count) { _prefetchCount = count.ToTemplateString(); return this; }

    /// <summary>
    /// Broker-side auto-acknowledge. When enabled the broker settles every delivery on hand-off
    /// (at-most-once): no manual ack/nack and a failed turn does NOT requeue. Default off (at-least-once).
    /// Cannot be combined with <see cref="Transacted"/>.
    /// </summary>
    public RabbitBuilder AutoAck(bool enabled = true) { _autoAck = enabled; return this; }

    /// <summary>Enable transacted channel.</summary>
    public RabbitBuilder Transacted() { _transacted = true; return this; }

    /// <summary>Set mandatory flag on published messages.</summary>
    public RabbitBuilder Mandatory() { _mandatory = true; return this; }

    /// <summary>Enable request-reply with temporary reply queue.</summary>
    public RabbitBuilder ReplyTo() { _replyTo = true; return this; }

    /// <summary>Consumer/producer timeout in seconds. Default 60.</summary>
    public RabbitBuilder Timeout(int seconds) { _timeout = seconds.ToString(); return this; }
    /// <summary>Timeout from an expression.</summary>
    public RabbitBuilder Timeout(IExpression seconds) { _timeout = seconds.ToTemplateString(); return this; }

    // ── Queue limits ──────────────────────────────────────────────────

    /// <summary>Max queue length (number of messages).</summary>
    public RabbitBuilder MaxLength(int max) { _maxLength = max.ToString(); return this; }
    /// <summary>Max queue length from an expression.</summary>
    public RabbitBuilder MaxLength(IExpression max) { _maxLength = max.ToTemplateString(); return this; }

    /// <summary>Max queue size in bytes.</summary>
    public RabbitBuilder MaxLengthBytes(int max) { _maxLengthBytes = max.ToString(); return this; }
    /// <summary>Max queue size from an expression.</summary>
    public RabbitBuilder MaxLengthBytes(IExpression max) { _maxLengthBytes = max.ToTemplateString(); return this; }

    /// <summary>Overflow strategy: "drop-head", "reject-publish".</summary>
    public RabbitBuilder Overflow(string policy) { _overflow = policy; return this; }

    /// <summary>Dead letter exchange name.</summary>
    public RabbitBuilder DeadLetterExchange(IExpression exchange) { _deadLetterExchange = exchange.ToTemplateString(); return this; }

    /// <summary>Dead letter routing key.</summary>
    public RabbitBuilder DeadLetterRoutingKey(IExpression key) { _deadLetterRoutingKey = key.ToTemplateString(); return this; }

    /// <summary>Queue type: "classic", "quorum", "stream".</summary>
    public RabbitBuilder QueueType(string type) { _queueType = type; return this; }

    /// <summary>Max priority level. Default 0 (disabled).</summary>
    public RabbitBuilder MaxPriority(int priority) { _maxPriority = priority.ToString(); return this; }
    /// <summary>Max priority from an expression.</summary>
    public RabbitBuilder MaxPriority(IExpression priority) { _maxPriority = priority.ToTemplateString(); return this; }

    // ── Connection settings ───────────────────────────────────────────

    /// <summary>Enable automatic connection recovery. Default true.</summary>
    public RabbitBuilder AutomaticRecovery(bool enabled = true) { _automaticRecovery = enabled; return this; }

    /// <summary>Enable topology recovery. Default true.</summary>
    public RabbitBuilder TopologyRecoveryEnabled(bool enabled = true) { _topologyRecoveryEnabled = enabled; return this; }

    /// <summary>Recovery interval in seconds. Default 5.</summary>
    public RabbitBuilder RecoveryInterval(int seconds) { _recoveryInterval = seconds.ToString(); return this; }
    /// <summary>Recovery interval from an expression.</summary>
    public RabbitBuilder RecoveryInterval(IExpression seconds) { _recoveryInterval = seconds.ToTemplateString(); return this; }

    /// <summary>Heartbeat interval in seconds. Default 60.</summary>
    public RabbitBuilder Heartbeat(int seconds) { _heartbeat = seconds.ToString(); return this; }
    /// <summary>Heartbeat interval from an expression.</summary>
    public RabbitBuilder Heartbeat(IExpression seconds) { _heartbeat = seconds.ToTemplateString(); return this; }

    /// <summary>Connection timeout in seconds. Default 60.</summary>
    public RabbitBuilder ConnectionTimeout(int seconds) { _connectionTimeout = seconds.ToString(); return this; }
    /// <summary>Connection timeout from an expression.</summary>
    public RabbitBuilder ConnectionTimeout(IExpression seconds) { _connectionTimeout = seconds.ToTemplateString(); return this; }

    /// <summary>Socket read timeout in seconds. Default 30.</summary>
    public RabbitBuilder SocketReadTimeout(int seconds) { _socketReadTimeout = seconds.ToString(); return this; }
    /// <summary>Socket read timeout from an expression.</summary>
    public RabbitBuilder SocketReadTimeout(IExpression seconds) { _socketReadTimeout = seconds.ToTemplateString(); return this; }

    /// <summary>Socket write timeout in seconds. Default 30.</summary>
    public RabbitBuilder SocketWriteTimeout(int seconds) { _socketWriteTimeout = seconds.ToString(); return this; }
    /// <summary>Socket write timeout from an expression.</summary>
    public RabbitBuilder SocketWriteTimeout(IExpression seconds) { _socketWriteTimeout = seconds.ToTemplateString(); return this; }

    /// <summary>Continuation timeout in seconds. Default 30.</summary>
    public RabbitBuilder ContinuationTimeout(int seconds) { _continuationTimeout = seconds.ToString(); return this; }
    /// <summary>Continuation timeout from an expression.</summary>
    public RabbitBuilder ContinuationTimeout(IExpression seconds) { _continuationTimeout = seconds.ToTemplateString(); return this; }

    /// <summary>Consumer dispatch concurrency. Default 1.</summary>
    public RabbitBuilder ConsumerDispatchConcurrency(ushort count) { _consumerDispatchConcurrency = count.ToString(); return this; }
    /// <summary>Consumer dispatch concurrency from an expression.</summary>
    public RabbitBuilder ConsumerDispatchConcurrency(IExpression count) { _consumerDispatchConcurrency = count.ToTemplateString(); return this; }

    // ── SSL ───────────────────────────────────────────────────────────

    /// <summary>Enable SSL with optional server name and client certificate.</summary>
    public RabbitBuilder Ssl(IExpression? serverName = null, IExpression? certPath = null, IExpression? certPassphrase = null)
    {
        _ssl = true; _sslServerName = serverName?.ToTemplateString(); _sslCertPath = certPath?.ToTemplateString(); _sslCertPassphrase = certPassphrase?.ToTemplateString();
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the RabbitMQ endpoint URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("rabbitmq:");
        sb.Append(_queue);

        var sep = '?';

        void Append(string key, string value)
        {
            sb.Append(sep); sb.Append(key); sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value)); sep = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }
        void AppendBoolExplicit(string key, bool? value)
        {
            if (value.HasValue) Append(key, value.Value.ToString().ToLowerInvariant());
        }

        // Connection
        AppendIf("host", _host);
        AppendIf("port", _port);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("virtualHost", _virtualHost);
        AppendIf("connectionFactory", _connectionFactory);
        AppendIf("clientName", _clientName);

        // Exchange
        AppendIf("exchange", _exchange);
        AppendIf("exchangeType", _exchangeType);
        AppendBoolExplicit("exchangeDurable", _exchangeDurable);
        AppendBoolExplicit("exchangeAutoDelete", _exchangeAutoDelete);
        AppendBool("declare", _declare);

        // Queue
        AppendBoolExplicit("durable", _durable);
        AppendBool("autoDelete", _autoDelete);
        AppendBool("exclusive", _exclusive);
        AppendIf("routingKey", _routingKey);

        // Message
        AppendIf("contentType", _contentType);
        AppendIf("messageTtl", _messageTtl);
        AppendIf("expires", _expires);

        // Consumer
        AppendIf("concurrentConsumers", _concurrentConsumers);
        AppendIf("prefetchCount", _prefetchCount);
        AppendBool("autoAck", _autoAck);
        AppendBool("transacted", _transacted);
        AppendBoolExplicit("mandatory", _mandatory);
        AppendBool("replyTo", _replyTo);
        AppendIf("timeout", _timeout);

        // Queue limits
        AppendIf("maxLength", _maxLength);
        AppendIf("maxLengthBytes", _maxLengthBytes);
        AppendIf("overflow", _overflow);
        AppendIf("deadLetterExchange", _deadLetterExchange);
        AppendIf("deadLetterRoutingKey", _deadLetterRoutingKey);
        AppendIf("queueType", _queueType);
        AppendIf("maxPriority", _maxPriority);

        // Connection settings
        AppendBoolExplicit("automaticRecovery", _automaticRecovery);
        AppendBoolExplicit("topologyRecoveryEnabled", _topologyRecoveryEnabled);
        AppendIf("recoveryInterval", _recoveryInterval);
        AppendIf("heartbeat", _heartbeat);
        AppendIf("connectionTimeout", _connectionTimeout);
        AppendIf("socketReadTimeout", _socketReadTimeout);
        AppendIf("socketWriteTimeout", _socketWriteTimeout);
        AppendIf("continuationTimeout", _continuationTimeout);
        AppendIf("consumerDispatchConcurrency", _consumerDispatchConcurrency);

        // SSL
        AppendBool("ssl", _ssl);
        AppendIf("sslServerName", _sslServerName);
        AppendIf("sslCertPath", _sslCertPath);
        AppendIf("sslCertPassphrase", _sslCertPassphrase);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(RabbitBuilder b) => b.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
