using redb.Route.Core;
using redb.Route.MqttNet;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// Broker credentials must be able to live in the registry instead of the endpoint URI,
/// so they never reach logs, telemetry, or the dashboard.
/// </summary>
public sealed class MqttConnectionFactoryTests
{
    private const string Secret = "brokerP4ssw0rd";

    private static MqttComponent Wire(string name, MqttConnectionFactory factory)
    {
        var context = new RouteContext();
        var component = new MqttComponent();
        context.AddComponent(component);
        context.AddToRegistry(name, factory);
        return component;
    }

    [Fact]
    public void Factory_SuppliesBrokerAndCredentials_WhenUriCarriesNone()
    {
        var component = Wire("iot-broker", new MqttConnectionFactory
        {
            Server = "mqtt.corp.local",
            Port = 8883,
            UseTls = true,
            Username = "svc-ingest",
            Password = Secret,
            ClientId = "ingest-1"
        });

        // Validate() requires a broker — this throws if the factory is not applied first.
        var uri = EndpointUriParser.Parse("mqtt://sensors/temp?connectionFactory=iot-broker");
        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().NotBeNull();
        uri.ToUriString().Should().NotContain(Secret);
        uri.ToString().Should().NotContain(Secret);
    }

    [Fact]
    public void ExplicitUriValue_WinsOverFactory()
    {
        var component = Wire("f", new MqttConnectionFactory
        {
            Server = "from-factory",
            Port = 8883,
            Username = "factory-user"
        });

        var uri = EndpointUriParser.Parse(
            "mqtt://sensors/temp?connectionFactory=f&server=from-uri&username=uri-user");
        component.CreateEndpoint(uri).Should().NotBeNull();

        uri.RawParameters["server"].Should().Be("from-uri");
        uri.RawParameters["username"].Should().Be("uri-user");
    }

    [Fact]
    public void MissingFactory_FallsBackToUriParameters()
    {
        var context = new RouteContext();
        var component = new MqttComponent();
        context.AddComponent(component);

        var uri = EndpointUriParser.Parse("mqtt://sensors/temp?connectionFactory=absent&server=direct");
        var act = () => component.CreateEndpoint(uri);

        act.Should().NotThrow();
    }

    [Fact]
    public void Dsl_EmitsConnectionFactory()
    {
        var uri = Mqtt.Subscribe("sensors/temp").ConnectionFactory("iot-broker").Build();

        uri.Should().Contain("connectionFactory=iot-broker");
        uri.Should().NotContain("password=");
    }
}
