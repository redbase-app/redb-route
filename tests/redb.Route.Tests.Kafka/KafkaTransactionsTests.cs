using System.Reflection;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Logging;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;
using redb.Route.Processors;
using redb.Route.Transactions;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Kafka transactions (<c>transactionalIdPrefix</c>) against the live 3-node cluster: the sends a producer defers in a
/// <c>.Transacted()</c> block commit as one Kafka transaction or not at all, the offset a route consumed from Kafka commits
/// in that transaction (exactly-once within Kafka), a send outside a block is a transaction of its own, and producers
/// sharing a prefix do not fence each other.
/// </summary>
public sealed class KafkaTransactionsTests
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";

    /// <summary>Over librdkafka's default message.max.bytes: refused before it leaves the client.</summary>
    private static byte[] TooLarge => new byte[2_000_000];

    private static async Task<string> CreateTopic(string name)
    {
        var topic = $"{name}-{Guid.NewGuid():N}";
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = 1, ReplicationFactor = 1 }]);
        return topic;
    }

    private static async Task<KafkaProducer> StartProducer(string topic, string parameters)
    {
        var uri = EndpointUriParser.Parse($"kafka://{topic}?brokers={BootstrapServers}&{parameters}");
        var endpoint = (KafkaEndpoint)new KafkaComponent().CreateEndpoint(uri);
        var producer = (KafkaProducer)endpoint.CreateProducer();
        await producer.Start();
        return producer;
    }

    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    private static async Task Produce(string topic, string value)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = BootstrapServers }).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = "", Value = value });
    }

    /// <summary>
    /// Reads <paramref name="topic"/> from the beginning with a fresh group: until <paramref name="expected"/> records
    /// arrived, or, when none are expected, for a quiet window long enough for a record to show up.
    /// </summary>
    private static List<string> Read(string topic, int expected, IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"verify-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            IsolationLevel = isolation,
        };
        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(topic);
        var values = new List<string>();
        var deadline = DateTime.UtcNow + (expected == 0 ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(20));
        try
        {
            while (DateTime.UtcNow < deadline && (expected == 0 || values.Count < expected))
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is not null)
                    values.Add(System.Text.Encoding.UTF8.GetString(result.Message.Value));
            }
        }
        finally
        {
            consumer.Close();
        }
        return values;
    }

    private static async Task<long> CommittedOffset(string group, string topic)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var result = await admin.ListConsumerGroupOffsetsAsync(
            [new ConsumerGroupTopicPartitions(group, [new TopicPartition(topic, 0)])]);
        return result[0].Partitions[0].Offset.Value;
    }

    private sealed class ExchangeOutcome : IRouteLifecycleListener
    {
        public readonly TaskCompletionSource<Exception?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OnExchangeCompleted(string routeId, IExchange exchange, CancellationToken ct)
        {
            Done.TrySetResult(null);
            return Task.CompletedTask;
        }

        public Task OnExchangeFailed(string routeId, IExchange exchange, Exception exception, CancellationToken ct)
        {
            Done.TrySetResult(exception);
            return Task.CompletedTask;
        }
    }

    private sealed class FailsOnCommit : ITransactedAction
    {
        public Task Commit(CancellationToken ct = default) =>
            throw new InvalidOperationException("a step committed after the Kafka transaction fails");

        public Task Rollback(CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task The_sends_of_a_block_commit_as_one_kafka_transaction_or_none()
    {
        var topic = await CreateTopic("eos-batch");
        var producer = await StartProducer(topic, "transactionalIdPrefix=eos-batch");

        var block = () => InTransaction(new Exchange(new Message("first")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = TooLarge;
            await producer.Process(ex, ct);
        });
        await block.Should().ThrowAsync<KafkaException>();
        await producer.Stop();

        Read(topic, expected: 0).Should().BeEmpty("the first send is aborted with the second");
        Read(topic, expected: 1, IsolationLevel.ReadUncommitted).Should().Equal(["first"],
            "it did reach the broker: the transaction's abort is what hides it");
    }

    [Fact]
    public async Task A_route_from_kafka_commits_the_consumed_offset_in_the_producers_transaction()
    {
        var input = await CreateTopic("eos-in");
        var output = await CreateTopic("eos-out");
        var group = $"eos-{Guid.NewGuid():N}";
        await Produce(input, "order-1");

        var outcome = new ExchangeOutcome();
        await using var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddLifecycleListener(outcome);
        context.AddRoutes(r => r
            .From($"kafka://{input}?brokers={BootstrapServers}&groupId={group}&autoOffsetReset=earliest")
            .Transacted()
                .To($"kafka://{output}?brokers={BootstrapServers}&transactionalIdPrefix=eos-rpw")
                .Process(ex => TransactedActions.Register(ex, "after-kafka", new FailsOnCommit(), "test"))
            .End());
        await context.Start();

        (await outcome.Done.Task.WaitAsync(TimeSpan.FromSeconds(60))).Should().NotBeNull(
            "the step after the Kafka transaction fails the exchange");
        await context.Stop();

        // The consumer commits nothing for a failed exchange: an advanced offset is the transaction's.
        (await CommittedOffset(group, input)).Should().Be(1);
        Read(output, expected: 1).Should().Equal("order-1");
    }

    [Fact]
    public async Task An_aborted_transaction_leaves_the_consumed_offset_uncommitted()
    {
        var input = await CreateTopic("eos-abort-in");
        var output = await CreateTopic("eos-abort-out");
        var group = $"eos-{Guid.NewGuid():N}";
        await Produce(input, "order-1");

        var outcome = new ExchangeOutcome();
        await using var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddLifecycleListener(outcome);
        context.AddRoutes(r => r
            .From($"kafka://{input}?brokers={BootstrapServers}&groupId={group}&autoOffsetReset=earliest")
            .Transacted()
                .Process(ex => ex.In.Body = TooLarge)
                .To($"kafka://{output}?brokers={BootstrapServers}&transactionalIdPrefix=eos-abort")
            .End());
        await context.Start();

        (await outcome.Done.Task.WaitAsync(TimeSpan.FromSeconds(60))).Should().BeAssignableTo<KafkaException>();
        await context.Stop();

        (await CommittedOffset(group, input)).Should().Be(Offset.Unset.Value, "the offset went down with the transaction");
        Read(output, expected: 0).Should().BeEmpty();
    }

    [Fact]
    public async Task A_send_outside_a_block_is_a_transaction_of_its_own()
    {
        var topic = await CreateTopic("eos-single");
        var producer = await StartProducer(topic, "transactionalIdPrefix=eos-single");

        await producer.Process(new Exchange(new Message("alone")));
        await producer.Stop();

        Read(topic, expected: 1).Should().Equal("alone");
    }

    [Fact]
    public async Task Producers_sharing_a_prefix_do_not_fence_each_other()
    {
        var topicA = await CreateTopic("eos-shared-a");
        var topicB = await CreateTopic("eos-shared-b");
        var first = await StartProducer(topicA, "transactionalIdPrefix=eos-shared");
        var second = await StartProducer(topicB, "transactionalIdPrefix=eos-shared");

        // With one transactional.id, starting the second would have fenced the first: its next send fails fatally.
        await first.Process(new Exchange(new Message("a1")));
        await second.Process(new Exchange(new Message("b1")));
        await first.Process(new Exchange(new Message("a2")));
        await first.Stop();
        await second.Stop();

        Read(topicA, expected: 2).Should().Equal("a1", "a2");
        Read(topicB, expected: 1).Should().Equal("b1");
    }

    [Fact]
    public async Task A_producer_stopped_while_marked_for_rebuild_does_not_come_back()
    {
        // Review R9 (docs/kafka/REVIEW-2026-09-28.md): the rebuild after a fatal error called ConnectAsync round Start.
        // Stopped meanwhile, the producer got a fresh client nobody closed, and committed a transaction while stopped.
        // The fatal error itself cannot be provoked on demand; the flag it sets is set here.
        var topic = await CreateTopic("eos-rebuild-stop");
        var producer = await StartProducer(topic, "transactionalIdPrefix=eos-rebuild-stop");
        await producer.Stop();
        typeof(KafkaProducer).GetField("_rebuild", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(producer, true);

        var send = new KafkaTransactionalSend(
            new Message<string, byte[]> { Value = "late"u8.ToArray(), Headers = new Headers() },
            new Exchange(new Message("late")));
        var commit = () => producer.CommitTransactionAsync([send], offsets: null, CancellationToken.None);

        await commit.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped*");
        typeof(KafkaProducer).GetField("_producer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(producer)
            .Should().BeNull("the client built after the stop is closed, not left to the finalizer");
        Read(topic, expected: 0).Should().BeEmpty();
    }

    /// <summary>A producer whose component logs through <paramref name="context"/>'s logger factory.</summary>
    private static async Task<KafkaProducer> StartLoggedProducer(
        RouteContext context, string topic, string parameters)
    {
        var component = new KafkaComponent();
        context.AddComponent(component);
        var uri = EndpointUriParser.Parse($"kafka://{topic}?brokers={BootstrapServers}&{parameters}");
        var producer = (KafkaProducer)((KafkaEndpoint)component.CreateEndpoint(uri)).CreateProducer();
        await producer.Start();
        return producer;
    }

    /// <summary>An exchange carrying the offsets a consumer of <paramref name="cluster"/> offered for it.</summary>
    private static (Exchange Exchange, KafkaCommitAction Commit, KafkaConsumedOffsets Offsets) FromConsumerOf(
        string cluster, string topic)
    {
        var consumer = Substitute.For<IConsumer<string, byte[]>>();
        var record = new ConsumeResult<string, byte[]>
        {
            TopicPartitionOffset = new TopicPartitionOffset(topic, 0, 0),
            Message = new Message<string, byte[]> { Value = [] },
        };
        var commit = new KafkaCommitAction(consumer, record, logger: null);
        var offsets = new KafkaConsumedOffsets(consumer, commit, cluster);
        var exchange = new Exchange(new Message("from-kafka"));
        exchange.Properties[KafkaConsumedOffsets.PropertyKey] = offsets;
        return (exchange, commit, offsets);
    }

    /// <summary>The id the stand's brokers report.</summary>
    private static async Task<string> ClusterId()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        return (await admin.DescribeClusterAsync()).ClusterId;
    }

    [Fact]
    public async Task One_cluster_listed_by_other_brokers_on_the_two_ends_is_still_exactly_once()
    {
        // Review §6.4: the cluster used to be the bootstrap list as written, so a consumer on one broker and a producer
        // on three were two clusters and the offset stayed out of the transaction. It is the cluster id now.
        var input = await CreateTopic("eos-id-in");
        var output = await CreateTopic("eos-id-out");
        var group = $"eos-{Guid.NewGuid():N}";
        await Produce(input, "order-1");

        var outcome = new ExchangeOutcome();
        await using var context = new RouteContext();
        context.AddComponent(new KafkaComponent());
        context.AddLifecycleListener(outcome);
        context.AddRoutes(r => r
            .From($"kafka://{input}?brokers=localhost:29092&groupId={group}&autoOffsetReset=earliest")
            .Transacted()
                .To($"kafka://{output}?brokers={BootstrapServers}&transactionalIdPrefix=eos-id")
                .Process(ex => TransactedActions.Register(ex, "after-kafka", new FailsOnCommit(), "test"))
            .End());
        await context.Start();

        (await outcome.Done.Task.WaitAsync(TimeSpan.FromSeconds(60))).Should().NotBeNull();
        await context.Stop();

        // As in A_route_from_kafka_commits_the_consumed_offset_in_the_producers_transaction: the consumer commits
        // nothing for a failed exchange, so an advanced offset is the transaction's.
        (await CommittedOffset(group, input)).Should().Be(1);
        Read(output, expected: 1).Should().Equal("order-1");
    }

    [Fact]
    public async Task A_consumer_losing_its_partition_mid_transaction_writes_nothing_twice()
    {
        // Review §6.5: consumer A is still in its route when its group moves the partition to consumer B (A exceeded
        // max.poll.interval.ms). B reads the record again and commits it with the offset; A's transaction then carries
        // an old group generation, which the broker refuses, and A's output is aborted. One record out, not two.
        var input = await CreateTopic("eos-rebalance-in");
        var output = await CreateTopic("eos-rebalance-out");
        var group = $"eos-{Guid.NewGuid():N}";
        await Produce(input, "order-1");

        var aInRoute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomeA = new ExchangeOutcome();
        await using var contextA = new RouteContext();
        contextA.AddComponent(new KafkaComponent());
        contextA.AddLifecycleListener(outcomeA);
        contextA.AddRoutes(r => r
            .From($"kafka://{input}?brokers={BootstrapServers}&groupId={group}&autoOffsetReset=earliest" +
                  "&sessionTimeoutMs=6000&maxPollIntervalMs=10000")
            .Transacted()
                .Process(async (_, ct) =>
                {
                    aInRoute.TrySetResult();
                    await releaseA.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
                })
                .To($"kafka://{output}?brokers={BootstrapServers}&transactionalIdPrefix=eos-rebalance-a")
            .End());
        await contextA.Start();
        await aInRoute.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var outcomeB = new ExchangeOutcome();
        await using var contextB = new RouteContext();
        contextB.AddComponent(new KafkaComponent());
        contextB.AddLifecycleListener(outcomeB);
        contextB.AddRoutes(r => r
            .From($"kafka://{input}?brokers={BootstrapServers}&groupId={group}&autoOffsetReset=earliest")
            .Transacted()
                .To($"kafka://{output}?brokers={BootstrapServers}&transactionalIdPrefix=eos-rebalance-b")
            .End());
        await contextB.Start();

        (await outcomeB.Done.Task.WaitAsync(TimeSpan.FromSeconds(90))).Should().BeNull("B takes the partition and commits");
        releaseA.TrySetResult();
        (await outcomeA.Done.Task.WaitAsync(TimeSpan.FromSeconds(60))).Should().NotBeNull(
            "A's transaction carries a group generation that is no longer current");
        await contextA.Stop();
        await contextB.Stop();

        Read(output, expected: 2).Should().Equal(["order-1"], "read_committed sees B's record only");
        (await CommittedOffset(group, input)).Should().Be(1);
    }

    private static (CapturingLoggerProvider Capture, RouteContext Context) LoggedContext()
    {
        var capture = new CapturingLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(capture).SetMinimumLevel(LogLevel.Debug));
        return (capture, new RouteContext(loggerFactory: factory));
    }

    [Fact]
    public async Task Offsets_of_another_cluster_are_left_to_the_consumer_and_the_producer_says_so_once()
    {
        // Review R2 (docs/kafka/REVIEW-2026-09-28.md): offsets of another cluster cannot ride in this producer's
        // transaction; that went without a word but a Debug line.
        var topic = await CreateTopic("eos-foreign");
        var (capture, context) = LoggedContext();
        await using var _ = context;
        var producer = await StartLoggedProducer(context, topic, "transactionalIdPrefix=eos-foreign");

        var first = FromConsumerOf("another-cluster", "input");
        var second = FromConsumerOf("another-cluster", "input");
        await InTransaction(first.Exchange, (ex, ct) => producer.Process(ex, ct));
        await InTransaction(second.Exchange, (ex, ct) => producer.Process(ex, ct));
        await producer.Stop();

        first.Commit.Committed.Should().BeFalse("the offsets stay the consumer's to commit");
        Read(topic, expected: 2).Should().HaveCount(2, "the sends themselves commit");
        capture.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("cannot take the consumed offsets"))
            .Should().ContainSingle("the cause holds for every exchange of the route: said once, not per message")
            .Which.Message.Should().Contain("another-cluster").And.Contain("at-least-once");
    }

    [Fact]
    public async Task A_transaction_committing_after_the_route_returned_says_it_is_not_exactly_once()
    {
        // Review R2/R12: an asynchronous step inside the block commits the transaction after the consumer closed the
        // offer; the offsets correctly stay out of it, and that was silent.
        var topic = await CreateTopic("eos-closed");
        var (capture, context) = LoggedContext();
        await using var _ = context;
        var producer = await StartLoggedProducer(context, topic, "transactionalIdPrefix=eos-closed");

        var late = FromConsumerOf(await ClusterId(), "input");
        late.Offsets.Close();
        await InTransaction(late.Exchange, (ex, ct) => producer.Process(ex, ct));
        await producer.Stop();

        late.Commit.Committed.Should().BeFalse();
        capture.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning
                                                    && e.Message.Contains("after the route returned to its Kafka consumer"));
    }
}
