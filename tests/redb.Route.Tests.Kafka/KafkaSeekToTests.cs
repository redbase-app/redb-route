using System.Collections.Concurrent;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Волна A4 плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN. HandleSeekTo used to read
/// <c>consumer.Assignment</c> right after <c>Subscribe()</c> - the group has not joined yet, the
/// assignment is empty (verified on the live cluster: <c>assignment.Count=0</c>), so
/// <c>seekTo=beginning</c> was a silent no-op for every group consumer. It only worked with an
/// explicit <c>partitionNumber</c>. The seek now rides the partitions-assigned handler, once,
/// on the first assignment - which is what "on first start" in the option's doc always meant.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaSeekToTests : IAsyncLifetime
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";
    private readonly List<IConsumer> _consumers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _consumers) await c.Stop();
    }

    private static async Task Produce(string topic, params string[] values)
    {
        var config = new ProducerConfig { BootstrapServers = BootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        foreach (var v in values)
            await producer.ProduceAsync(topic, new Message<string, string> { Key = "", Value = v });
        producer.Flush(TimeSpan.FromSeconds(10));
    }

    private KafkaConsumer StartCollectingConsumer(string uriParams, string topic, ConcurrentQueue<string> sink)
    {
        var uri = EndpointUriParser.Parse($"kafka://{topic}?brokers={BootstrapServers}&{uriParams}");
        var endpoint = (KafkaEndpoint)new KafkaComponent().CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            sink.Enqueue(System.Text.Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
            return Task.CompletedTask;
        });
        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        return consumer;
    }

    [Fact]
    public async Task SeekToBeginning_GroupConsumer_ReadsTheBacklog()
    {
        var topic = $"a4-seek-{Guid.NewGuid():N}"[..30];
        await Produce(topic, "old-1", "old-2");

        // autoOffsetReset=latest would skip the backlog; seekTo=beginning must win over it.
        var received = new ConcurrentQueue<string>();
        var consumer = StartCollectingConsumer(
            $"groupId=g-{Guid.NewGuid():N}&autoOffsetReset=latest&seekTo=beginning", topic, received);
        await consumer.Start();

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (received.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);

        received.Should().Contain(["old-1", "old-2"],
            "seekTo=beginning был тихим no-op: Assignment пуст сразу после Subscribe");
    }

    [Fact]
    public async Task SeekTo_IsAppliedOnce_NotOnEveryRebalance()
    {
        var topic = $"a4-once-{Guid.NewGuid():N}"[..30];
        await Produce(topic, "first");

        var received = new ConcurrentQueue<string>();
        var groupId = $"g-{Guid.NewGuid():N}";
        var consumer = StartCollectingConsumer(
            $"groupId={groupId}&autoOffsetReset=latest&seekTo=beginning", topic, received);
        await consumer.Start();

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!received.Contains("first") && DateTime.UtcNow < deadline) await Task.Delay(200);
        received.Should().Contain("first");

        // A second consumer joining the same group forces a rebalance of the first one. The seek
        // must NOT re-apply, otherwise every rebalance would replay the topic from the start.
        var second = StartCollectingConsumer(
            $"groupId={groupId}&autoOffsetReset=latest", topic, new ConcurrentQueue<string>());
        await second.Start();
        await Task.Delay(6000);

        received.Count(v => v == "first").Should().Be(1,
            "seek применяется один раз на первое назначение, а не на каждый ребаланс");
    }

    [Fact]
    public async Task SeekTo_SurvivesAnEmptyFirstAssignment()
    {
        // Ревью дуги (M1): more consumers than partitions means a member's FIRST assignment
        // callback can carry zero partitions. TakeSeekToOnce() used to burn the one-shot flag on
        // that empty list - when partitions later arrived on a rebalance, the seek silently never
        // happened: the exact "silent no-op" A4 was written to kill, through the side door.
        var topic = $"a4-empty-{Guid.NewGuid():N}"[..30];
        await Produce(topic, "old-1", "old-2");

        var groupId = $"g-{Guid.NewGuid():N}";

        // Holder drains the single partition and commits, so the group offset sits at the end.
        var holderSink = new ConcurrentQueue<string>();
        var holder = StartCollectingConsumer(
            $"groupId={groupId}&autoOffsetReset=earliest&partitionAssignmentStrategy=CooperativeSticky", topic, holderSink);
        await holder.Start();
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (holderSink.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);
        holderSink.Count.Should().Be(2, "держатель должен выпить партицию и закоммитить хвост");

        // The seeking member joins while the only partition is held: its first assignment is empty.
        var received = new ConcurrentQueue<string>();
        var seeker = StartLoggedConsumer(
            $"groupId={groupId}&autoOffsetReset=latest&seekTo=beginning&partitionAssignmentStrategy=CooperativeSticky", topic, received);
        await seeker.Start();
        await Task.Delay(6000); // let it join and receive the empty assignment

        // Holder leaves; the rebalance finally hands the partition to the seeker.
        await holder.Stop();

        deadline = DateTime.UtcNow.AddSeconds(25);
        while (received.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(200);

        received.Should().Contain(["old-1", "old-2"],
            $"seekTo обязан пережить пустое первое назначение и отработать на первом непустом; лог: {string.Join(" | ", Logs)}");
    }

    internal static readonly ConcurrentQueue<string> Logs = new();

    private sealed class QueueLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new L();
        public void Dispose() { }
        private sealed class L : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => Logs.Enqueue(formatter(state, exception));
        }
    }

    private KafkaConsumer StartLoggedConsumer(string uriParams, string topic, ConcurrentQueue<string> sink)
    {
        var lf = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(new QueueLoggerProvider()));
        var ctx = new RouteContext(loggerFactory: lf);
        ctx.AddComponent(new KafkaComponent());
        var endpoint = (KafkaEndpoint)ctx.GetEndpoint($"kafka://{topic}?brokers={BootstrapServers}&{uriParams}");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            sink.Enqueue(System.Text.Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
            return Task.CompletedTask;
        });
        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        _consumers.Add(consumer);
        return consumer;
    }
}
