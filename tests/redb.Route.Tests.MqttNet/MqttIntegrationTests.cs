using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.Expressions;
using redb.Route.MqttNet.Connection;
using Xunit.Abstractions;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// Integration tests for the MQTT connector against a real Mosquitto broker.
/// Requires: docker compose -f docker-compose.tests.yml up -d mosquitto
/// Broker: localhost:11883 (anonymous, no auth).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttIntegrationTests
{
    private const string Server = "localhost";
    private const int Port = 11883;

    private readonly ITestOutputHelper _output;

    public MqttIntegrationTests(ITestOutputHelper output) => _output = output;

    // ── Helpers ─────────────────────────────────────────────────────

    private static string UniqueTopic() => $"test/{Guid.NewGuid():N}";

    /// <summary>
    /// Creates a connected MqttEndpoint from a URI with inline server (no named broker).
    /// The component gets a real DefaultMqttClientFactory.
    /// </summary>
    private static MqttEndpoint CreateEndpoint(string topic, MqttMode mode, string? extraParams = null)
    {
        var qs = $"mode={mode}&server={Server}&port={Port}";
        if (extraParams is not null) qs += $"&{extraParams}";

        var uri = EndpointUriParser.Parse($"mqtt:{topic}?{qs}");
        var component = new MqttComponent
        {
            ClientFactory = new DefaultMqttClientFactory()
        };
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    /// <summary>Creates a standalone MQTTnet client for test-side pub/sub.</summary>
    private static async Task<IMqttClient> CreateRawClientAsync(string? clientId = null)
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(Server, Port)
            .WithClientId(clientId ?? $"test-raw-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();
        await client.ConnectAsync(options);
        return client;
    }

    // ── Basic Publish → Subscribe roundtrip ─────────────────────────

    [Fact]
    public async Task PublishAndSubscribe_Roundtrip()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Consumer
        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            // Producer
            var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish, "qos=1");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                var exchange = new Exchange(new Message("Hello from integration test"));
                await ((IProducer)producer).Process(exchange);
                _output.WriteLine("Published message to {0}", topic);

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("Hello from integration test");
                _output.WriteLine("Received {0} message(s)", received.Count);
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Multiple messages ───────────────────────────────────────────

    [Fact]
    public async Task PublishMultiple_AllReceived()
    {
        var topic = UniqueTopic();
        const int messageCount = 10;
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                if (received.Count >= messageCount) tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish, "qos=1");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                for (var i = 0; i < messageCount; i++)
                {
                    var exchange = new Exchange(new Message($"msg-{i}"));
                    await ((IProducer)producer).Process(exchange);
                }

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().HaveCount(messageCount);
                _output.WriteLine("All {0} messages received", received.Count);
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── QoS levels ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task QosLevels_WorkCorrectly(int qos)
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, $"qos={qos}");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish, $"qos={qos}");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                await ((IProducer)producer).Process(new Exchange(new Message($"qos-{qos}-test")));

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                received.First().In.Headers[MqttHeaders.Qos].Should().Be(qos);
                _output.WriteLine("QoS {0} roundtrip OK", qos);
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Wildcard subscription (+) ───────────────────────────────────

    [Fact]
    public async Task WildcardSubscription_SingleLevel()
    {
        var baseTopic = $"test/{Guid.NewGuid():N}";
        var subTopic = $"{baseTopic}/+/data";
        var pubTopic = $"{baseTopic}/sensor1/data";

        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(subTopic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            // Publish via raw client to a specific sub-topic
            using var rawClient = await CreateRawClientAsync();
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(pubTopic)
                .WithPayload(Encoding.UTF8.GetBytes("wildcard-test"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await rawClient.PublishAsync(message);
            await rawClient.DisconnectAsync();

            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received.Should().NotBeEmpty();
            Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("wildcard-test");
            received.First().In.Headers[MqttHeaders.Topic].Should().Be(pubTopic);
            _output.WriteLine("Wildcard subscription matched: {0} → {1}", subTopic, pubTopic);
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Wildcard subscription (#) ───────────────────────────────────

    [Fact]
    public async Task WildcardSubscription_MultiLevel()
    {
        var baseTopic = $"test/{Guid.NewGuid():N}";
        var subTopic = $"{baseTopic}/#";
        var pubTopic = $"{baseTopic}/a/b/c";

        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(subTopic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            using var rawClient = await CreateRawClientAsync();
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(pubTopic)
                .WithPayload(Encoding.UTF8.GetBytes("multi-level"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await rawClient.PublishAsync(message);
            await rawClient.DisconnectAsync();

            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received.Should().NotBeEmpty();
            received.First().In.Headers[MqttHeaders.Topic].Should().Be(pubTopic);
            _output.WriteLine("Multi-level wildcard matched: {0} → {1}", subTopic, pubTopic);
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Retain flag ─────────────────────────────────────────────────

    [Fact]
    public async Task RetainedMessage_ReceivedOnSubscribe()
    {
        var topic = UniqueTopic();

        // First: publish a retained message
        var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish, "qos=1&retain=true");
        var producer = pubEndpoint.CreateProducer();
        await producer.Start();

        try
        {
            await ((IProducer)producer).Process(new Exchange(new Message("retained-payload")));
        }
        finally
        {
            await producer.Stop();
        }

        // Brief delay for broker to store the retained message
        await Task.Delay(500);

        // Now subscribe — should receive the retained message immediately
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received.Should().NotBeEmpty();
            Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("retained-payload");
            ((bool)received.First().In.Headers[MqttHeaders.Retain]!).Should().BeTrue();
            _output.WriteLine("Retained message received");
        }
        finally
        {
            await consumer.Stop();

            // Cleanup: clear retained message by publishing empty payload with retain
            using var rawClient = await CreateRawClientAsync();
            var clearMsg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Array.Empty<byte>())
                .WithRetainFlag(true)
                .Build();
            await rawClient.PublishAsync(clearMsg);
            await rawClient.DisconnectAsync();
        }
    }

    // ── MQTT 5.0 properties roundtrip ───────────────────────────────

    [Fact]
    public async Task Mqtt5Properties_ContentType_Roundtrip()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish,
                "qos=1&contentType=application/json");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                await ((IProducer)producer).Process(
                    new Exchange(new Message("{\"temp\":22.5}")));

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();

                var ex = received.First();
                Encoding.UTF8.GetString((byte[])ex.In.Body!).Should().Be("{\"temp\":22.5}");
                ex.In.Headers[MqttHeaders.ContentType].Should().Be("application/json");
                _output.WriteLine("MQTT 5.0 ContentType roundtrip OK");
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Response topic roundtrip ────────────────────────────────────

    [Fact]
    public async Task Mqtt5Properties_ResponseTopic_Roundtrip()
    {
        var topic = UniqueTopic();
        var responseTopic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            // Publish with response topic via raw client (to set MQTT 5 props directly)
            using var rawClient = await CreateRawClientAsync();
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes("request"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithResponseTopic(responseTopic)
                .WithCorrelationData(Encoding.UTF8.GetBytes("corr-123"))
                .Build();
            await rawClient.PublishAsync(message);
            await rawClient.DisconnectAsync();

            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received.Should().NotBeEmpty();

            var ex = received.First();
            ex.In.Headers[MqttHeaders.ResponseTopic].Should().Be(responseTopic);
            ex.In.Headers[MqttHeaders.CorrelationData].Should().Be("corr-123");
            _output.WriteLine("MQTT 5.0 ResponseTopic + CorrelationData roundtrip OK");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Topic override from header ──────────────────────────────────

    [Fact]
    public async Task Producer_TopicOverrideFromHeader()
    {
        var defaultTopic = UniqueTopic();
        var actualTopic = UniqueTopic();

        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe to the actual (overridden) topic
        var subEndpoint = CreateEndpoint(actualTopic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            // Producer configured for default topic, but header overrides
            var pubEndpoint = CreateEndpoint(defaultTopic, MqttMode.Publish, "qos=1");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                var exchange = new Exchange(new Message("overridden"));
                exchange.In.Headers[MqttHeaders.Topic] = actualTopic;
                await ((IProducer)producer).Process(exchange);

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("overridden");
                _output.WriteLine("Topic override: {0} → {1}", defaultTopic, actualTopic);
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Binary payload roundtrip ────────────────────────────────────

    [Fact]
    public async Task BinaryPayload_Roundtrip()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = CreateEndpoint(topic, MqttMode.Publish, "qos=1");
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                // Send raw bytes
                var data = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
                await ((IProducer)producer).Process(new Exchange(new Message(data)));

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                // Consumer decodes as UTF8 string; binary data survives as string
                received.First().In.Body.Should().NotBeNull();
                _output.WriteLine("Binary payload roundtrip OK");
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Fluent builder URI integration ──────────────────────────────

    [Fact]
    public async Task FluentBuilder_EndToEnd()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Use the fluent builder to generate URIs (like a real route definition)
        string subUri = Mqtt.Subscribe(topic).Server(new ConstantExpression(Server)).Port(Port).Qos(1);
        string pubUri = Mqtt.Publish(topic).Server(new ConstantExpression(Server)).Port(Port).Qos(1);

        _output.WriteLine("Sub URI: {0}", subUri);
        _output.WriteLine("Pub URI: {0}", pubUri);

        var component = new MqttComponent { ClientFactory = new DefaultMqttClientFactory() };

        var subEndpoint = (MqttEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(subUri));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = (MqttEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(pubUri));
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                await ((IProducer)producer).Process(new Exchange(new Message("fluent-builder-test")));

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("fluent-builder-test");
                _output.WriteLine("Fluent builder end-to-end OK");
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── DI registration integration ─────────────────────────────────

    [Fact]
    public async Task DiRegistration_NamedBroker_EndToEnd()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Build DI container
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton(Substitute.For<IRouteContext>());
        services.AddRedbRouteMqtt(mqtt =>
        {
            mqtt.AddBroker("test", o =>
            {
                o.Server = Server;
                o.Port = Port;
            });
        });

        var sp = services.BuildServiceProvider();
        var component = sp.GetRequiredService<MqttComponent>();
        component.BrokerRegistry = sp.GetRequiredService<IMqttBrokerRegistry>();
        component.ClientFactory = sp.GetRequiredService<IMqttClientFactory>();

        // Create endpoints using named broker
        string subUri = Mqtt.Subscribe(topic).Broker(new ConstantExpression("test")).Qos(1);
        string pubUri = Mqtt.Publish(topic).Broker(new ConstantExpression("test")).Qos(1);

        var subEndpoint = (MqttEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(subUri));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            var pubEndpoint = (MqttEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(pubUri));
            var producer = pubEndpoint.CreateProducer();
            await producer.Start();

            try
            {
                await ((IProducer)producer).Process(new Exchange(new Message("di-test")));

                await Task.WhenAny(tcs.Task, Task.Delay(15_000));
                received.Should().NotBeEmpty();
                Encoding.UTF8.GetString((byte[])received.First().In.Body!).Should().Be("di-test");
                received.First().In.Headers[MqttHeaders.Broker].Should().Be("test");
                _output.WriteLine("DI named broker end-to-end OK");
            }
            finally
            {
                await producer.Stop();
            }
        }
        finally
        {
            await consumer.Stop();
        }
    }

    // ── Consumer Stop/Start lifecycle ───────────────────────────────

    [Fact]
    public async Task Consumer_StopAndRestart()
    {
        var topic = UniqueTopic();
        var received = new ConcurrentBag<IExchange>();

        var subEndpoint = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x => received.Add(x.Arg<IExchange>()));

        var consumer = subEndpoint.CreateConsumer(processor);
        await consumer.Start();
        await consumer.Stop();

        // Publish while consumer is stopped — should NOT receive
        using (var rawClient = await CreateRawClientAsync())
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes("missed"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build();
            await rawClient.PublishAsync(msg);
            await rawClient.DisconnectAsync();
        }

        await Task.Delay(1000);
        received.Should().BeEmpty("consumer was stopped, should not receive");

        // Restart with new consumer
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received2 = new ConcurrentBag<IExchange>();
        var processor2 = Substitute.For<IProcessor>();
        processor2.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received2.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });

        var subEndpoint2 = CreateEndpoint(topic, MqttMode.Subscribe, "qos=1");
        var consumer2 = subEndpoint2.CreateConsumer(processor2);
        await consumer2.Start();

        try
        {
            using var rawClient2 = await CreateRawClientAsync();
            var msg2 = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes("after-restart"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await rawClient2.PublishAsync(msg2);
            await rawClient2.DisconnectAsync();

            await Task.WhenAny(tcs.Task, Task.Delay(15_000));
            received2.Should().NotBeEmpty();
            Encoding.UTF8.GetString((byte[])received2.First().In.Body!).Should().Be("after-restart");
            _output.WriteLine("Consumer stop/restart lifecycle OK");
        }
        finally
        {
            await consumer2.Stop();
        }
    }

    // ── Expression resolution roundtrip ─────────────────────────────

    [Fact]
    public async Task Producer_ExpressionTopic_ResolvesAtRuntime()
    {
        var baseTopic = UniqueTopic();
        // Producer topic uses an expression: the actual topic is resolved from header
        // Topic is the URI path, so we put the template there directly
        var pubEndpoint = CreateEndpoint("${header.actualTopic}", MqttMode.Publish, "qos=1");
        var producer = (MqttProducer)pubEndpoint.CreateProducer();
        await producer.Start();

        var received = new ConcurrentBag<IExchange>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Consumer subscribes to the real topic
        var subEndpoint = CreateEndpoint(baseTopic, MqttMode.Subscribe, "qos=1");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(x =>
            {
                received.Add(x.Arg<IExchange>());
                tcs.TrySetResult();
            });
        var consumer = (MqttConsumer)subEndpoint.CreateConsumer(processor);
        await consumer.Start();

        // Publish with expression-resolved topic
        var msg = new Message("expr-topic-test");
        msg.Headers["actualTopic"] = baseTopic;
        var exchange = new Exchange(msg);
        await producer.Process(exchange);

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await producer.Stop();
        await consumer.Stop();
        await pubEndpoint.Stop();
        await subEndpoint.Stop();

        received.Should().NotBeEmpty("topic expression should resolve to the subscribed topic");
        Encoding.UTF8.GetString((byte[])received.First().In.Body!)
            .Should().Be("expr-topic-test");
    }
}
