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

    /// <summary>Topic the message was received on (consumer) or sent to (producer).</summary>
    public const string Topic = "redbMqtt.topic";

    /// <summary>QoS level of the received or published message.</summary>
    public const string Qos = "redbMqtt.qos";

    /// <summary>Whether the message had the retain flag set.</summary>
    public const string Retain = "redbMqtt.retain";

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
