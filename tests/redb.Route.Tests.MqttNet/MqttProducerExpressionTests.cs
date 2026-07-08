using MQTTnet;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// Tests for expression resolution in MQTT producer (Phase 1 bugfix).
/// Verifies header > expression > static priority for all properties.
/// </summary>
public class MqttProducerExpressionTests
{
    // ── Helpers ──────────────────────────────────────────────────────

    private static MqttEndpoint CreateEndpoint(
        Dictionary<string, string> parameters,
        string topic = "test/topic")
    {
        var component = new MqttComponent();
        parameters["mode"] = "Publish";
        if (!parameters.ContainsKey("broker")) parameters["broker"] = "main";

        var uri = new EndpointUri("mqtt", topic, $"mqtt:{topic}", parameters);
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    private static (MqttEndpoint endpoint, IMqttClient mockClient) CreateMocked(
        Dictionary<string, string> parameters,
        string topic = "test/topic")
    {
        var endpoint = CreateEndpoint(parameters, topic);

        var mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);
        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)));

        var mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));

        var mockRegistry = Substitute.For<IMqttBrokerRegistry>();
        mockRegistry.GetOptions("main").Returns(new MqttBrokerOptions { Server = "localhost" });

        endpoint.MqttComponent.ClientFactory = mockFactory;
        endpoint.MqttComponent.BrokerRegistry = mockRegistry;

        return (endpoint, mockClient);
    }

    private static async Task<MqttApplicationMessage> SendAndCapture(
        MqttEndpoint endpoint, IMqttClient mockClient, IExchange exchange)
    {
        MqttApplicationMessage? captured = null;
        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => captured = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();
        await ((IProducer)producer).Process(exchange);
        return captured!;
    }

    // ── Topic expression ─────────────────────────────────────────────

    [Fact]
    public async Task Topic_Expression_ResolvesFromHeader()
    {
        var (endpoint, client) = CreateMocked(new(), topic: "prefix/${header.device}");
        var msg = new Message("payload");
        msg.Headers["device"] = "sensor42";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.Topic.Should().Be("prefix/sensor42");
    }

    [Fact]
    public async Task Topic_HeaderOverride_WinsOverExpression()
    {
        var (endpoint, client) = CreateMocked(new(), topic: "prefix/${header.device}");
        var msg = new Message("payload");
        msg.Headers["device"] = "sensor42";
        msg.Headers[MqttHeaders.Topic] = "override/topic";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.Topic.Should().Be("override/topic");
    }

    // ── QoS expression ───────────────────────────────────────────────

    [Fact]
    public async Task Qos_Expression_ResolvesFromHeader()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["qosExpression"] = "${header.qosLevel}"
        });
        var msg = new Message("payload");
        msg.Headers["qosLevel"] = "2";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }

    [Fact]
    public async Task Qos_HeaderOverride_WinsOverExpression()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["qosExpression"] = "${header.qosLevel}"
        });
        var msg = new Message("payload");
        msg.Headers["qosLevel"] = "2";
        msg.Headers[MqttHeaders.Qos] = 1; // int header
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce);
    }

    [Fact]
    public async Task Qos_StaticFallback_WhenNoExpressionOrHeader()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["qos"] = "1"
        });
        var msg = new Message("payload");
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.AtLeastOnce);
    }

    // ── MessageExpiryInterval expression ─────────────────────────────

    [Fact]
    public async Task MessageExpiryInterval_Expression_ResolvesFromHeader()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["messageExpiryIntervalExpression"] = "${header.ttl}"
        });
        var msg = new Message("payload");
        msg.Headers["ttl"] = "300";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.MessageExpiryInterval.Should().Be(300);
    }

    [Fact]
    public async Task MessageExpiryInterval_HeaderOverride_WinsOverExpression()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["messageExpiryIntervalExpression"] = "${header.ttl}"
        });
        var msg = new Message("payload");
        msg.Headers["ttl"] = "300";
        msg.Headers[MqttHeaders.MessageExpiryInterval] = 60;
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.MessageExpiryInterval.Should().Be(60);
    }

    // ── ContentType expression ───────────────────────────────────────

    [Fact]
    public async Task ContentType_Expression_ResolvesFromHeader()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["contentType"] = "${header.ct}"
        });
        var msg = new Message("payload");
        msg.Headers["ct"] = "application/json";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task ContentType_HeaderOverride_WinsOverExpression()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["contentType"] = "${header.ct}"
        });
        var msg = new Message("payload");
        msg.Headers["ct"] = "application/json";
        msg.Headers[MqttHeaders.ContentType] = "text/xml";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.ContentType.Should().Be("text/xml");
    }

    // ── ResponseTopic expression ─────────────────────────────────────

    [Fact]
    public async Task ResponseTopic_Expression_ResolvesFromHeader()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["responseTopic"] = "reply/${header.device}"
        });
        var msg = new Message("payload");
        msg.Headers["device"] = "sensor42";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.ResponseTopic.Should().Be("reply/sensor42");
    }

    [Fact]
    public async Task ResponseTopic_HeaderOverride_WinsOverExpression()
    {
        var (endpoint, client) = CreateMocked(new()
        {
            ["responseTopic"] = "reply/${header.device}"
        });
        var msg = new Message("payload");
        msg.Headers["device"] = "sensor42";
        msg.Headers[MqttHeaders.ResponseTopic] = "override/reply";
        var exchange = new Exchange(msg);

        var published = await SendAndCapture(endpoint, client, exchange);
        published.ResponseTopic.Should().Be("override/reply");
    }

    // ── DSL Build expression routing ─────────────────────────────────

    [Fact]
    public void DslBuild_QosExpression_RoutesToExpressionParam()
    {
        var uri = Mqtt.Publish("test/topic").Qos(new HeaderExpression("qosLevel")).Build();
        uri.Should().Contain("qosExpression=");
        uri.Should().NotContain("&qos=");
    }

    [Fact]
    public void DslBuild_QosStatic_RoutesToNormalParam()
    {
        var uri = Mqtt.Publish("test/topic").Qos(1).Build();
        uri.Should().Contain("qos=1");
        uri.Should().NotContain("qosExpression");
    }

    [Fact]
    public void DslBuild_MessageExpiryExpression_RoutesToExpressionParam()
    {
        var uri = Mqtt.Publish("test/topic").MessageExpiryInterval(new HeaderExpression("ttl")).Build();
        uri.Should().Contain("messageExpiryIntervalExpression=");
        uri.Should().NotContain("&messageExpiryInterval=");
    }
}
