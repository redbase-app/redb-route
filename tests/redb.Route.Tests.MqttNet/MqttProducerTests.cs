using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

public class MqttProducerTests
{
    private static MqttEndpoint CreateEndpoint(
        string topic = "out/topic",
        string? broker = "main",
        string? server = null,
        int qos = 0,
        bool retain = false,
        int messageExpiryInterval = 0,
        string? contentType = null,
        string? responseTopic = null)
    {
        var component = new MqttComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Publish"
        };
        if (broker != null) parameters["broker"] = broker;
        if (server != null) parameters["server"] = server;
        if (qos > 0) parameters["qos"] = qos.ToString();
        if (retain) parameters["retain"] = "true";
        if (messageExpiryInterval > 0) parameters["messageExpiryInterval"] = messageExpiryInterval.ToString();
        if (contentType != null) parameters["contentType"] = contentType;
        if (responseTopic != null) parameters["responseTopic"] = responseTopic;

        var uri = new EndpointUri("mqtt", topic, $"mqtt:{topic}", parameters);
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    private static MqttEndpoint CreateEndpointWithMocks(
        out IMqttClient mockClient,
        out IMqttClientFactory mockFactory,
        out IMqttBrokerRegistry mockRegistry,
        string topic = "out/topic",
        int qos = 0,
        bool retain = false,
        int messageExpiryInterval = 0,
        string? contentType = null,
        string? responseTopic = null)
    {
        var endpoint = CreateEndpoint(topic, "main", qos: qos, retain: retain,
            messageExpiryInterval: messageExpiryInterval, contentType: contentType,
            responseTopic: responseTopic);

        mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);
        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)));

        mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));

        mockRegistry = Substitute.For<IMqttBrokerRegistry>();
        mockRegistry.GetOptions("main").Returns(new MqttBrokerOptions { Server = "localhost" });

        endpoint.MqttComponent.ClientFactory = mockFactory;
        endpoint.MqttComponent.BrokerRegistry = mockRegistry;

        return endpoint;
    }

    // ── Start / Stop lifecycle ──────────────────────────────────────

    [Fact]
    public async Task Start_ConnectsClient()
    {
        var endpoint = CreateEndpointWithMocks(
            out _, out var mockFactory, out _);

        var producer = endpoint.CreateProducer();
        await producer.Start();

        await mockFactory.Received(1).CreateConnectedClientAsync(
            Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_DisconnectsClient()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        var producer = endpoint.CreateProducer();
        await producer.Start();
        await producer.Stop();

        await mockClient.Received(1).DisconnectAsync(
            Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }

    // ── Process / Publish ───────────────────────────────────────────

    [Fact]
    public async Task Process_NotStarted_Throws()
    {
        var endpoint = CreateEndpointWithMocks(
            out _, out _, out _);

        var producer = endpoint.CreateProducer();
        var exchange = new Exchange(new Message("data"));

        var act = async () => await ((IProducer)producer).Process(exchange);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not connected*");
    }

    [Fact]
    public async Task Process_PublishesMessage()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello"));
        await ((IProducer)producer).Process(exchange);

        await mockClient.Received(1).PublishAsync(
            Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_TopicFromEndpoint()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            topic: "commands/device1");

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("payload"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Topic.Should().Be("commands/device1");
    }

    [Fact]
    public async Task Process_TopicOverrideFromHeader()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            topic: "default/topic");

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers[MqttHeaders.Topic] = "override/topic";
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Topic.Should().Be("override/topic");
    }

    [Fact]
    public async Task Process_QosFromOptions()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            qos: 2);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }

    [Fact]
    public async Task Process_QosOverrideFromHeader()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            qos: 0);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers[MqttHeaders.Qos] = 2;
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.QualityOfServiceLevel.Should().Be(MqttQualityOfServiceLevel.ExactlyOnce);
    }

    [Fact]
    public async Task Process_RetainFromOptions()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            retain: true);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Retain.Should().BeTrue();
    }

    [Fact]
    public async Task Process_RetainOverrideFromHeader()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            retain: false);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers[MqttHeaders.Retain] = true;
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Retain.Should().BeTrue();
    }

    // ── SerializeBody ───────────────────────────────────────────────

    [Fact]
    public async Task Process_StringBody_SerializedToUtf8()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello World"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        Encoding.UTF8.GetString(capturedMessage!.Payload).Should().Be("Hello World");
    }

    [Fact]
    public async Task Process_ByteArrayBody_SentDirectly()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var data = new byte[] { 0x01, 0x02, 0x03 };
        var exchange = new Exchange(new Message(data));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        Encoding.UTF8.GetString(capturedMessage!.Payload).Should().Be(Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task Process_NullBody_EmptyPayload()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(null));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.Payload.Length.Should().Be(0);
    }

    [Fact]
    public async Task Process_ObjectBody_UseToString()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message(42));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        Encoding.UTF8.GetString(capturedMessage!.Payload).Should().Be("42");
    }

    // ── MQTT 5.0 properties ────────────────────────────────────────

    [Fact]
    public async Task Process_ContentType_SetOnMessage()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            contentType: "application/json");

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("{}"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task Process_ResponseTopic_SetOnMessage()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            responseTopic: "responses/dev1");

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.ResponseTopic.Should().Be("responses/dev1");
    }

    [Fact]
    public async Task Process_MessageExpiryInterval_SetOnMessage()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            messageExpiryInterval: 600);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.MessageExpiryInterval.Should().Be(600);
    }

    // ── Result headers ──────────────────────────────────────────────

    [Fact]
    public async Task Process_SetsResultHeaders()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            topic: "result/topic");

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)));

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        await ((IProducer)producer).Process(exchange);

        exchange.In.Headers[MqttHeaders.Topic].Should().Be("result/topic");
        exchange.In.Headers[MqttHeaders.Qos].Should().Be(0);
        exchange.In.Headers[MqttHeaders.Broker].Should().Be("main");
    }

    // ── Broker resolution ───────────────────────────────────────────

    [Fact]
    public async Task Start_NamedBroker_ResolvesFromRegistry()
    {
        var endpoint = CreateEndpointWithMocks(
            out _, out _, out var mockRegistry);

        var producer = endpoint.CreateProducer();
        await producer.Start();

        mockRegistry.Received(1).GetOptions("main");
    }

    [Fact]
    public async Task Start_InlineServer_BuildsBrokerOptions()
    {
        var endpoint = CreateEndpoint(
            topic: "t", broker: null, server: "mqtt.example.com");

        var mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);
        var mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));

        endpoint.MqttComponent.ClientFactory = mockFactory;

        var producer = endpoint.CreateProducer();
        await producer.Start();

        await mockFactory.Received(1).CreateConnectedClientAsync(
            Arg.Is<MqttBrokerOptions>(o => o.Server == "mqtt.example.com"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Start_NoBrokerNoServer_Throws()
    {
        var component = new MqttComponent();
        var parameters = new Dictionary<string, string>
        {
            ["mode"] = "Publish",
            ["broker"] = "main"
        };
        var uri = new EndpointUri("mqtt", "t", "mqtt:t", parameters);
        var endpoint = (MqttEndpoint)component.CreateEndpoint(uri);
        // No registry set

        var producer = endpoint.CreateProducer();
        var act = async () => await producer.Start();

        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IMqttBrokerRegistry*");
    }

    // ── UserProperties ──────────────────────────────────────────────

    [Fact]
    public async Task Process_UserProperties_ForwardedToMessage()
    {
        MqttApplicationMessage? capturedMessage = null;
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        mockClient.PublishAsync(Arg.Any<MqttApplicationMessage>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new MqttClientPublishResult(0, MqttClientPublishReasonCode.Success, "", null)))
            .AndDoes(x => capturedMessage = x.Arg<MqttApplicationMessage>());

        var producer = endpoint.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("data"));
        exchange.In.Headers[MqttHeaders.UserProperties] = new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2"
        };
        await ((IProducer)producer).Process(exchange);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.UserProperties.Should().HaveCount(2);
    }
}
