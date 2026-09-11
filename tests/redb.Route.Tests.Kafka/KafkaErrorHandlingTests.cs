using System.Collections.Concurrent;
using Confluent.Kafka;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Волна A2 плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN, e2e против живого кластера.
/// A message whose processing throws used to be LOST with nothing but a log line: its offset was
/// never committed, but the next successful message's commit covered it (a Kafka commit is a
/// position, not a per-record mark), so a restart resumed past it. The connector's own family
/// contract is different - RabbitMQ nacks with requeue. And the option that exists exactly for
/// this scenario, breakOnFirstError, was declared, documented, exposed in the DSL and read by
/// nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaErrorHandlingTests : IAsyncLifetime
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";
    private readonly List<IConsumer> _consumers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _consumers) await c.Stop();
    }

    private KafkaEndpoint CreateEndpoint(string topic, string extraParams)
    {
        var uri = EndpointUriParser.Parse($"kafka://{topic}?brokers={BootstrapServers}&{extraParams}");
        return (KafkaEndpoint)new KafkaComponent().CreateEndpoint(uri);
    }

    private static async Task Produce(string topic, params string[] values)
    {
        var config = new ProducerConfig { BootstrapServers = BootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        foreach (var v in values)
            await producer.ProduceAsync(topic, new Message<string, string> { Key = "", Value = v });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(200);
    }

    [Fact]
    public async Task BreakOnFirstError_FailedMessage_IsRetriedNotLost()
    {
        var topic = $"a2-break-{Guid.NewGuid():N}"[..30];
        await Produce(topic, "poison-once", "after");

        var attempts = new ConcurrentQueue<string>();
        var failedOnce = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var body = System.Text.Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!);
            attempts.Enqueue(body);
            if (body == "poison-once" && Interlocked.Exchange(ref failedOnce, 1) == 0)
                throw new InvalidOperationException("transient failure");
            return Task.CompletedTask;
        });

        var endpoint = CreateEndpoint(topic,
            $"groupId=g-{Guid.NewGuid():N}&autoOffsetReset=earliest&breakOnFirstError=true");
        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        await consumer.Start();

        await WaitUntil(() => attempts.Count(a => a == "poison-once") >= 2 && attempts.Contains("after"));

        attempts.Count(a => a == "poison-once").Should().BeGreaterThanOrEqualTo(2,
            "breakOnFirstError обязан вернуть упавшее сообщение повтором, а не пропустить его");
        attempts.Should().Contain("after", "после успешного повтора движение продолжается");
    }

    [Fact]
    public async Task Default_FailedMessage_IsSkippedExplicitly_AndCountedByTheRoute()
    {
        var topic = $"a2-skip-{Guid.NewGuid():N}"[..30];
        await Produce(topic, "poison-always", "after");

        // The PRODUCT path: a real route. Pipeline statistics belong to the core's
        // StatisticsProcessor wrapping the consumer's processor - the connector deliberately does
        // not record the escaping exception itself, that would double-count (принцип 0).
        var processed = new ConcurrentQueue<string>();
        var uri = $"kafka://{topic}?brokers={BootstrapServers}&groupId=g-{Guid.NewGuid():N}&autoOffsetReset=earliest";

        await using var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddRoutes(r => r.From(uri).Process(e =>
        {
            var body = System.Text.Encoding.UTF8.GetString((byte[])e.In.Body!);
            if (body == "poison-always") throw new InvalidOperationException("permanent failure");
            processed.Enqueue(body);
        }));
        await context.Start();

        await WaitUntil(() => processed.Contains("after"));

        // The default stays "move on" (Camel's default too), but it is visible:
        // the escaping exception lands in the endpoint statistics via the core wrapper.
        processed.Should().Contain("after");
        ((IEndpointStatistics)context.GetEndpoint(uri)).Errors.Should().BeGreaterThanOrEqualTo(1,
            "пропуск отравленного сообщения обязан быть виден в статистике эндпоинта");
    }

    [Fact]
    public async Task BatchAcrossPartitions_CommitsEveryPartition_BeforeShutdown()
    {
        // Волна A6.4. The batch used to commit only batch[^1] - ONE partition; the others rode on
        // the revoked-handler at CLEAN shutdown (verified by the review's probe), so kill -9
        // replayed far more than needed. Committed offsets must be right while running.
        var topic = $"a6-batch-{Guid.NewGuid():N}"[..30];
        using (var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build())
        {
            await admin.CreateTopicsAsync([new Confluent.Kafka.Admin.TopicSpecification
                { Name = topic, NumPartitions = 3, ReplicationFactor = 1 }]);
        }
        await Task.Delay(1500);

        var config = new ProducerConfig { BootstrapServers = BootstrapServers };
        using (var producer = new ProducerBuilder<string, string>(config).Build())
        {
            for (var i = 0; i < 30; i++)
                await producer.ProduceAsync(new TopicPartition(topic, new Partition(i % 3)),
                    new Message<string, string> { Key = "", Value = $"m{i}" });
            producer.Flush(TimeSpan.FromSeconds(10));
        }

        var processedTotal = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var messages = (List<IMessage>)ci.Arg<IExchange>().In.Body!;
            Interlocked.Add(ref processedTotal, messages.Count);
            return Task.CompletedTask;
        });

        var groupId = $"g-{Guid.NewGuid():N}";
        var endpoint = CreateEndpoint(topic,
            $"groupId={groupId}&autoOffsetReset=earliest&maxPollRecords=30&pollTimeoutMs=4000");
        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        await consumer.Start();

        await WaitUntil(() => Volatile.Read(ref processedTotal) >= 30);
        await Task.Delay(1500); // let the inline commit land

        // The consumer is STILL RUNNING - no revoked-handler safety net involved.
        using var check = new ConsumerBuilder<string, string>(new ConsumerConfig
            { BootstrapServers = BootstrapServers, GroupId = groupId }).Build();
        var tps = Enumerable.Range(0, 3).Select(i => new TopicPartition(topic, new Partition(i))).ToList();
        var committed = check.Committed(tps, TimeSpan.FromSeconds(10));
        check.Close();

        committed.Should().OnlyContain(c => c.Offset.Value == 10,
            "батч обязан коммитить последний оффсет КАЖДОЙ партиции, а не одной последней записи");
    }

    [Fact]
    public async Task Producer_RecordsWireBytesOut()
    {
        var topic = $"a5-bytes-{Guid.NewGuid():N}"[..30];
        var endpoint = CreateEndpoint(topic, "acks=all");
        var producer = (KafkaProducer)endpoint.CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message("12345")));
        }
        finally
        {
            await producer.Stop();
        }

        // ToProcessor counts MessagesOut for a routed producer, but only the connector knows the
        // payload size on the wire.
        ((IEndpointStatistics)endpoint).BytesOut.Should().Be(5);
    }

    [Fact]
    public async Task TopicIsPattern_SubscribesByRegex()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var actualTopic = $"a2-pat-{suffix}-eu";
        await Produce(actualTopic, "matched");

        var received = new ConcurrentQueue<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            received.Enqueue(System.Text.Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
            return Task.CompletedTask;
        });

        // The topic in the URI is a regex WITHOUT the ^ prefix - the flag must supply it.
        var endpoint = CreateEndpoint($"a2-pat-{suffix}-.*",
            $"groupId=g-{Guid.NewGuid():N}&autoOffsetReset=earliest&topicIsPattern=true");
        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        await consumer.Start();

        await WaitUntil(() => received.Contains("matched"), 30000);

        received.Should().Contain("matched",
            "topicIsPattern=true обязан включать regex-подписку, а не быть мёртвым флагом");
    }

    [Fact]
    public void TopicIsPattern_WithExplicitPartition_IsRefused()
    {
        var act = () => CreateEndpoint("orders-.*",
            "groupId=g&topicIsPattern=true&partitionNumber=0");
        act.Should().Throw<ArgumentException>().WithMessage("*topicIsPattern*",
            "regex-подписка и явное назначение партиции противоречат друг другу");
    }
}
