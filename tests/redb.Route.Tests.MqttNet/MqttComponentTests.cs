using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;

namespace redb.Route.Tests.MqttNet;

public class MqttComponentTests
{
    [Fact]
    public void Scheme_ReturnsMqtt()
    {
        var component = new MqttComponent();
        component.Scheme.Should().Be("mqtt");
    }

    [Fact]
    public void CreateEndpoint_ValidUri_ReturnsMqttEndpoint()
    {
        var component = new MqttComponent();
        var uri = new EndpointUri("mqtt", "sensors/temp",
            "mqtt:sensors/temp?mode=Subscribe&broker=main",
            new Dictionary<string, string> { ["mode"] = "Subscribe", ["broker"] = "main" });

        var endpoint = component.CreateEndpoint(uri);

        endpoint.Should().BeOfType<MqttEndpoint>();
        endpoint.Uri.Should().BeSameAs(uri);
        endpoint.Component.Should().BeSameAs(component);
    }

    [Fact]
    public void CreateEndpoint_NullUri_Throws()
    {
        var component = new MqttComponent();
        var act = () => component.CreateEndpoint(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CreateEndpoint_BindsOptionsCorrectly()
    {
        var component = new MqttComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Subscribe",
            ["broker"] = "main",
            ["qos"] = "1",
            ["sharedSubscription"] = "group1",
            ["cleanSession"] = "true",
            ["keepAlive"] = "30"
        };
        var uri = new EndpointUri("mqtt", "sensors/+", "mqtt:sensors/+?...", parameters);

        var endpoint = (MqttEndpoint)component.CreateEndpoint(uri);

        endpoint.EndpointOptions.Mode.Should().Be(MqttMode.Subscribe);
        endpoint.EndpointOptions.Broker.Should().Be("main");
        endpoint.EndpointOptions.Qos.Should().Be(1);
        endpoint.EndpointOptions.SharedSubscription.Should().Be("group1");
        endpoint.EndpointOptions.CleanSession.Should().BeTrue();
        endpoint.EndpointOptions.KeepAlive.Should().Be(30);
    }

    [Fact]
    public void CreateEndpoint_NoBrokerNorServer_Throws()
    {
        var component = new MqttComponent();
        var uri = new EndpointUri("mqtt", "topic", "mqtt:topic",
            new Dictionary<string, string> { ["mode"] = "Subscribe" });

        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*Broker*Server*");
    }

    [Fact]
    public void CreateEndpoint_InvalidQos_Throws()
    {
        var component = new MqttComponent();
        var uri = new EndpointUri("mqtt", "topic", "mqtt:topic",
            new Dictionary<string, string> { ["broker"] = "main", ["qos"] = "5" });

        var act = () => component.CreateEndpoint(uri);
        act.Should().Throw<ArgumentException>().WithMessage("*QoS*");
    }
}

public class MqttEndpointTests
{
    private static MqttEndpoint CreateEndpoint(
        MqttMode mode = MqttMode.Subscribe,
        string topic = "test/topic",
        string? broker = "main",
        string? server = null)
    {
        var component = new MqttComponent();
        var parameters = new Dictionary<string, string> { ["mode"] = mode.ToString() };
        if (broker != null) parameters["broker"] = broker;
        if (server != null) parameters["server"] = server;

        var uri = new EndpointUri("mqtt", topic, $"mqtt:{topic}", parameters);
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public void Topic_ReturnsUriPath()
    {
        var endpoint = CreateEndpoint(topic: "sensors/temperature");
        endpoint.Topic.Should().Be("sensors/temperature");
    }

    [Fact]
    public void CreateProducer_ReturnsProducer()
    {
        var endpoint = CreateEndpoint(MqttMode.Publish);
        var producer = endpoint.CreateProducer();
        producer.Should().NotBeNull();
    }

    [Fact]
    public void CreateConsumer_SubscribeMode_ReturnsConsumer()
    {
        var endpoint = CreateEndpoint(MqttMode.Subscribe);
        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);
        consumer.Should().NotBeNull();
    }

    [Fact]
    public void CreateConsumer_PublishMode_Throws()
    {
        var endpoint = CreateEndpoint(MqttMode.Publish);
        var processor = Substitute.For<IProcessor>();

        var act = () => endpoint.CreateConsumer(processor);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mode=Publish*");
    }

    [Fact]
    public void CreateConsumer_NullProcessor_Throws()
    {
        var endpoint = CreateEndpoint(MqttMode.Subscribe);

        var act = () => endpoint.CreateConsumer(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void MqttComponent_ReturnsCastComponent()
    {
        var endpoint = CreateEndpoint();
        endpoint.MqttComponent.Should().BeOfType<MqttComponent>();
    }

    [Fact]
    public void EndpointOptions_ReturnsOptions()
    {
        var endpoint = CreateEndpoint();
        endpoint.EndpointOptions.Should().NotBeNull();
        endpoint.EndpointOptions.Broker.Should().Be("main");
    }
}
