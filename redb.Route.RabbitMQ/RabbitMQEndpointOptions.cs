using redb.Route.Core;

namespace redb.Route.RabbitMQ;

/// <summary>
/// Typed options for <see cref="RabbitMQEndpoint"/>. Bound from URI query parameters.
/// </summary>
public sealed class RabbitMQEndpointOptions : EndpointOptions
{
    // ── Connection ──

    /// <summary>RabbitMQ host name (default: localhost).</summary>
    [ConnectionParameter]
    public string Host { get; set; } = "localhost";

    /// <summary>RabbitMQ port (default: 5672).</summary>
    [ConnectionParameter]
    public int Port { get; set; } = 5672;

    /// <summary>RabbitMQ username (default: guest).</summary>
    [ConnectionParameter]
    public string Username { get; set; } = "guest";

    /// <summary>RabbitMQ password (default: guest).</summary>
    [Sensitive]
    [ConnectionParameter]
    public string Password { get; set; } = "guest";

    /// <summary>Virtual host (default: /).</summary>
    [ConnectionParameter]
    public string VirtualHost { get; set; } = "/";

    /// <summary>Name of <see cref="RabbitMQConnectionFactory"/> in the route registry.</summary>
    [ConnectionFactoryReference]
    public string? ConnectionFactory { get; set; }

    /// <summary>Client-provided name for RabbitMQ connections.</summary>
    [ConnectionParameter]
    public string ClientName { get; set; } = "redb.Route";

    // ── Exchange ──

    /// <summary>Exchange name (empty = default exchange).</summary>
    public string Exchange { get; set; } = string.Empty;

    /// <summary>Exchange type: direct, topic, fanout, headers (default: direct), or an x-prefixed plugin type such as x-delayed-message.</summary>
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
    // A string so that "auto" binds verbatim instead of silently degrading to the int default
    // (план лимитов, решение В-7); resolved once via ConcurrencyOption.Resolve.
    public string? ConcurrentConsumers { get; set; }

    /// <summary>Resolved consumer parallelism: 1 by default, N, or auto = max(CPU, 2).</summary>
    public int ResolvedConcurrentConsumers => ConcurrencyOption.Resolve(ConcurrentConsumers, "concurrentConsumers");

    /// <summary>Prefetch count per consumer (default: 10). Should be &gt;= <see cref="ConcurrentConsumers"/>
    /// so the broker can keep the parallel slots fed.</summary>
    public ushort PrefetchCount { get; set; } = 10;

    /// <summary>
    /// When the delivery is settled (<see cref="Core.AckMode"/>). <c>Manual</c> (default): the consumer acks after a
    /// turn that ended well and nack-requeues on failure (at-least-once). <c>Auto</c>: the consumer subscribes with
    /// <c>autoAck: true</c> and the broker settles every delivery on hand-off (at-most-once); a failure in the route does
    /// not requeue it. <c>Auto</c> cannot be combined with <see cref="Transacted"/>.
    /// </summary>
    public AckMode AckMode { get; set; } = AckMode.Manual;

    // ── Transactions ──

    /// <summary>
    /// Consumer: <c>true</c> takes the delivery on a transacted channel. Producer: whether the send joins the enclosing
    /// <c>.Transacted()</c> block. Unset, it follows the block: deferred until the database commits inside one, sent at
    /// once outside. <c>true</c> requires a block and fails outside one; <c>false</c> sends at once even inside one.
    /// A request-reply producer (<see cref="ReplyTo"/>) always sends at once and refuses <c>true</c>.
    /// </summary>
    public bool? Transacted { get; set; }

    /// <summary>
    /// Publish over a connection of its own (default: false, one connection for everything with the same settings, as in
    /// Spring AMQP). The broker's flow control and resource alarms block a publishing connection as a whole, so a
    /// consumer on it can no longer ack. With <c>true</c>, the sends of this endpoint's producer and the RPC replies of
    /// its consumer go over a second connection with the same settings (named "... (publisher)"), and the consumer keeps
    /// the first one to itself. Two named connection factories separate the connections just as well; only a consumer's
    /// replies need this option.
    /// </summary>
    public bool PublisherConnection { get; set; }

    /// <summary>Use mandatory flag. Null = auto-detect by exchange type.</summary>
    public bool? Mandatory { get; set; }

