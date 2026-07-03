using redb.Route.Core;

namespace redb.Route.RabbitMQ;

/// <summary>
/// Typed options for <see cref="RabbitMQEndpoint"/>. Bound from URI query parameters.
/// </summary>
public sealed class RabbitMQEndpointOptions : EndpointOptions
{
    // ── Connection ──

    /// <summary>RabbitMQ host name (default: localhost).</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>RabbitMQ port (default: 5672).</summary>
    public int Port { get; set; } = 5672;

    /// <summary>RabbitMQ username (default: guest).</summary>
    public string Username { get; set; } = "guest";

    /// <summary>RabbitMQ password (default: guest).</summary>
    public string Password { get; set; } = "guest";

    /// <summary>Virtual host (default: /).</summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>Name of <see cref="RabbitMQConnectionFactory"/> in the route registry.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Client-provided name for RabbitMQ connections.</summary>
    public string ClientName { get; set; } = "redb.Route";

    // ── Exchange ──

    /// <summary>Exchange name (empty = default exchange).</summary>
    public string Exchange { get; set; } = string.Empty;

    /// <summary>Exchange type: direct, topic, fanout, headers (default: direct).</summary>
    public string ExchangeType { get; set; } = "direct";

    /// <summary>Whether exchange survives broker restart.</summary>
    public bool ExchangeDurable { get; set; } = true;

    /// <summary>Auto-delete exchange when no longer in use.</summary>
    public bool ExchangeAutoDelete { get; set; }

    /// <summary>Declare exchange and queue on startup.</summary>
    public bool Declare { get; set; }

    // ── Queue ──

    /// <summary>Queue name (empty = auto-generated exclusive queue).</summary>
    public string Queue { get; set; } = string.Empty;

    /// <summary>Whether queue survives broker restart.</summary>
    public bool Durable { get; set; } = true;

    /// <summary>Auto-delete queue when last consumer disconnects.</summary>
    public bool AutoDelete { get; set; }

    /// <summary>Only one consumer allowed on this queue.</summary>
    public bool Exclusive { get; set; }

    // ── Routing ──

    /// <summary>Routing key for publish and bind operations.</summary>
    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>Content type for published messages (default: application/json).</summary>
    public string ContentType { get; set; } = "application/json";

    // ── Consumer ──

    /// <summary>
    /// Consumer parallelism. Sizes both the channel's AMQP consumer-dispatch concurrency (how many
    /// deliveries the client hands to the processor in parallel) and the app-level concurrency
    /// semaphore. This is the single knob for consumer-side parallelism: <c>ConcurrentConsumers(N)</c>
    /// yields up to N messages processed concurrently on the queue. Default: 1 (serial).
    /// </summary>
    public int ConcurrentConsumers { get; set; } = 1;

    /// <summary>Prefetch count per consumer (default: 10). Should be &gt;= <see cref="ConcurrentConsumers"/>
    /// so the broker can keep the parallel slots fed.</summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// Broker-side auto-acknowledge (default: false). When <c>true</c> the consumer subscribes with
    /// <c>autoAck: true</c> — the broker settles every delivery on hand-off (at-most-once): there is no
    /// manual ack/nack, and a failure in the processor does NOT requeue the message. When <c>false</c>
    /// (default) the consumer acks after a successful turn and nack-requeues on failure (at-least-once).
    /// Cannot be combined with <see cref="Transacted"/>.
    /// </summary>
    public bool AutoAck { get; set; }

    // ── Transactions ──

    /// <summary>Enable transacted mode for the channel.</summary>
    public bool Transacted { get; set; }

    /// <summary>Use mandatory flag. Null = auto-detect by exchange type.</summary>
    public bool? Mandatory { get; set; }

    // ── RPC ──

    /// <summary>Enable request-reply (RPC) pattern.</summary>
    public bool ReplyTo { get; set; }

    /// <summary>RPC timeout in seconds (default: 60).</summary>
    public int Timeout { get; set; } = 60;

    /// <summary>
    /// Maximum number of unconfirmed (in-flight) publishes allowed on a producer channel
    /// before <see cref="RabbitMQ.Client.IChannel.BasicPublishAsync(string, string, bool, CancellationToken)"/>
    /// awaits broker confirmation. Default: 2048.
    /// <para>
    /// Bound to a <c>ThrottlingRateLimiter</c> wired into <c>CreateChannelOptions.outstandingPublisherConfirmationsRateLimiter</c>.
    /// Without this limit, RabbitMQ.Client 7.x falls back to its default rate limiter that returns
    /// "Could not acquire a lease from the rate limiter" under sustained publish bursts.
    /// </para>
    /// </summary>
    public int MaxOutstandingConfirms { get; set; } = 2048;

    // ── Queue arguments ──

    /// <summary>Message TTL in milliseconds (x-message-ttl). 0 = disabled.</summary>
    public int MessageTtl { get; set; }

