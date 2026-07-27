using redb.Route.Core;

namespace redb.Route.MqttNet;

/// <summary>
/// Endpoint options for the MQTT component. Bound from URI parameters via reflection.
/// </summary>
public sealed class MqttEndpointOptions : EndpointOptions
{
    /// <summary>Subscribe or Publish mode.</summary>
    public MqttMode Mode { get; set; } = MqttMode.Subscribe;

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Named broker reference (registered via <c>AddBroker()</c>).</summary>
    public string? Broker { get; set; }

    /// <summary>Inline broker URL (e.g. <c>tcp://localhost:1883</c>). Alternative to named Broker.</summary>
    public string? Server { get; set; }

    /// <summary>Server port. Defaults to 1883 (or 8883 for TLS).</summary>
    public int Port { get; set; }

    /// <summary>Username for broker authentication.</summary>
    public string? Username { get; set; }

    /// <summary>Password for broker authentication.</summary>
    [Sensitive]
    public string? Password { get; set; }

    /// <summary>Client ID. Auto-generated if not set.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Named <see cref="MqttConnectionFactory"/> from the route registry. Lets broker credentials
    /// live in the registry instead of the endpoint URI, so they never reach logs or dashboards.
    /// </summary>
    public string? ConnectionFactory { get; set; }

    /// <summary>Enable TLS/SSL.</summary>
    public bool UseTls { get; set; }

    /// <summary>Keep-alive interval in seconds. Default 60.</summary>
    public int KeepAlive { get; set; } = 60;

    /// <summary>Clean session / clean start. Default true.</summary>
    public bool CleanSession { get; set; } = true;

    // ── Subscribe (consumer) ──────────────────────────────────────────

    /// <summary>Quality of Service level (0, 1, 2). Default 0.</summary>
    public int Qos { get; set; }

    /// <summary>QoS level as expression (e.g. <c>${header['qos']}</c>). Resolved at runtime, overrides <see cref="Qos"/> if set.</summary>
    public string? QosExpression { get; set; }

    /// <summary>MQTT 5.0 shared subscription group name. Enables load-balanced consuming.</summary>
    public string? SharedSubscription { get; set; }

    /// <summary>
    /// Number of concurrent processing workers for received messages (default 1 = serial).
    /// When &gt; 1, received messages are dispatched to a bounded pool of N workers, so up to N
    /// messages are processed in parallel. Each message is acknowledged only AFTER its worker
    /// finishes processing (manual ack), preserving at-least-once delivery for QoS 1/2 — unlike a
    /// generic <c>.Threads(N)</c> in the route, which would acknowledge on hand-off (before processing).
    /// Ordering is not preserved when &gt; 1.
    /// </summary>
    public int ConcurrentConsumers { get; set; } = 1;

    // ── Publish (producer) ────────────────────────────────────────────

    /// <summary>Retain message on broker. Default false.</summary>
    public bool Retain { get; set; }

    /// <summary>Message expiry interval in seconds (MQTT 5.0). 0 = no expiry.</summary>
    public int MessageExpiryInterval { get; set; }

    /// <summary>Message expiry as expression (e.g. <c>${body['ttl']}</c>). Resolved at runtime, overrides <see cref="MessageExpiryInterval"/> if set.</summary>
    public string? MessageExpiryIntervalExpression { get; set; }

    /// <summary>Content type for published messages (MQTT 5.0).</summary>
    public string? ContentType { get; set; }

    /// <summary>Response topic for request/response pattern (MQTT 5.0).</summary>
    public string? ResponseTopic { get; set; }

    /// <inheritdoc/>
    public override void Validate()
    {
        if (Broker is null && Server is null)
            throw new ArgumentException("Either Broker or Server is required for MQTT endpoint.");

        if (Qos < 0 || Qos > 2)
            throw new ArgumentException($"QoS must be 0, 1, or 2. Got: {Qos}");

        if (ConcurrentConsumers < 1)
            throw new ArgumentException($"ConcurrentConsumers must be at least 1. Got: {ConcurrentConsumers}");
    }
}
