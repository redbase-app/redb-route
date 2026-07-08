using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;
using Xunit.Abstractions;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Integration tests against a real Kafka cluster (3-node KRaft).
/// Expects brokers at localhost:29092,localhost:29094,localhost:29096.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaIntegrationTests
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";
    private readonly ITestOutputHelper _output;

    public KafkaIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private KafkaEndpoint CreateEndpoint(string topic, string? extraParams = null)
    {
        var qs = $"brokers={BootstrapServers}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"kafka://{topic}?{qs}");
        var component = new KafkaComponent();
        return (KafkaEndpoint)component.CreateEndpoint(uri);
    }

    private async Task<string?> ConsumeOneMessage(string topic, string groupId, int timeoutMs = 15000)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = groupId,
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };

        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            // Retry loop: topic may not be available immediately after auto-creation
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = consumer.Consume(cts.Token);
                    return result?.Message?.Value;
                }
                catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    await Task.Delay(500, cts.Token);
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

    private async Task ProduceMessage(string topic, string value, string? key = null)
    {
        var config = new ProducerConfig { BootstrapServers = BootstrapServers };
        using var producer = new ProducerBuilder<string, string>(config).Build();
        await producer.ProduceAsync(topic, new Message<string, string> { Key = key ?? "", Value = value });
        producer.Flush(TimeSpan.FromSeconds(5));
    }

    // ───── Tests ─────

    [Fact]
    public async Task Producer_SendsMessage_ConsumerReceives()
    {
        var topic = $"test-produce-consume-{Guid.NewGuid():N}";
        _output.WriteLine($"Topic: {topic}");

        var ep = CreateEndpoint(topic);
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello Kafka Cluster"));
        await producer.Process(exchange);
        await producer.Stop();

        var received = await ConsumeOneMessage(topic, $"verify-{Guid.NewGuid():N}");
        received.Should().Be("Hello Kafka Cluster");
    }

    [Fact]
    public async Task Producer_WithRecordMetadata_SetsHeaders()
    {
        var topic = $"test-metadata-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "recordMetadata=true");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("meta-test"));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentTopic);
        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentPartition);
        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentOffset);
    }

    [Fact]
    public async Task Producer_ForwardsMessageHeaders()
    {
        var topic = $"test-headers-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic);
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var msg = new Message("data");
        msg.Headers["custom-header"] = "custom-value";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();

        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"hdr-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        using var cts = new CancellationTokenSource(15000);
        var result = consumer.Consume(cts.Token);
        consumer.Close();

        result.Message.Headers.TryGetLastBytes("custom-header", out var bytes).Should().BeTrue();
        System.Text.Encoding.UTF8.GetString(bytes!).Should().Be("custom-value");
    }

    [Fact]
    public async Task Consumer_PollsMessages_InvokesProcessor()
    {
        var topic = $"test-consumer-{Guid.NewGuid():N}";
        await ProduceMessage(topic, "consumer-test-msg");

        var ep = CreateEndpoint(topic, $"groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(Encoding.UTF8.GetString((byte[])ex.In.Body!));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        completed.Should().Be(tcs.Task, "consumer should receive the message within timeout");
        received.Should().Contain("consumer-test-msg");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Consumer_SetsKafkaMetadataHeaders()
    {
        var topic = $"test-meta-headers-{Guid.NewGuid():N}";
        await ProduceMessage(topic, "meta-test", key: "myKey");

        var ep = CreateEndpoint(topic, $"groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest");
        IExchange? capturedExchange = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedExchange = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers.Should().ContainKey(KafkaHeaders.Topic);
        capturedExchange.In.Headers[KafkaHeaders.Topic].Should().Be(topic);
        capturedExchange.In.Headers.Should().ContainKey(KafkaHeaders.Partition);
        capturedExchange.In.Headers.Should().ContainKey(KafkaHeaders.Offset);
        capturedExchange.In.Headers.Should().ContainKey(KafkaHeaders.Key);
        capturedExchange.In.Headers[KafkaHeaders.Key].Should().Be("myKey");
        capturedExchange.Pattern.Should().Be(ExchangePattern.InOnly);
    }

    [Fact]
    public async Task Consumer_BatchMode_CollectsMultipleMessages()
    {
        var topic = $"test-batch-{Guid.NewGuid():N}";

        for (int i = 0; i < 5; i++)
            await ProduceMessage(topic, $"batch-{i}");

        var ep = CreateEndpoint(topic,
            $"groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest&maxPollRecords=10&pollTimeoutMs=5000");

        var processedMessages = new ConcurrentBag<object?>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                processedMessages.Add(ex.In.Body);
                if (ex.In.Headers.TryGetValue(KafkaHeaders.BatchSize, out _))
                    tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(30_000));
        await consumer.Stop();

        processedMessages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TransactedProducer_DeferredCommit_Delivers()
    {
        // Regression for the "Local: Erroneous state" bug: with transacted=true, committing the
        // deferred KafkaSendAction (what a .Transacted() boundary does) must actually deliver the
        // message. Before the fix this threw because transactional.id + InitTransactions put
        // librdkafka into transactional mode, where Produce without BeginTransaction is rejected.
        var topic = $"test-tx-deliver-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "transacted=true");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("tx-real-msg"));
        await producer.Process(exchange);   // registers the deferred KafkaSendAction

        var actions = (ConcurrentDictionary<string, ITransactedAction>)exchange.Properties["TRANSACT_ACTION"]!;
        foreach (var a in actions.Values)
            await a.Commit();                // ← the deferred ProduceAsync; must NOT throw

        await producer.Stop();

        var received = await ConsumeOneMessage(topic, $"verify-{Guid.NewGuid():N}");
        received.Should().Be("tx-real-msg");
    }

    [Fact]
    public async Task TransactedProducer_RegistersDeferredAction()
    {
        var topic = $"test-transact-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "transacted=true");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted-msg"));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.Properties.Should().ContainKey("TRANSACT_ACTION");
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        actions!.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Roundtrip_ProduceAndConsume_MultipleMessages()
    {
        var topic = $"test-roundtrip-{Guid.NewGuid():N}";
        const int messageCount = 10;

        var epProd = CreateEndpoint(topic);
        var producer = (KafkaProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();

        var epCons = CreateEndpoint(topic, $"groupId=rt-{Guid.NewGuid():N}&autoOffsetReset=Earliest");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(Encoding.UTF8.GetString((byte[])ex.In.Body!));
                if (received.Count >= messageCount)
                    allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
        for (int i = 0; i < messageCount; i++)
            received.Should().Contain($"msg-{i}");
    }

    [Fact]
    public async Task Producer_WithKey_PartitioningWorks()
    {
        var topic = $"test-key-partition-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "key=${header.orderId}");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var msg = new Message("order-data");
        msg.Headers["orderId"] = "ORD-12345";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();

        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"key-verify-{Guid.NewGuid():N}",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
            EnableAutoCommit = true
        };
        using var consumer = new ConsumerBuilder<string, string>(config).Build();
        consumer.Subscribe(topic);

        using var cts = new CancellationTokenSource(15000);
        var result = consumer.Consume(cts.Token);
        consumer.Close();

        result.Message.Key.Should().Be("ORD-12345");
        result.Message.Value.Should().Be("order-data");
    }

    [Fact]
    public async Task MultiNode_ProduceConsume_AcrossCluster()
    {
        var topic = $"test-cluster-{Guid.NewGuid():N}";
        _output.WriteLine($"Cluster test: {topic}");

        // Produce 20 messages with acks=all — replicated across brokers
        var epProd = CreateEndpoint(topic, "acks=all");
        var producer = (KafkaProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < 20; i++)
        {
            var exchange = new Exchange(new Message($"cluster-msg-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();

        // Consume all 20
        var epCons = CreateEndpoint(topic, $"groupId=cluster-{Guid.NewGuid():N}&autoOffsetReset=Earliest");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                if (received.Count >= 20)
                    allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
        await consumer.Stop();

        received.Count.Should().Be(20);
    }

    // ───── EnableAutoCommit (framework-level) ─────

    /// <summary>
    /// Reads the total committed offset for a group across all partitions of a topic via the
    /// group coordinator. Uses a non-subscribing probe consumer (never joins the group → no
    /// rebalance), so it can be called while another consumer in the same group is running.
    /// </summary>
    private long GetCommittedTotal(string topic, string groupId)
    {
        using var admin = new AdminClientBuilder(
            new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        var meta = admin.GetMetadata(topic, TimeSpan.FromSeconds(10));
        var tps = meta.Topics[0].Partitions
            .Select(p => new TopicPartition(topic, new Partition(p.PartitionId)))
            .ToList();

        using var probe = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = groupId,
            EnableAutoCommit = false
        }).Build();

        var committed = probe.Committed(tps, TimeSpan.FromSeconds(10));
        return committed.Where(c => c.Offset != Offset.Unset).Sum(c => c.Offset.Value);
    }

    [Fact]
    public async Task Consumer_AutoCommitDefault_CommitsOffsetInline_BeforeStop()
    {
        // Default EnableAutoCommit=true: the consumer must commit the offset inline right after a
        // successful Process — i.e. BEFORE any graceful stop (the PartitionsRevokedHandler that
        // commits on stop is intentionally not exercised here, so a committed offset can only come
        // from the inline auto-commit path).
        var topic = $"test-autocommit-on-{Guid.NewGuid():N}";
        var groupId = $"ac-on-{Guid.NewGuid():N}";
        const int n = 3;
        for (int i = 0; i < n; i++) await ProduceMessage(topic, $"msg-{i}");

        var ep = CreateEndpoint(topic, $"groupId={groupId}&autoOffsetReset=Earliest");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                if (received.Count >= n) allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
            received.Count.Should().BeGreaterThanOrEqualTo(n);

            // Poll the committed offset WHILE the consumer is still running (no stop → no revoke commit).
            long committed = 0;
            for (int i = 0; i < 20 && committed < n; i++)
            {
                await Task.Delay(500);
                committed = GetCommittedTotal(topic, groupId);
            }

            committed.Should().BeGreaterThanOrEqualTo(n,
                "EnableAutoCommit=true must commit the offset inline after Process, before any stop");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Fact]
    public async Task Consumer_AutoCommitDisabled_NoTransaction_DoesNotCommitInline()
    {
        // EnableAutoCommit=false and no transactional route: nothing commits the deferred
        // KafkaCommitAction during processing, so the committed offset stays unset while the
        // consumer is running (it would only advance on a graceful stop via the revoke handler,
        // which we assert BEFORE stopping).
        var topic = $"test-autocommit-off-{Guid.NewGuid():N}";
        var groupId = $"ac-off-{Guid.NewGuid():N}";
        const int n = 3;
        for (int i = 0; i < n; i++) await ProduceMessage(topic, $"msg-{i}");

        var ep = CreateEndpoint(topic, $"groupId={groupId}&autoOffsetReset=Earliest&enableAutoCommit=false");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                if (received.Count >= n) allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
            received.Count.Should().BeGreaterThanOrEqualTo(n);

            // Give any (unexpected) commit a chance to surface, then assert NOTHING was committed.
            await Task.Delay(3_000);
            GetCommittedTotal(topic, groupId).Should().Be(0,
                "without auto-commit and without a transaction, the offset must not advance during processing");
        }
        finally
        {
            await consumer.Stop();
        }
    }
}
