namespace redb.Route.MqttNet.Connection;

/// <summary>
/// Registry for named MQTT broker connections.
/// </summary>
public interface IMqttBrokerRegistry
{
    /// <summary>Gets broker options by name.</summary>
    MqttBrokerOptions GetOptions(string name);

    /// <summary>Checks if a named broker is registered.</summary>
    bool Contains(string name);
}

/// <summary>
/// In-memory broker registry. Populated at startup via <see cref="MqttConfigurationBuilder.AddBroker"/>.
/// </summary>
internal sealed class MqttBrokerRegistry : IMqttBrokerRegistry
{
    private readonly Dictionary<string, MqttBrokerOptions> _brokers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a broker with the given name.</summary>
    internal void Register(string name, MqttBrokerOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(options);
        _brokers[name] = options;
    }

    /// <inheritdoc/>
    public MqttBrokerOptions GetOptions(string name)
    {
        if (_brokers.TryGetValue(name, out var options))
            return options;

        throw new InvalidOperationException(
            $"MQTT broker '{name}' is not registered. Call AddBroker(\"{name}\", ...) in AddRedbRouteMqtt().");
    }

    /// <inheritdoc/>
    public bool Contains(string name) => _brokers.ContainsKey(name);
}