    // ── RPC ──

    /// <summary>
    /// Request-reply (RPC): the producer publishes the request and waits for the reply on a reply queue of its own. The
    /// request's AMQP <c>reply-to</c> is always that queue, overriding a <c>ReplyTo</c> header, since the producer is the
    /// one waiting. To send a message that names another reply address without waiting, set the <c>ReplyTo</c> header on
    /// an ordinary send.
    /// </summary>
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

    /// <summary>Queue type: classic, quorum, stream (x-queue-type). Quorum and stream queues are replicated across cluster nodes.</summary>
    public string? QueueType { get; set; }

    /// <summary>Maximum priority level for priority queues (x-max-priority). 0 = disabled.</summary>
    public int MaxPriority { get; set; }

    // ── Connection resilience ──

    /// <summary>Enable automatic recovery (default: true).</summary>
    [ConnectionParameter]
    public bool AutomaticRecovery { get; set; } = true;

    /// <summary>Re-declare topology after recovery (default: true). Critical for cluster.</summary>
    [ConnectionParameter]
    public bool TopologyRecoveryEnabled { get; set; } = true;

    /// <summary>Network recovery interval in seconds (default: 5).</summary>
    [ConnectionParameter]
    public int RecoveryInterval { get; set; } = 5;

    /// <summary>Requested heartbeat interval in seconds (default: 60).</summary>
    [ConnectionParameter]
    public int Heartbeat { get; set; } = 60;

    /// <summary>Connection timeout in seconds (default: 60). Prevents hangs on unreachable nodes.</summary>
    [ConnectionParameter]
    public int ConnectionTimeout { get; set; } = 60;

    /// <summary>Socket read timeout in seconds (default: 30).</summary>
    [ConnectionParameter]
    public int SocketReadTimeout { get; set; } = 30;

    /// <summary>Socket write timeout in seconds (default: 30).</summary>
    [ConnectionParameter]
    public int SocketWriteTimeout { get; set; } = 30;

    /// <summary>AMQP continuation timeout in seconds (default: 30).</summary>
    [ConnectionParameter]
    public int ContinuationTimeout { get; set; } = 30;

    /// <summary>Concurrent dispatch limit per connection (default: 1).</summary>
    [ConnectionParameter]
    public ushort ConsumerDispatchConcurrency { get; set; } = 1;

    // ── SSL ──

    /// <summary>Enable SSL/TLS for the connection.</summary>
    [ConnectionParameter]
    public bool Ssl { get; set; }

    /// <summary>
    /// The name the broker certificate must carry. Unset, each host of the list is checked against its own name; set it
    /// when the certificate names a load balancer or an alias.
    /// </summary>
    [ConnectionParameter]
    public string? SslServerName { get; set; }

    /// <summary>Client certificate (PFX) presented for mutual TLS; checked for its validity period at startup.</summary>
    [ConnectionParameter]
    public string? SslCertPath { get; set; }

    /// <summary>Passphrase of <see cref="SslCertPath"/>.</summary>
    [Sensitive]
    [ConnectionParameter]
    public string? SslCertPassword { get; set; }

    /// <summary>
    /// PEM file with the root certificates the broker certificate must chain to, instead of the system trust store — a
    /// corporate or private CA without installing it in the operating system.
    /// </summary>
    [ConnectionParameter]
    public string? SslCaCertPath { get; set; }

    /// <summary>
    /// Allowed TLS versions, <c>Tls12</c>, <c>Tls13</c> or <c>Tls12,Tls13</c>. Unset (<c>None</c>), the operating system
    /// chooses. Older versions are refused.
    /// </summary>
    [ConnectionParameter]
    public System.Security.Authentication.SslProtocols SslProtocols { get; set; }

    /// <summary>
    /// Revocation check of the broker certificate: <c>NoCheck</c> (default), <c>Online</c> (CRL and OCSP the
    /// certificate points at) or <c>Offline</c> (the operating system's cache only).
    /// </summary>
    [ConnectionParameter]
    public System.Security.Cryptography.X509Certificates.X509RevocationMode RevocationMode { get; set; }

    /// <summary>Accept a broker certificate whose revocation status cannot be determined. Default false: refused.</summary>
    [ConnectionParameter]
    public bool RevocationSoftFail { get; set; }

