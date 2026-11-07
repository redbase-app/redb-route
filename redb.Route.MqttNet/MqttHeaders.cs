namespace redb.Route.MqttNet;

/// <summary>
/// Constants for exchange headers set by the MQTT component.
/// All prefixed with "redbMqtt." to avoid collisions with other connectors.
/// </summary>
public static class MqttHeaders
{
    /// <summary>Common prefix for all MQTT headers.</summary>
    public const string Prefix = "redbMqtt.";

    // ── Topic ─────────────────────────────────────────────────────────

    /// <summary>
    /// Topic the message was received on (consumer) or sent to (producer). Informational: the connector writes it, a
    /// publish never reads it. To publish elsewhere than the endpoint's topic, set <see cref="OverrideTopic"/>.
    /// </summary>
    public const string Topic = "redbMqtt.topic";

    /// <summary>
    /// Topic a publish goes to instead of the endpoint's topic, set by the route. The connector never writes it, so a
    /// received topic or an earlier publish cannot steer the next one (Apache Camel keeps CamelMqttTopic and
    /// CamelPahoOverrideTopic apart the same way).
    /// </summary>
    public const string OverrideTopic = "redbMqtt.overrideTopic";

    /// <summary>
    /// QoS level of the received or published message. Informational: the connector writes it, a publish never reads
    /// it. To publish with another QoS than the endpoint's, set <see cref="OverrideQos"/>.
    /// </summary>
    public const string Qos = "redbMqtt.qos";

    /// <summary>
    /// QoS level (an <see cref="int"/>, 0 to 2) a publish uses instead of the endpoint's, set by the route. The connector
    /// never writes it, so the QoS of a received message does not carry over to a republish.
    /// </summary>
    public const string OverrideQos = "redbMqtt.overrideQos";

    /// <summary>
    /// Whether the received or published message had the retain flag set. Informational: the connector writes it, a
    /// publish never reads it. To set the retain flag of a publish, set <see cref="OverrideRetain"/>.
    /// </summary>
    public const string Retain = "redbMqtt.retain";

    /// <summary>
    /// Retain flag (a <see cref="bool"/>) a publish uses instead of the endpoint's, set by the route. The connector never
    /// writes it, so a retained message received and republished is not retained again unless the route asks for it.
    /// </summary>
    public const string OverrideRetain = "redbMqtt.overrideRetain";

    // ── Payload ───────────────────────────────────────────────────────

    /// <summary>MQTT payload content type (MQTT 5.0).</summary>
    public const string ContentType = "redbMqtt.contentType";

    /// <summary>Response topic (MQTT 5.0 request/response).</summary>
    public const string ResponseTopic = "redbMqtt.responseTopic";

    /// <summary>Correlation data (MQTT 5.0 request/response).</summary>
    public const string CorrelationData = "redbMqtt.correlationData";

    /// <summary>Message expiry interval in seconds (MQTT 5.0).</summary>
    public const string MessageExpiryInterval = "redbMqtt.messageExpiryInterval";

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Broker name used for the connection.</summary>
    public const string Broker = "redbMqtt.broker";

    /// <summary>Client ID of the MQTT connection.</summary>
    public const string ClientId = "redbMqtt.clientId";

    // ── MQTT 5.0 User Properties ──────────────────────────────────────

    /// <summary>MQTT 5.0 User Properties as Dictionary&lt;string,string&gt;.</summary>
    public const string UserProperties = "redbMqtt.userProperties";
}
