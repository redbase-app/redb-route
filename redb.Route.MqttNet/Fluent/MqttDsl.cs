using System.Text;
using System.Web;
using redb.Route.Abstractions;

namespace redb.Route.MqttNet;

/// <summary>
/// Entry point for the MQTT Fluent API. Two methods: Subscribe and Publish.
/// Each returns an <see cref="MqttBuilder"/> for fluent configuration.
/// <para>
/// The builder generates a URI string equivalent to writing the URI manually.
/// Both paths converge in <see cref="MqttEndpointOptions"/> via URI binding.
/// </para>
/// <example>
/// <code>
/// // Subscribe (consumer):
/// .From(Mqtt.Subscribe("sensors/temperature")
///     .Broker("main").Qos(1).SharedSubscription("group1"))
///
/// // Publish (producer):
/// .To(Mqtt.Publish("sensors/commands")
///     .Broker("main").Qos(1).Retain())
///
/// // URI equivalent:
/// .From("mqtt:sensors/temperature?mode=Subscribe&amp;broker=main&amp;qos=1&amp;sharedSubscription=group1")
/// </code>
/// </example>
/// </summary>
public static class Mqtt
{
    /// <summary>Creates a subscription consumer for the given topic or topic filter (wildcards supported).</summary>
    /// <param name="topic">MQTT topic or topic filter (e.g. <c>sensors/+/temperature</c>, <c>home/#</c>).</param>
    public static MqttBuilder Subscribe(string topic) => new(MqttMode.Subscribe, topic);

    /// <summary>Creates a publisher producer for the given topic.</summary>
    /// <param name="topic">MQTT topic to publish to.</param>
    public static MqttBuilder Publish(string topic) => new(MqttMode.Publish, topic);
}

/// <summary>
/// Fluent builder for MQTT endpoint URIs. Thin sugar over <see cref="MqttEndpointOptions"/>.
/// Call <see cref="Build"/> or use implicit conversion to <see cref="string"/>
/// to get the URI for <c>From()</c> / <c>To()</c>.
/// </summary>
public sealed class MqttBuilder
{
    private readonly MqttMode _mode;
    private readonly string _topic;

    // Connection
    private string? _broker;
    private string? _server;
    private string? _port;
    private string? _username;
    private string? _password;
    private string? _clientId;
    private bool _useTls;
    private string? _keepAlive;
    private bool? _cleanSession;

    // Subscribe
    private string? _qos;
    private string? _sharedSubscription;

    // Publish
    private bool _retain;
    private string? _messageExpiryInterval;
    private string? _contentType;
    private string? _responseTopic;

    /// <summary>Creates a builder for the given mode and topic.</summary>
    internal MqttBuilder(MqttMode mode, string topic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        _mode = mode;
        _topic = topic;
    }

    // ── Connection ────────────────────────────────────────────────────

    /// <summary>Use a named broker registered via <c>AddBroker()</c>.</summary>
    public MqttBuilder Broker(IExpression name) { _broker = name.ToTemplateString(); return this; }

    /// <summary>Connect directly to this server (hostname or IP).</summary>
    public MqttBuilder Server(IExpression server) { _server = server.ToTemplateString(); return this; }

    /// <summary>Server port. Default 1883.</summary>
    public MqttBuilder Port(int port) { _port = port.ToString(); return this; }
    /// <summary>Server port from an expression.</summary>
    public MqttBuilder Port(IExpression port) { _port = port.ToTemplateString(); return this; }

    /// <summary>Username for broker authentication.</summary>
    public MqttBuilder Username(IExpression username) { _username = username.ToTemplateString(); return this; }

    /// <summary>Password for broker authentication.</summary>
    public MqttBuilder Password(IExpression password) { _password = password.ToTemplateString(); return this; }

    /// <summary>Client ID for the MQTT connection.</summary>
    public MqttBuilder ClientId(IExpression clientId) { _clientId = clientId.ToTemplateString(); return this; }

    /// <summary>Enable TLS/SSL.</summary>
    public MqttBuilder UseTls() { _useTls = true; return this; }

