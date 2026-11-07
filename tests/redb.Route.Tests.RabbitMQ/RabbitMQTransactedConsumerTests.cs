using System.Collections.Concurrent;
using System.Text;
using RabbitMQ.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// A consumer with <c>transacted=true</c> settles on a transacted channel, where an ack or nack takes effect only at
/// <c>tx.commit</c>. The broker is the judge: a settle left in an uncommitted transaction keeps the delivery
/// unacknowledged, and closing the channel puts it back in the queue. Expects RabbitMQ at localhost:5672 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQTransactedConsumerTests
{
    private const string Host = "localhost";
    private const int Port = 5672;
    private readonly ITestOutputHelper _output;

    public RabbitMQTransactedConsumerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Transacted_consumer_leaves_no_delivery_unacknowledged()
    {
        var queue = $"test-txcons-ack-{Guid.NewGuid():N}";
        await PublishAsync(queue, "m-0", "m-1", "m-2");

        var calls = new ConcurrentBag<string>();
        var consumer = CreateConsumer(queue, "transacted=true", ex =>
        {
            calls.Add(BodyOf(ex));
            return Task.CompletedTask;
        }, out var endpoint);

        await consumer.Start();
        await WaitUntilAsync(() => consumer.ProcessedCount >= 3);
        await consumer.Stop();
        await endpoint.Stop();

        calls.Should().HaveCount(3, "every delivery is processed once while the consumer runs");
        (await ReadyCountAsync(queue)).Should().Be(0,
            "every ack was committed; an ack left in an open transaction returns its delivery to the queue when the channel closes");
        await DeleteQueueAsync(queue);
    }

    [Fact]
    public async Task Transacted_consumer_requeues_a_failed_delivery_at_once()
    {
        var queue = $"test-txcons-nack-{Guid.NewGuid():N}";
        await PublishAsync(queue, "once-failing");

        var attempts = 0;
        var consumer = CreateConsumer(queue, "transacted=true", _ =>
            Interlocked.Increment(ref attempts) == 1
                ? throw new InvalidOperationException("first attempt fails")
                : Task.CompletedTask, out var endpoint);

        await consumer.Start();
        await WaitUntilAsync(() => consumer.ProcessedCount >= 1);
        await consumer.Stop();
        await endpoint.Stop();

        Volatile.Read(ref attempts).Should().Be(2,
            "the nack is committed, so the broker redelivers at once instead of holding the delivery until the channel closes");
        (await ReadyCountAsync(queue)).Should().Be(0);
        await DeleteQueueAsync(queue);
    }

    [Fact]
    public async Task Transacted_consumer_with_concurrent_consumers_settles_every_delivery_once()
    {
        const int count = 20;
        var queue = $"test-txcons-par-{Guid.NewGuid():N}";
        await PublishAsync(queue, Enumerable.Range(0, count).Select(i => $"p-{i}").ToArray());

        // Every third message fails on its first attempt: acks and nacks of neighbouring deliveries
        // interleave on the one transacted channel.
        var failedOnce = new ConcurrentDictionary<string, bool>();
        var succeeded = new ConcurrentBag<string>();
        var consumer = CreateConsumer(queue, "transacted=true&concurrentConsumers=4&prefetchCount=20", async ex =>
        {
            var body = BodyOf(ex);
            await Task.Delay(Random.Shared.Next(5, 30));
            if (int.Parse(body[2..]) % 3 == 0 && failedOnce.TryAdd(body, true))
                throw new InvalidOperationException($"first attempt of {body} fails");
            succeeded.Add(body);
        }, out var endpoint);

        await consumer.Start();
        await WaitUntilAsync(() => consumer.ProcessedCount >= count);
        await consumer.Stop();
        await endpoint.Stop();

        succeeded.Should().OnlyHaveUniqueItems("an acknowledged delivery is never delivered again")
            .And.HaveCount(count);
        (await ReadyCountAsync(queue)).Should().Be(0);
        await DeleteQueueAsync(queue);
    }

    // ───── Helpers ─────

    private static string BodyOf(IExchange ex) => Encoding.UTF8.GetString((byte[])ex.In.Body!);

    private RabbitMQEndpoint CreateEndpoint(string queue, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username=admin&password=admin&declare=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        return (RabbitMQEndpoint)new RabbitMQComponent().CreateEndpoint(EndpointUriParser.Parse($"rabbitmq://{queue}?{qs}"));
    }

    private RabbitMQConsumer CreateConsumer(string queue, string extraParams, Func<IExchange, Task> body, out RabbitMQEndpoint endpoint)
    {
        endpoint = CreateEndpoint(queue, extraParams);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => body(ci.Arg<IExchange>()));
        return (RabbitMQConsumer)endpoint.CreateConsumer(processor);
    }

    private async Task PublishAsync(string queue, params string[] bodies)
    {
        var endpoint = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)endpoint.CreateProducer();
        await producer.Start();
        foreach (var body in bodies)
            await producer.Process(new Exchange(new Message(body)));
        await producer.Stop();
        await endpoint.Stop();
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        // Room for a redelivery that should not happen to show up.
        await Task.Delay(500);
        _output.WriteLine($"condition met: {condition()}");
    }

    private static async Task<IConnection> ConnectAsync()
        => await new ConnectionFactory { HostName = Host, Port = Port, UserName = "admin", Password = "admin" }.CreateConnectionAsync();

    /// <summary>Messages ready in the queue: a delivery whose settle was never committed is back here once its channel closed.</summary>
    private static async Task<uint> ReadyCountAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private static async Task DeleteQueueAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queue);
    }
}
