using redb.Route.Abstractions;

namespace redb.Route.MqttNet;

/// <summary>
/// Named connection factory for MQTT. Register it in the route registry and reference it by name
/// from the endpoint URI (<c>connectionFactory=my-broker</c>) so broker credentials never have to
/// appear in the URI, and therefore can never reach logs, telemetry, or the Tsak dashboard.
/// <para>
/// Example:
/// <code>
/// context.AddToRegistry("iot-broker", new MqttConnectionFactory
/// {
///     Server = "mqtt.corp.local",
///     Port = 8883,
///     UseTls = true,
///     Username = "svc-ingest",
///     Password = Environment.GetEnvironmentVariable("MQTT_PASSWORD")
/// });
///
/// // route carries no credentials at all:
/// r.From("mqtt://sensors/+/temp?connectionFactory=iot-broker")
/// </code>
/// </para>
/// </summary>
public sealed class MqttConnectionFactory
{
    /// <summary>Broker address in <c>host:port</c> form (alternative to <see cref="Server"/>).</summary>
    public string? Broker { get; set; }

    /// <summary>Broker hostname or IP.</summary>
    public string? Server { get; set; }

    /// <summary>Broker port (1883 plain, 8883 TLS).</summary>
    public int Port { get; set; }

    /// <summary>Username for broker authentication.</summary>
    public string? Username { get; set; }

    /// <summary>Password for broker authentication.</summary>
    public string? Password { get; set; }

    /// <summary>MQTT client identifier.</summary>
    public string? ClientId { get; set; }

    /// <summary>Connect over TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>Keep-alive interval in seconds (default 60).</summary>
    public int KeepAlive { get; set; } = 60;

    /// <summary>Start a clean session (default true).</summary>
    public bool CleanSession { get; set; } = true;

    /// <summary>
    /// Copies this factory's connection and credential settings onto the endpoint options, but only
    /// for parameters the endpoint URI did not set explicitly — an inline URI value always wins,
    /// so existing routes keep their behaviour.
    /// </summary>
    internal void ApplyTo(MqttEndpointOptions options, EndpointUri uri)
    {
        var supplied = uri.RawParameters;

        if (!supplied.ContainsKey(nameof(options.Broker))) options.Broker = Broker;
        if (!supplied.ContainsKey(nameof(options.Server))) options.Server = Server;
        if (!supplied.ContainsKey(nameof(options.Port))) options.Port = Port;
        if (!supplied.ContainsKey(nameof(options.Username))) options.Username = Username;
        if (!supplied.ContainsKey(nameof(options.Password))) options.Password = Password;
        if (!supplied.ContainsKey(nameof(options.ClientId))) options.ClientId = ClientId;
        if (!supplied.ContainsKey(nameof(options.UseTls))) options.UseTls = UseTls;
        if (!supplied.ContainsKey(nameof(options.KeepAlive))) options.KeepAlive = KeepAlive;
        if (!supplied.ContainsKey(nameof(options.CleanSession))) options.CleanSession = CleanSession;
    }
}
