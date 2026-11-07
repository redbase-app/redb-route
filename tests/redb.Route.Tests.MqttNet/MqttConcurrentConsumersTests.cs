using System.Text;
using MQTTnet;
using MQTTnet.Protocol;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;
using Xunit.Abstractions;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// Integration tests for MQTT <c>concurrentConsumers</c> (Threads/B) against a real Mosquitto broker.
/// Proves: N&gt;1 processes up to N messages in parallel; default (1) stays strictly serial; and no
/// message is lost (each is acknowledged only after its worker finishes — manual ack-after-process).
/// Broker: localhost:11883 (anonymous).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttConcurrentConsumersTests
{
    private const string Server = "localhost";
    private const int Port = 11883;

    private readonly ITestOutputHelper _output;
    public MqttConcurrentConsumersTests(ITestOutputHelper output) => _output = output;

    private static string UniqueTopic() => $"test/{Guid.NewGuid():N}";

    private static MqttEndpoint CreateEndpoint(string topic, string extraParams)
    {
        var uri = EndpointUriParser.Parse($"mqtt:{topic}?mode=Subscribe&server={Server}&port={Port}&{extraParams}");
        var component = new MqttComponent { ClientFactory = new DefaultMqttClientFactory() };
        return (MqttEndpoint)component.CreateEndpoint(uri);
    }

    private static async Task<IMqttClient> CreateRawClientAsync()
    {
        var factory = new MqttClientFactory();
        var client = factory.CreateMqttClient();
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(Server, Port)
            .WithClientId($"test-raw-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();
        await client.ConnectAsync(options);
        return client;
    }

    private static async Task PublishManyAsync(string topic, int count)
    {
        using var raw = await CreateRawClientAsync();
        for (var i = 0; i < count; i++)
        {
            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes($"msg-{i}"))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();
            await raw.PublishAsync(msg);
        }
        await raw.DisconnectAsync();
    }

    [Fact]
    public async Task ConcurrentConsumers_ProcessesUpToNInParallel()
    {
        const int pool = 5, total = 20;
        var topic = UniqueTopic();

        var current = 0;
        var max = 0;
        var maxLock = new object();
        var received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var c = Interlocked.Increment(ref current);
                lock (maxLock) { if (c > max) max = c; }
                // Service time must exceed pool/delivery-rate so the workers stay saturated rather than
                // starve on the broker's QoS-1 delivery pacing (Little's law) — then the pool reaches N.
                await Task.Delay(200);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref received) >= total) done.TrySetResult();
            });

        var consumer = CreateEndpoint(topic, $"qos=1&concurrentConsumers={pool}").CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await PublishManyAsync(topic, total);
            await Task.WhenAny(done.Task, Task.Delay(30_000));

            Volatile.Read(ref received).Should().Be(total, "no message may be lost under concurrent dispatch");
            max.Should().Be(pool, "the pool must saturate to exactly N workers processing at once");
            _output.WriteLine("concurrentConsumers={0}: max observed concurrency = {1}, received = {2}", pool, max, received);
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Fact]
    public async Task Default_IsStrictlySerial()
    {
        const int total = 8;
        var topic = UniqueTopic();

        var current = 0;
        var max = 0;
        var maxLock = new object();
        var received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var c = Interlocked.Increment(ref current);
                lock (maxLock) { if (c > max) max = c; }
                await Task.Delay(20);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref received) >= total) done.TrySetResult();
            });

        // No concurrentConsumers → default 1 → strictly serial (backward-compatible).
        var consumer = CreateEndpoint(topic, "qos=1").CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await PublishManyAsync(topic, total);
            await Task.WhenAny(done.Task, Task.Delay(30_000));

            Volatile.Read(ref received).Should().Be(total);
            max.Should().Be(1, "the default consumer must never run two bodies at once");
        }
        finally
        {
            await consumer.Stop();
        }
    }
}
