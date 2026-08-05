using redb.Route.Core;

namespace redb.Route.IbmMq;

/// <summary>
/// Destination type for IBM MQ endpoints.
/// </summary>
public enum IbmMqDestinationType
{
    /// <summary>Point-to-point queue.</summary>
    Queue,

    /// <summary>Publish/subscribe topic.</summary>
    Topic
}

/// <summary>
/// Consumer receive strategy.
/// </summary>
public enum IbmMqReceiveMode
{
    /// <summary>
    /// Synchronous MQGET-WAIT poll loop (IBM.WMQ). Default, battle-tested, but the managed
    /// client carries an internal ~500&#160;ms poll tick, so delivery latency is ~250–500&#160;ms.
    /// </summary>
    Poll,

    /// <summary>
    /// Event-driven push via XMS .NET <c>MessageListener</c> (IBM.XMS). The broker pushes
    /// messages the instant they arrive, dropping delivery latency to &lt;50&#160;ms. This is the
    /// only supported event-driven receive path on the managed .NET client (IBM.WMQ exposes no
    /// public MQCB). Opt-in — <see cref="Poll"/> remains the default fallback.
    /// </summary>
    Listener
}

/// <summary>
/// IBM MQ message persistence mode.
/// </summary>
public enum IbmMqPersistence
{
    /// <summary>Use the queue's default persistence setting.</summary>
    AsQDef = -1,

    /// <summary>Non-persistent (faster, may be lost on restart).</summary>
    NonPersistent = 0,

    /// <summary>Persistent (written to disk, survives restart).</summary>
    Persistent = 1
}

/// <summary>
/// IBM MQ message type (MQMT_*).
/// </summary>
public enum IbmMqMessageType
{
    /// <summary>Normal one-way message.</summary>
    Datagram = 8,

    /// <summary>Request expecting a reply.</summary>
    Request = 1,

    /// <summary>Reply to a request.</summary>
    Reply = 2,

    /// <summary>Report message.</summary>
    Report = 4
}

/// <summary>
/// Target client mode — controls whether MQRFH2 headers are included.
/// </summary>
public enum IbmMqTargetClient
{
    /// <summary>Include MQRFH2 for JMS interoperability.</summary>
    Jms,

    /// <summary>Raw MQMD only — no RFH2 header.</summary>
    Mq
}

/// <summary>
/// RPC correlation pattern — how replies are matched to requests.
/// </summary>
public enum IbmMqCorrelationPattern
{
    /// <summary>Reply CorrelId = Request MsgId (standard IBM MQ pattern).</summary>
    MsgId,

    /// <summary>Reply CorrelId = Request CorrelId (JMS pattern).</summary>
    CorrelId
}

/// <summary>
/// Endpoint options for IBM MQ transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>ibmmq:QUEUE.NAME?host=mq-host&amp;port=1414&amp;channel=DEV.APP.SVRCONN&amp;queueManager=QM1</c>
/// </para>
/// </summary>
public sealed class IbmMqEndpointOptions : EndpointOptions
{
    // ── Connection ───────────────────────────────────────────────────

    /// <summary>Named connection factory from registry. Takes priority over inline params.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Queue manager hostname. (default: localhost)</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Queue manager listener port. (default: 1414)</summary>
    public int Port { get; set; } = 1414;

    /// <summary>Server-connection channel name. (default: DEV.APP.SVRCONN)</summary>
    public string Channel { get; set; } = "DEV.APP.SVRCONN";

    /// <summary>Queue manager name. (required)</summary>
    public string QueueManager { get; set; } = "QM1";

    /// <summary>Authentication user.</summary>
    public string? User { get; set; }

    /// <summary>Authentication password.</summary>
    [Sensitive]
    public string? Password { get; set; }

    /// <summary>Client identifier for connection tracking.</summary>
    public string? ClientId { get; set; }

    // ── Destination ──────────────────────────────────────────────────

    /// <summary>Destination type: Queue or Topic. (default: Queue)</summary>
    public IbmMqDestinationType DestinationType { get; set; } = IbmMqDestinationType.Queue;

    // ── Consumer ─────────────────────────────────────────────────────

    /// <summary>
    /// Receive strategy: <c>Poll</c> (synchronous MQGET-WAIT, default) or <c>Listener</c>
    /// (event-driven XMS <c>MessageListener</c>, &lt;50&#160;ms latency). (default: Poll)
    /// </summary>
    public IbmMqReceiveMode ReceiveMode { get; set; } = IbmMqReceiveMode.Poll;

    /// <summary>Number of concurrent consumer threads. (default: 1)</summary>
    public int ConcurrentConsumers { get; set; } = 1;

    /// <summary>MQGET wait interval in milliseconds. 0 = no wait. (default: 5000)</summary>
    public int WaitInterval { get; set; } = 5000;

    /// <summary>Batch size for bulk receive. 0 = single message mode. (default: 0)</summary>
    public int BatchSize { get; set; }

    /// <summary>Backout threshold — messages exceeding this are sent to BackoutQueue. 0 = disabled.</summary>
    public int BackoutThreshold { get; set; }

    /// <summary>Queue to route poison messages to. Falls back to BOQNAME from queue definition.</summary>
    public string? BackoutQueue { get; set; }

    /// <summary>Message selector expression (for topic subscriptions).</summary>
    public string? Selector { get; set; }

    /// <summary>Apply MQGMO_CONVERT on receive. (default: true)</summary>
    public bool Convert { get; set; } = true;

