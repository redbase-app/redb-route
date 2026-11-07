using MQTTnet;

namespace redb.Route.MqttNet.Connection;

/// <summary>
/// Creates and manages <see cref="IMqttClient"/> instances.
/// </summary>
public interface IMqttClientFactory
{
    /// <summary>Creates a connected MQTT client from broker options.</summary>
    Task<IMqttClient> CreateConnectedClientAsync(
        MqttBrokerOptions options,
        string? clientIdOverride = null,
        CancellationToken ct = default);
}

/// <summary>
/// Default factory using MQTTnet's <see cref="MqttClientFactory"/>.
/// </summary>
internal sealed class DefaultMqttClientFactory : IMqttClientFactory
{
    /// <inheritdoc/>
    public async Task<IMqttClient> CreateConnectedClientAsync(
        MqttBrokerOptions options,
        string? clientIdOverride = null,
        CancellationToken ct = default)
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();

        var clientId = clientIdOverride
            ?? options.ClientId
            ?? $"redb-route-{Guid.NewGuid():N}";

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(options.Server, options.Port)
            .WithClientId(clientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(options.KeepAlive))
            .WithCleanSession(options.CleanSession);

        if (options.Username != null)
            builder.WithCredentials(options.Username, options.Password);

        if (options.UseTls)
            builder.WithTlsOptions(o => { });

        var mqttOptions = builder.Build();
        await client.ConnectAsync(mqttOptions, ct).ConfigureAwait(false);
        return client;
    }
}