    /// <summary>Keep-alive interval in seconds.</summary>
    public MqttBuilder KeepAlive(int seconds) { _keepAlive = seconds.ToString(); return this; }
    /// <summary>Keep-alive interval from an expression.</summary>
    public MqttBuilder KeepAlive(IExpression seconds) { _keepAlive = seconds.ToTemplateString(); return this; }

    /// <summary>Set clean session flag.</summary>
    public MqttBuilder CleanSession(bool clean = true) { _cleanSession = clean; return this; }

    // ── QoS ──────────────────────────────────────────────────────────

    /// <summary>Quality of Service level: 0 (at most once), 1 (at least once), 2 (exactly once).</summary>
    public MqttBuilder Qos(int level) { _qos = level.ToString(); return this; }
    /// <summary>QoS from an expression.</summary>
    public MqttBuilder Qos(IExpression level) { _qos = level.ToTemplateString(); return this; }

    // ── Subscribe ────────────────────────────────────────────────────

    /// <summary>MQTT 5.0 shared subscription group for load-balanced consuming.</summary>
    public MqttBuilder SharedSubscription(IExpression group) { _sharedSubscription = group.ToTemplateString(); return this; }

    // ── Publish ──────────────────────────────────────────────────────

    /// <summary>Set retain flag on published messages.</summary>
    public MqttBuilder Retain() { _retain = true; return this; }

    /// <summary>Message expiry interval in seconds (MQTT 5.0).</summary>
    public MqttBuilder MessageExpiryInterval(int seconds) { _messageExpiryInterval = seconds.ToString(); return this; }
    /// <summary>Message expiry from an expression.</summary>
    public MqttBuilder MessageExpiryInterval(IExpression seconds) { _messageExpiryInterval = seconds.ToTemplateString(); return this; }

    /// <summary>Content type for published messages (MQTT 5.0).</summary>
    public MqttBuilder ContentType(string contentType) { _contentType = contentType; return this; }
    /// <summary>Content type from an expression.</summary>
    public MqttBuilder ContentType(IExpression contentType) { _contentType = contentType.ToTemplateString(); return this; }

    /// <summary>Response topic for request/response pattern (MQTT 5.0).</summary>
    public MqttBuilder ResponseTopic(IExpression topic) { _responseTopic = topic.ToTemplateString(); return this; }

    // ── Build ─────────────────────────────────────────────────────────

    /// <summary>Builds the MQTT URI string.</summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.Append("mqtt:");
        sb.Append(_topic);

        var separator = '?';

        void Append(string key, string value)
        {
            sb.Append(separator);
            sb.Append(key);
            sb.Append('=');
            sb.Append(HttpUtility.UrlEncode(value));
            separator = '&';
        }

        void AppendIf(string key, string? value) { if (value != null) Append(key, value); }
        void AppendBool(string key, bool value) { if (value) Append(key, "true"); }

        // Route int-or-expression params to the correct property
        void AppendIntOrExpression(string key, string? value)
        {
            if (value == null) return;
            if (value.Contains("${"))
                Append(key + "Expression", value);
            else
                Append(key, value);
        }

        Append("mode", _mode.ToString());

        // Connection
        AppendIf("broker", _broker);
        AppendIf("server", _server);
        AppendIf("port", _port);
        AppendIf("username", _username);
        AppendIf("password", _password);
        AppendIf("clientId", _clientId);
        AppendBool("useTls", _useTls);
        AppendIf("keepAlive", _keepAlive);
        if (_cleanSession.HasValue)
            Append("cleanSession", _cleanSession.Value.ToString().ToLowerInvariant());

        // Subscribe/Publish common
        AppendIntOrExpression("qos", _qos);

        // Subscribe-only
        AppendIf("sharedSubscription", _sharedSubscription);

        // Publish-only
        AppendBool("retain", _retain);
        AppendIntOrExpression("messageExpiryInterval", _messageExpiryInterval);
        AppendIf("contentType", _contentType);
        AppendIf("responseTopic", _responseTopic);

        return sb.ToString();
    }

    /// <summary>Implicit conversion to URI string.</summary>
    public static implicit operator string(MqttBuilder builder) => builder.Build();

    /// <inheritdoc/>
    public override string ToString() => Build();
}
