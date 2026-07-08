using redb.Route.Core;
using AmqpSymbol = global::Amqp.Types.Symbol;

namespace redb.Route.Amqp;

/// <summary>
/// Endpoint options for AMQP 1.0 transport. All properties are auto-bound from URI query parameters.
/// <para>
/// URI format: <c>amqp://address?host=broker&amp;port=5672&amp;user=admin&amp;password=admin&amp;...</c>
/// </para>
/// <para>
/// Supports: ActiveMQ Artemis, ActiveMQ Classic, Azure Service Bus, Amazon MQ, Qpid, any AMQP 1.0 broker.
/// </para>
/// </summary>
public sealed class AmqpEndpointOptions : EndpointOptions
{
    // ── Connection (override per-endpoint, fallback to component-level factory) ──

    /// <summary>Named connection factory from registry. Takes priority over inline params.</summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Broker host. Comma-separated for failover. (default: localhost)</summary>
    public string Host { get; set; } = "localhost";

    /// <summary>Broker port. (default: 5672)</summary>
    public int Port { get; set; } = 5672;

    /// <summary>SASL username.</summary>
    public string? User { get; set; }

    /// <summary>SASL password.</summary>
    public string? Password { get; set; }

    /// <summary>Container ID for this connection.</summary>
    public string? ContainerId { get; set; }

    /// <summary>Virtual host / AMQP hostname.</summary>
    public string? VirtualHost { get; set; }

    /// <summary>Enable SSL/TLS.</summary>
    public bool Ssl { get; set; }

    // ── AMQP terminus (Source/Target) ──

    /// <summary>
    /// Terminus durability: 0 = none, 1 = configuration, 2 = unsettled-state.
    /// Maps to <c>Source.Durable</c> / <c>Target.Durable</c>. (default: 0)
    /// </summary>
    public uint Durable { get; set; }

    /// <summary>
    /// Expiry policy for the terminus: link-detach, session-end, connection-close, never. (default: session-end)
    /// </summary>
    public string ExpiryPolicy { get; set; } = "session-end";

    /// <summary>Terminus timeout in seconds. (default: 0)</summary>
    public uint TerminusTimeout { get; set; }

    /// <summary>Distribution mode for the source: move or copy. (default: move)</summary>
    public string? DistributionMode { get; set; }

    /// <summary>Create dynamic terminus (broker assigns name). (default: false)</summary>
    public bool Dynamic { get; set; }

    /// <summary>
    /// JMS/AMQP selector filter expression. Applied as a Source filter.
    /// Example: <c>color='red' AND weight &gt; 10</c>
    /// </summary>
    public string? FilterSelector { get; set; }

    /// <summary>
    /// Terminus capabilities. Comma-separated AMQP symbols.
    /// Examples: <c>topic</c>, <c>queue</c>, <c>shared</c>, <c>global</c>.
    /// Artemis uses these to distinguish address types.
    /// </summary>
    public string? Capabilities { get; set; }

    // ── Link settlement ──

    /// <summary>
    /// Sender settle mode: Unsettled (0), Settled (1), Mixed (2). (default: Mixed)
    /// Controls at-most-once vs at-least-once delivery for producers.
    /// </summary>
    public int SenderSettleMode { get; set; } = 2;

    /// <summary>
    /// Receiver settle mode: First (0 = auto-ack), Second (1 = explicit ack). (default: First)
    /// </summary>
    public int ReceiverSettleMode { get; set; }

    // ── Consumer ──

    /// <summary>Link credit (prefetch). How many messages the broker can send before ack. (default: 100)</summary>
    public int Credit { get; set; } = 100;

    /// <summary>Auto-accept (settle) messages after processing. (default: true)</summary>
    public bool AutoAccept { get; set; } = true;

    /// <summary>Number of concurrent consumer processors. (default: 1)</summary>
    public int ConcurrentConsumers { get; set; } = 1;

    /// <summary>Consumer receive timeout in seconds. 0 = infinite wait. (default: 60)</summary>
    public int ReceiveTimeout { get; set; } = 60;

    // ── Producer: message defaults ──

    /// <summary>Set AMQP header.durable on outgoing messages. (default: true)</summary>
    public bool MessageDurable { get; set; } = true;

    /// <summary>Default message priority (0–9). (default: 4)</summary>
    public byte MessagePriority { get; set; } = 4;

