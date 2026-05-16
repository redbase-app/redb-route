using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet.Connection;

namespace redb.Route.MqttNet;

/// <summary>
/// MQTT component registered with scheme "mqtt". Creates endpoints from URIs.
/// </summary>
public class MqttComponent : ComponentBase
{
    /// <inheritdoc/>
    public override string Scheme => "mqtt";

    /// <summary>The broker registry for named broker lookups.</summary>
    internal IMqttBrokerRegistry? BrokerRegistry { get; set; }

    /// <summary>The client factory for creating MQTT connections.</summary>
    internal IMqttClientFactory? ClientFactory { get; set; }

    /// <inheritdoc/>
    public override IEndpoint CreateEndpoint(EndpointUri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var options = new MqttEndpointOptions();
        options.BindFromUri(uri.RawParameters);
        options.Validate();

        return new MqttEndpoint(uri, this, options);
    }
}

/// <summary>
/// MQTT endpoint. Creates consumer (subscriber) or producer (publisher) based on mode.
/// </summary>
public class MqttEndpoint : EndpointBase<MqttEndpointOptions>
{
    /// <summary>Creates a new MQTT endpoint.</summary>
    public MqttEndpoint(EndpointUri uri, MqttComponent component, MqttEndpointOptions options)
        : base(uri, component, options) { }

    /// <summary>The owning MQTT component.</summary>
    internal MqttComponent MqttComponent => (MqttComponent)Component;

    /// <summary>Endpoint options.</summary>
    internal MqttEndpointOptions EndpointOptions => Options;

    /// <summary>The topic from the URI path.</summary>
    public string Topic => Uri.Path;

    /// <inheritdoc/>
    public override IProducer CreateProducer()
    {
        return new MqttProducer(this, Options);
    }

    /// <inheritdoc/>
    public override IConsumer CreateConsumer(IProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);

        if (Options.Mode != MqttMode.Subscribe)
            throw new InvalidOperationException(
                $"MQTT consumer requires Mode=Subscribe, but Mode={Options.Mode}.");

        return new MqttConsumer(this, processor, Options);
    }
}
