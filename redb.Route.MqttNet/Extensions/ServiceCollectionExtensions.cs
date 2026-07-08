using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.MqttNet.Connection;

namespace redb.Route.MqttNet;

/// <summary>
/// DI registration for the MQTT connector.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the MQTT component with the route framework.
    /// <example>
    /// <code>
    /// services.AddRedbRoute();
    /// services.AddRedbRouteMqtt(mqtt =>
    /// {
    ///     mqtt.AddBroker("main", o =>
    ///     {
    ///         o.Server = "localhost";
    ///         o.Port = 1883;
    ///     });
    /// });
    /// </code>
    /// </example>
    /// </summary>
    public static IServiceCollection AddRedbRouteMqtt(
        this IServiceCollection services,
        Action<MqttConfigurationBuilder>? configure = null)
    {
        var builder = new MqttConfigurationBuilder(services);
        configure?.Invoke(builder);

        services.AddSingleton<IMqttBrokerRegistry>(builder.BuildRegistry());
        services.AddSingleton<IMqttClientFactory>(builder.BuildClientFactory());
        services.AddSingleton<MqttComponent>();

        services.AddSingleton<IMqttComponentRegistrar>(sp =>
        {
            var context = sp.GetRequiredService<IRouteContext>();
            var component = sp.GetRequiredService<MqttComponent>();
            component.BrokerRegistry = sp.GetRequiredService<IMqttBrokerRegistry>();
            component.ClientFactory = sp.GetRequiredService<IMqttClientFactory>();
            context.AddComponent(component);
            return new MqttComponentRegistrar();
        });

        return services;
    }
}

internal interface IMqttComponentRegistrar;
internal sealed class MqttComponentRegistrar : IMqttComponentRegistrar;

/// <summary>
/// Fluent builder for MQTT DI configuration.
/// </summary>
public sealed class MqttConfigurationBuilder
{
    private readonly IServiceCollection _services;
    private readonly MqttBrokerRegistry _brokerRegistry = new();
    private IMqttClientFactory? _clientFactory;

    internal MqttConfigurationBuilder(IServiceCollection services) => _services = services;

    /// <summary>The service collection.</summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Registers a named broker for use with <c>Mqtt.Subscribe("topic").Broker("name")</c>.
    /// </summary>
    public MqttConfigurationBuilder AddBroker(string name, Action<MqttBrokerOptions> configure)
    {
        var options = new MqttBrokerOptions();
        configure(options);
        _brokerRegistry.Register(name, options);
        return this;
    }

    /// <summary>
    /// Registers a named broker with pre-built options.
    /// </summary>
    public MqttConfigurationBuilder AddBroker(string name, MqttBrokerOptions options)
    {
        _brokerRegistry.Register(name, options);
        return this;
    }

    /// <summary>
    /// Replaces the default client factory (for testing or custom client management).
    /// </summary>
    public MqttConfigurationBuilder UseClientFactory(IMqttClientFactory factory)
    {
        _clientFactory = factory;
        return this;
    }

    internal IMqttBrokerRegistry BuildRegistry() => _brokerRegistry;
    internal IMqttClientFactory BuildClientFactory() => _clientFactory ?? new DefaultMqttClientFactory();
}
