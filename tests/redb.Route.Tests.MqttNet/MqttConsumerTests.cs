using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

public class MqttConsumerTests
{
    private static MqttEndpoint CreateEndpoint(
        MqttMode mode = MqttMode.Subscribe,
        string topic = "test/topic",
        string? broker = "main",
        string? server = null,
        int qos = 0,
        string? sharedSubscription = null,
        string? clientId = null)
    {
        var component = new MqttComponent();
        var parameters = new Dictionary<string, string> { ["mode"] = mode.ToString() };
        if (broker != null) parameters["broker"] = broker;
        if (server != null) parameters["server"] = server;
        if (qos > 0) parameters["qos"] = qos.ToString();
        if (sharedSubscription != null) parameters["sharedSubscription"] = sharedSubscription;
        if (clientId != null) parameters["clientId"] = clientId;

        var uri = new EndpointUri("mqtt", topic, $"mqtt:{topic}", parameters);
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    private static MqttEndpoint CreateEndpointWithMocks(
        out IMqttClient mockClient,
        out IMqttClientFactory mockFactory,
        out IMqttBrokerRegistry mockRegistry,
        string topic = "test/topic",
        int qos = 0,
        string? sharedSubscription = null,
        string? clientId = null)
    {
        var endpoint = CreateEndpoint(
            MqttMode.Subscribe, topic, "main",
            qos: qos, sharedSubscription: sharedSubscription, clientId: clientId);

        mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);

        mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));

        mockRegistry = Substitute.For<IMqttBrokerRegistry>();
        mockRegistry.GetOptions("main").Returns(new MqttBrokerOptions { Server = "localhost" });
        mockRegistry.Contains("main").Returns(true);

        endpoint.MqttComponent.ClientFactory = mockFactory;
        endpoint.MqttComponent.BrokerRegistry = mockRegistry;

        return endpoint;
    }

    private static MqttApplicationMessageReceivedEventArgs CreateReceivedArgs(
        MqttApplicationMessage message, string clientId = "c1")
    {
        var packet = new MqttPublishPacket { Topic = message.Topic };
        return new MqttApplicationMessageReceivedEventArgs(clientId, message, packet, null);
    }

    // ── Start / Stop lifecycle ──────────────────────────────────────

    [Fact]
    public async Task Start_ConnectsAndSubscribes()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out var mockFactory, out _);

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();

        await mockFactory.Received(1).CreateConnectedClientAsync(
            Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        await mockClient.Received(1).SubscribeAsync(
            Arg.Any<MqttClientSubscribeOptions>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_UnsubscribesAndDisconnects()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();
        await consumer.Stop();

        await mockClient.Received(1).UnsubscribeAsync(
            Arg.Any<MqttClientUnsubscribeOptions>(), Arg.Any<CancellationToken>());
        await mockClient.Received(1).DisconnectAsync(
            Arg.Any<MqttClientDisconnectOptions>(), Arg.Any<CancellationToken>());
    }

    // ── Broker resolution ───────────────────────────────────────────

    [Fact]
    public async Task Start_NamedBroker_ResolvesFromRegistry()
    {
        var endpoint = CreateEndpointWithMocks(
            out _, out _, out var mockRegistry);

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();

        mockRegistry.Received(1).GetOptions("main");
    }

    [Fact]
    public async Task Start_InlineServer_DoesNotUseRegistry()
    {
        var endpoint = CreateEndpoint(
            MqttMode.Subscribe, "test", broker: null, server: "mqtt.local");

        var mockClient = Substitute.For<IMqttClient>();
        mockClient.IsConnected.Returns(true);
        var mockFactory = Substitute.For<IMqttClientFactory>();
        mockFactory.CreateConnectedClientAsync(
                Arg.Any<MqttBrokerOptions>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockClient));

        endpoint.MqttComponent.ClientFactory = mockFactory;

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();

        await mockFactory.Received(1).CreateConnectedClientAsync(
            Arg.Is<MqttBrokerOptions>(o => o.Server == "mqtt.local"),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Start_NoBrokerNoRegistryNoServer_Throws()
    {
        // Broker is set but no registry
        var endpoint = CreateEndpoint(MqttMode.Subscribe, "t", broker: "main");
        // Don't set BrokerRegistry — it's null

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        var act = async () => await consumer.Start();

        act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IMqttBrokerRegistry*");
    }

    // ── SharedSubscription topic format ─────────────────────────────

    [Fact]
    public async Task Start_SharedSubscription_SubscribesWithSharedTopic()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            topic: "sensors/temp",
            sharedSubscription: "workers");

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();

        await mockClient.Received(1).SubscribeAsync(
            Arg.Is<MqttClientSubscribeOptions>(opts =>
                opts.TopicFilters.Count == 1 &&
                opts.TopicFilters[0].Topic == "$share/workers/sensors/temp"),
            Arg.Any<CancellationToken>());
    }

    // ── QoS subscription level ──────────────────────────────────────

    [Theory]
    [InlineData(0, MqttQualityOfServiceLevel.AtMostOnce)]
    [InlineData(1, MqttQualityOfServiceLevel.AtLeastOnce)]
    [InlineData(2, MqttQualityOfServiceLevel.ExactlyOnce)]
    public async Task Start_QosLevel_SetCorrectly(int qos, MqttQualityOfServiceLevel expected)
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            qos: qos);

        var processor = Substitute.For<IProcessor>();
        var consumer = endpoint.CreateConsumer(processor);

        await consumer.Start();

        await mockClient.Received(1).SubscribeAsync(
            Arg.Is<MqttClientSubscribeOptions>(opts =>
                opts.TopicFilters[0].QualityOfServiceLevel == expected),
            Arg.Any<CancellationToken>());
    }

    // ── CreateExchange tests (via message receive simulation) ───────

    [Fact]
    public async Task OnMessage_CreatesExchangeWithBody()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        // Simulate message received
        var payload = Encoding.UTF8.GetBytes("Hello MQTT");
        var message = new MqttApplicationMessageBuilder()
            .WithTopic("test/topic")
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        // Trigger the event
        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message, "client1"));

        // Wait briefly for async processing
        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])capturedExchange!.In.Body!).Should().Be("Hello MQTT");
    }

    [Fact]
    public async Task OnMessage_SetsTopicHeader()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("sensors/temperature")
            .WithPayload(Encoding.UTF8.GetBytes("data"))
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.Topic].Should().Be("sensors/temperature");
    }

    [Fact]
    public async Task OnMessage_SetsQosHeader()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload(Encoding.UTF8.GetBytes("data"))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.ExactlyOnce)
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.Qos].Should().Be(2);
    }

    [Fact]
    public async Task OnMessage_EmptyPayload_BodyIsNull()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Body.Should().BeNull();
    }

    [Fact]
    public async Task OnMessage_BrokerHeader_Set()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload(Encoding.UTF8.GetBytes("x"))
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.Broker].Should().Be("main");
    }

    [Fact]
    public async Task OnMessage_ClientIdHeader_Set()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _,
            clientId: "my-consumer");

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload(Encoding.UTF8.GetBytes("data"))
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.ClientId].Should().Be("my-consumer");
    }

    [Fact]
    public async Task OnMessage_ContentType_SetInHeaders()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload(Encoding.UTF8.GetBytes("{}"))
            .WithContentType("application/json")
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.ContentType].Should().Be("application/json");
    }

    [Fact]
    public async Task OnMessage_RetainHeader_Set()
    {
        var endpoint = CreateEndpointWithMocks(
            out var mockClient, out _, out _);

        IExchange? capturedExchange = null;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => capturedExchange = x.Arg<IExchange>());

        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();

        var message = new MqttApplicationMessageBuilder()
            .WithTopic("t")
            .WithPayload(Encoding.UTF8.GetBytes("data"))
            .WithRetainFlag(true)
            .Build();

        mockClient.ApplicationMessageReceivedAsync += Raise.Event<Func<MqttApplicationMessageReceivedEventArgs, Task>>(
            CreateReceivedArgs(message));

        await Task.Delay(100);

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers[MqttHeaders.Retain].Should().Be(true);
    }
}