    // ── Producer ─────────────────────────────────────────────────────

    /// <summary>Message persistence mode. (default: AsQDef)</summary>
    public IbmMqPersistence Persistence { get; set; } = IbmMqPersistence.AsQDef;

    /// <summary>Expression override for persistence (resolves to enum name or numeric value).</summary>
    public string? PersistenceExpression { get; set; }

    /// <summary>Message priority. -1 = use queue default. (default: -1)</summary>
    public int Priority { get; set; } = -1;

    /// <summary>Expression override for priority (resolves to int).</summary>
    public string? PriorityExpression { get; set; }

    /// <summary>Message expiry in tenths of a second. -1 = unlimited. (default: -1)</summary>
    public int Expiry { get; set; } = -1;

    /// <summary>Expression override for expiry (resolves to int).</summary>
    public string? ExpiryExpression { get; set; }

    /// <summary>Target client mode — Jms (with MQRFH2) or Mq (raw MQMD). (default: Jms)</summary>
    public IbmMqTargetClient TargetClient { get; set; } = IbmMqTargetClient.Jms;

    /// <summary>Message type for outgoing messages. (default: Datagram)</summary>
    public IbmMqMessageType MessageType { get; set; } = IbmMqMessageType.Datagram;

    /// <summary>Expression override for message type (resolves to enum name or numeric value).</summary>
    public string? MessageTypeExpression { get; set; }

    // ── Transactions ─────────────────────────────────────────────────

    /// <summary>Enable local MQ transactions (MQCMIT/MQBACK). (default: false)</summary>
    public bool Transacted { get; set; }

    // ── Dead Letter ──────────────────────────────────────────────────

    /// <summary>Dead-letter queue for failed messages. If empty, uses queue manager's DLQ.</summary>
    public string? DeadLetterQueue { get; set; }

    /// <summary>Max redelivery attempts before sending to DeadLetterQueue. 0 = disabled.</summary>
    public int MaxRedeliveries { get; set; }

    // ── RPC (Request/Reply) ──────────────────────────────────────────

    /// <summary>Enable request/reply pattern. (default: false)</summary>
    public bool ReplyTo { get; set; }

    /// <summary>Reply-to queue name. If empty, uses dynamic/temporary reply queue.</summary>
    public string? ReplyToQueue { get; set; }

    /// <summary>Reply-to queue manager. If empty, uses local queue manager.</summary>
    public string? ReplyToQueueManager { get; set; }

    /// <summary>RPC timeout in seconds. (default: 30)</summary>
    public int Timeout { get; set; } = 30;

    /// <summary>How to correlate RPC replies. (default: MsgId)</summary>
    public IbmMqCorrelationPattern CorrelationPattern { get; set; } = IbmMqCorrelationPattern.MsgId;

    // ── SSL/TLS ──────────────────────────────────────────────────────

    /// <summary>SSL CipherSpec name (e.g. TLS_RSA_WITH_AES_256_CBC_SHA256).</summary>
    public string? SslCipherSpec { get; set; }

    /// <summary>SSL certificate label in keystore.</summary>
    public string? SslCertLabel { get; set; }

    /// <summary>Distinguished Name pattern for peer validation.</summary>
    public string? SslPeerName { get; set; }

    /// <summary>Path to the key repository (.kdb file without extension).</summary>
    public string? SslKeyRepository { get; set; }

    /// <summary>Number of bytes before SSL secret key is renegotiated. 0 = disabled.</summary>
    public int SslKeyResetCount { get; set; }

    // ── Advanced ─────────────────────────────────────────────────────

    /// <summary>Coded Character Set ID for message body. (default: 1208 = UTF-8)</summary>
    public int CCSID { get; set; } = 1208;

    /// <summary>
    /// Allow writing the <b>advanced/raw</b> MQMD fields from exchange headers — message type, format,
    /// group id, message sequence number. (default: false, mirrors IBM MQ JMS WMQ_MQMD_WRITE_ENABLED)
    /// <para>The standard JMS-equivalent fields (correlation id, priority, expiry, persistence,
    /// reply-to queue/manager) always forward from headers regardless of this flag.</para>
    /// </summary>
    public bool MqmdWriteEnabled { get; set; }

    /// <summary>Populate all MQMD fields into exchange headers on receive. (default: true)</summary>
    public bool MqmdReadEnabled { get; set; } = true;

    /// <inheritdoc />
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(QueueManager))
            throw new ArgumentException("IBM MQ queueManager is required.");

        if (string.IsNullOrWhiteSpace(Host))
            throw new ArgumentException("IBM MQ host is required.");

        if (string.IsNullOrWhiteSpace(Channel))
            throw new ArgumentException("IBM MQ channel is required.");

        if (Port is < 1 or > 65535)
            throw new ArgumentException($"IBM MQ port must be 1–65535, got: {Port}");

        if (ConcurrentConsumers < 1)
            throw new ArgumentException($"IBM MQ concurrentConsumers must be >= 1, got: {ConcurrentConsumers}");

        if (WaitInterval < 0)
            throw new ArgumentException($"IBM MQ waitInterval must be >= 0, got: {WaitInterval}");

        if (BatchSize < 0)
            throw new ArgumentException($"IBM MQ batchSize must be >= 0, got: {BatchSize}");

        if (Timeout < 1)
            throw new ArgumentException($"IBM MQ rpc timeout must be >= 1, got: {Timeout}");
    }
}