    /// <summary>Message priority as expression (e.g. <c>${body['priority']}</c>). Resolved at runtime, overrides <see cref="MessagePriority"/> if set.</summary>
    public string? MessagePriorityExpression { get; set; }

    /// <summary>Default message TTL in milliseconds. 0 = no expiry.</summary>
    public uint MessageTtl { get; set; }

    /// <summary>Message TTL as expression (e.g. <c>${body['ttl']}</c>). Resolved at runtime, overrides <see cref="MessageTtl"/> if set.</summary>
    public string? MessageTtlExpression { get; set; }

    /// <summary>Default content-type for outgoing messages. (default: text/plain)</summary>
    public string ContentType { get; set; } = "text/plain";

    /// <summary>AMQP subject/routing property on outgoing messages.</summary>
    public string? Subject { get; set; }

    /// <summary>AMQP group-id for message grouping.</summary>
    public string? GroupId { get; set; }

    /// <summary>Enable request/reply pattern (creates temp reply-to address). (default: false)</summary>
    public bool ReplyTo { get; set; }

    /// <summary>Request/reply timeout in seconds. (default: 30)</summary>
    public int Timeout { get; set; } = 30;

    /// <summary>
    /// Send-side timeout (seconds) for the consumer's RPC reply <c>SenderLink.SendAsync</c>.
    /// Kept short on purpose: if the originating client has gone away its reply address is
    /// dead, and a long send timeout would block the consumer slot. (default: 2)
    /// </summary>
    public int ReplyTimeout { get; set; } = 2;

    // ── Transacted ──

    /// <summary>Enable transacted mode (AMQP transactions via Coordinator). (default: false)</summary>
    public bool Transacted { get; set; }

    // ── Topology ──

    /// <summary>
    /// Declare the address on the broker if it doesn't exist. Requires broker support.
    /// Artemis auto-creates by default. (default: false)
    /// </summary>
    public bool Declare { get; set; }

    /// <summary>
    /// Routing type for Artemis: ANYCAST (queue semantics) or MULTICAST (topic semantics).
    /// Mapped to capabilities. (default: ANYCAST)
    /// </summary>
    public string RoutingType { get; set; } = "ANYCAST";

    /// <inheritdoc />
    public override void Validate()
    {
        if (Port is < 1 or > 65535)
            throw new ArgumentException($"AMQP port must be 1–65535, got: {Port}");

        if (Credit < 0)
            throw new ArgumentException($"AMQP credit (prefetch) must be >= 0, got: {Credit}");

        if (ConcurrentConsumers < 1)
            throw new ArgumentException($"AMQP concurrentConsumers must be >= 1, got: {ConcurrentConsumers}");

        if (SenderSettleMode is < 0 or > 2)
            throw new ArgumentException($"SenderSettleMode must be 0–2, got: {SenderSettleMode}");

        if (ReceiverSettleMode is < 0 or > 1)
            throw new ArgumentException($"ReceiverSettleMode must be 0–1, got: {ReceiverSettleMode}");

        if (Durable > 2)
            throw new ArgumentException($"Durable must be 0–2, got: {Durable}");
    }

    /// <summary>
    /// Resolves the terminus capabilities as an array of AMQP symbols.
    /// Combines explicit Capabilities with RoutingType for Artemis compatibility.
    /// </summary>
    internal string[] ResolveCapabilities()
    {
        var caps = new List<string>();

        if (!string.IsNullOrEmpty(Capabilities))
        {
            caps.AddRange(Capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Artemis routing type as capability
        if (!string.IsNullOrEmpty(RoutingType))
        {
            var rt = RoutingType.ToUpperInvariant() switch
            {
                "ANYCAST" => "queue",
                "MULTICAST" => "topic",
                _ => RoutingType.ToLowerInvariant()
            };

            if (!caps.Contains(rt, StringComparer.OrdinalIgnoreCase))
                caps.Add(rt);
        }

        return caps.ToArray();
    }

    /// <summary>
    /// Maps the ExpiryPolicy string to the AMQP symbol value.
    /// </summary>
    internal AmqpSymbol ResolveExpiryPolicy()
    {
        return ExpiryPolicy.ToLowerInvariant() switch
        {
            "link-detach" => new AmqpSymbol("link-detach"),
            "session-end" => new AmqpSymbol("session-end"),
            "connection-close" => new AmqpSymbol("connection-close"),
            "never" => new AmqpSymbol("never"),
            _ => new AmqpSymbol("session-end")
        };
    }
}