    /// <summary>Queue expiry in milliseconds (x-expires). 0 = disabled.</summary>
    public int Expires { get; set; }

    /// <summary>Maximum queue length in messages (x-max-length). 0 = unlimited.</summary>
    public int MaxLength { get; set; }

    /// <summary>Maximum queue size in bytes (x-max-length-bytes). 0 = unlimited.</summary>
    public int MaxLengthBytes { get; set; }

    /// <summary>Overflow strategy: drop-head, reject-publish, reject-publish-dlx (x-overflow).</summary>
    public string? Overflow { get; set; }

    /// <summary>Dead-letter exchange name (x-dead-letter-exchange). Critical for error handling.</summary>
    public string? DeadLetterExchange { get; set; }

    /// <summary>Dead-letter routing key (x-dead-letter-routing-key).</summary>
    public string? DeadLetterRoutingKey { get; set; }

    /// <summary>Queue type: classic, quorum (x-queue-type). Quorum queues are replicated across cluster nodes.</summary>
    public string? QueueType { get; set; }

    /// <summary>Maximum priority level for priority queues (x-max-priority). 0 = disabled.</summary>
    public int MaxPriority { get; set; }

    // ── Connection resilience ──

    /// <summary>Enable automatic recovery (default: true).</summary>
    public bool AutomaticRecovery { get; set; } = true;

    /// <summary>Re-declare topology after recovery (default: true). Critical for cluster.</summary>
    public bool TopologyRecoveryEnabled { get; set; } = true;

    /// <summary>Network recovery interval in seconds (default: 5).</summary>
    public int RecoveryInterval { get; set; } = 5;

    /// <summary>Requested heartbeat interval in seconds (default: 60).</summary>
    public int Heartbeat { get; set; } = 60;

    /// <summary>Connection timeout in seconds (default: 60). Prevents hangs on unreachable nodes.</summary>
    public int ConnectionTimeout { get; set; } = 60;

    /// <summary>Socket read timeout in seconds (default: 30).</summary>
    public int SocketReadTimeout { get; set; } = 30;

    /// <summary>Socket write timeout in seconds (default: 30).</summary>
    public int SocketWriteTimeout { get; set; } = 30;

    /// <summary>AMQP continuation timeout in seconds (default: 30).</summary>
    public int ContinuationTimeout { get; set; } = 30;

    /// <summary>Concurrent dispatch limit per connection (default: 1).</summary>
    public ushort ConsumerDispatchConcurrency { get; set; } = 1;

    // ── SSL ──

    /// <summary>Enable SSL/TLS for the connection.</summary>
    public bool Ssl { get; set; }

    /// <summary>SSL server name for certificate validation.</summary>
    public string? SslServerName { get; set; }

    /// <summary>Path to client certificate file (for mTLS).</summary>
    public string? SslCertPath { get; set; }

    /// <summary>Client certificate passphrase.</summary>
    public string? SslCertPassphrase { get; set; }

    /// <inheritdoc />
    public override void Validate()
    {
        if (PrefetchCount == 0)
            throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be greater than 0.");

        if (ConcurrentConsumers <= 0)
            throw new ArgumentOutOfRangeException(nameof(ConcurrentConsumers), "ConcurrentConsumers must be greater than 0.");

        if (Timeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be greater than 0.");

        if (AutoAck && Transacted)
            throw new ArgumentException(
                "AutoAck cannot be combined with Transacted: an auto-acked delivery is settled by the broker on hand-off and cannot be transactionally committed or rolled back.");
    }

    /// <summary>
    /// Resolves the mandatory flag. If not explicitly set, uses sensible defaults
    /// based on exchange type.
    /// </summary>
    internal bool ResolveMandatory()
    {
        if (Mandatory.HasValue) return Mandatory.Value;

        return ExchangeType.ToLowerInvariant() switch
        {
            "topic" or "fanout" => false,
            _ => true
        };
    }

    /// <summary>Builds queue arguments dictionary.</summary>
    internal Dictionary<string, object> BuildQueueArguments()
    {
        var args = new Dictionary<string, object>();
        if (MessageTtl > 0) args["x-message-ttl"] = MessageTtl;
        if (Expires > 0) args["x-expires"] = Expires;
        if (MaxLength > 0) args["x-max-length"] = MaxLength;
        if (MaxLengthBytes > 0) args["x-max-length-bytes"] = MaxLengthBytes;
        if (!string.IsNullOrEmpty(Overflow)) args["x-overflow"] = Overflow;
        if (!string.IsNullOrEmpty(DeadLetterExchange)) args["x-dead-letter-exchange"] = DeadLetterExchange;
        if (!string.IsNullOrEmpty(DeadLetterRoutingKey)) args["x-dead-letter-routing-key"] = DeadLetterRoutingKey;
        if (!string.IsNullOrEmpty(QueueType)) args["x-queue-type"] = QueueType;
        if (MaxPriority > 0) args["x-max-priority"] = MaxPriority;
        return args;
    }
}