    /// <summary>
    /// <c>Plain</c> (default, username and password) or <c>External</c>: the TLS client certificate is the login, no
    /// password is sent. <c>External</c> needs <c>ssl=true</c> and <c>sslCertPath</c>.
    /// </summary>
    [ConnectionParameter]
    public RabbitMQAuthMechanism AuthMechanism { get; set; }

    /// <summary>The TLS settings of this endpoint's connection, loading the certificate files it names.</summary>
    internal RabbitMQTlsSettings ResolveTls(string owner) => !Ssl
        ? RabbitMQTlsSettings.Off
        : new RabbitMQTlsSettings(
            true,
            SslServerName,
            string.IsNullOrEmpty(SslCertPath) ? null : RabbitMQTls.LoadClientCertificate(SslCertPath, SslCertPassword, owner),
            SslProtocols,
            string.IsNullOrEmpty(SslCaCertPath) ? null : RabbitMQTls.LoadCaCertificates(SslCaCertPath, owner),
            RevocationMode,
            RevocationSoftFail);

    /// <inheritdoc />
    protected override string? UnknownParameterHint(string name)
        => name.Equals("autoAck", StringComparison.OrdinalIgnoreCase)
            ? "'autoAck' is replaced by 'ackMode': ackMode=auto for autoAck=true, ackMode=manual (the default) for autoAck=false."
            : name.Equals("sslCertPassphrase", StringComparison.OrdinalIgnoreCase)
                ? "'sslCertPassphrase' is renamed 'sslCertPassword', the name every other connector uses."
                : null;

    /// <inheritdoc />
    public override void Validate()
    {
        if (PrefetchCount == 0)
            throw new ArgumentOutOfRangeException(nameof(PrefetchCount), "PrefetchCount must be greater than 0.");

        _ = ConcurrencyOption.Resolve(ConcurrentConsumers, "concurrentConsumers"); // loud on garbage/zero

        if (Timeout <= 0)
            throw new ArgumentOutOfRangeException(nameof(Timeout), "Timeout must be greater than 0.");

        if (AckMode == AckMode.Auto && Transacted == true)
            throw new ArgumentException(
                "ackMode=auto cannot be combined with Transacted: an auto-acked delivery is settled by the broker on hand-off and cannot be transactionally committed or rolled back.");

        // A value the broker does not know fails here, naming the option, instead of as a channel error at declare.
        // Exchange types beyond the built-in four come from plugins and are x-prefixed (x-delayed-message,
        // x-consistent-hash); the built-in names are written the way the broker names them, which is case-sensitive.
        var builtInExchangeType = BuiltInExchangeTypes.FirstOrDefault(t => t.Equals(ExchangeType, StringComparison.OrdinalIgnoreCase));
        if (builtInExchangeType is not null)
            ExchangeType = builtInExchangeType;
        else if (!ExchangeType.StartsWith("x-", StringComparison.Ordinal))
            throw new ArgumentException(
                $"Unknown 'exchangeType' value '{ExchangeType}'. Allowed: {string.Join(", ", BuiltInExchangeTypes)}, or a plugin type starting with 'x-'.");

        if (!string.IsNullOrEmpty(QueueType) && !QueueTypes.Contains(QueueType, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown 'queueType' value '{QueueType}'. Allowed: {string.Join(", ", QueueTypes)}.");

        if (!string.IsNullOrEmpty(Overflow) && !OverflowStrategies.Contains(Overflow, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown 'overflow' value '{Overflow}'. Allowed: {string.Join(", ", OverflowStrategies)}.");

        // The connection settings of a named factory are checked on the factory; on the URI they are refused next to it.
        if (string.IsNullOrEmpty(ConnectionFactory))
            RabbitMQTls.Validate(new RabbitMQTls.TlsRules(
                Ssl, SslServerName, SslCertPath, SslCertPassword, ClientCertificateObject: false,
                SslCaCertPath, CaCertificatesObject: false, SslProtocols, RevocationMode, RevocationSoftFail, AuthMechanism),
                "RabbitMQ endpoint");
    }


    private static readonly string[] BuiltInExchangeTypes = ["direct", "topic", "fanout", "headers"];
    private static readonly string[] QueueTypes = ["classic", "quorum", "stream"];
    private static readonly string[] OverflowStrategies = ["drop-head", "reject-publish", "reject-publish-dlx"];

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
