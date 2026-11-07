namespace redb.Route.MqttNet.Connection;

/// <summary>
/// Options for connecting to an MQTT broker.
/// </summary>
public sealed class MqttBrokerOptions
{
    /// <summary>Broker server hostname or IP.</summary>
    public string Server { get; set; } = "localhost";

    /// <summary>Broker port. Default 1883.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Username for authentication.</summary>
    public string? Username { get; set; }

    /// <summary>Password for authentication.</summary>
    public string? Password { get; set; }

    /// <summary>Client ID prefix. Final ID may include a unique suffix.</summary>
    public string? ClientId { get; set; }

    /// <summary>Enable TLS.</summary>
    public bool UseTls { get; set; }

    /// <summary>Keep-alive interval in seconds.</summary>
    public int KeepAlive { get; set; } = 60;

    /// <summary>Clean session / clean start.</summary>
    public bool CleanSession { get; set; } = true;
}
