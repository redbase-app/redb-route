using System.Collections.Concurrent;
using System.Text;
using Confluent.Kafka;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;
using redb.Route.Processors;
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

    /// <summary>Starts a consumer on <paramref name="topic"/> and returns the first exchange it hands the route.</summary>
    private async Task<IExchange> FirstExchange(string topic, string consumerParams)
    {
        var ep = CreateEndpoint(topic, $"groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest&{consumerParams}");
        var first = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                first.TrySetResult(callInfo.Arg<IExchange>());
                return Task.CompletedTask;
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            return await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await consumer.Stop();
        }
    }

    /// <summary>Reads the first record of <paramref name="topic"/> as it is on the wire: key, value, headers.</summary>
    private static Message<string, byte[]>? ReadOneRecord(string topic)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers,
            GroupId = $"verify-{Guid.NewGuid():N}",
            AutoOffsetReset = Confluent.Kafka.AutoOffsetReset.Earliest,
        };
        using var consumer = new ConsumerBuilder<string, byte[]>(config).Build();
        consumer.Subscribe(topic);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (consumer.Consume(TimeSpan.FromMilliseconds(250))?.Message is { } message)
                        return message;
                }
                catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                }
            }
            return null;
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>Sends one exchange as a consumed record would reach the producer: with the headers the consumer set.</summary>
    private async Task<Message<string, byte[]>?> PassOn(string topic, string? producerParams, Action<IMessage> consumed)
    {
        var producer = (KafkaProducer)CreateEndpoint(topic, producerParams).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("passed-on"));
        consumed(exchange.In);
        await producer.Process(exchange);
        await producer.Stop();
        return ReadOneRecord(topic);
    }

    [Fact]
    public async Task Producer_PassingARecordOn_KeepsItsKey()
    {
        // Review R3 (docs/kafka/REVIEW-2026-09-28.md): the consumer put the record's key on the exchange and the
        // producer dropped it with the other redbKafka.* headers: a passthrough changed partition and compaction.
        var topic = $"test-key-carry-{Guid.NewGuid():N}";

        var record = await PassOn(topic, null, m => m.Headers[KafkaHeaders.Key] = "order-7");

        record.Should().NotBeNull();
        record!.Key.Should().Be("order-7");
    }

    [Fact]
    public async Task Producer_KeyOption_WinsOverTheConsumedKey()
    {
        var topic = $"test-key-option-{Guid.NewGuid():N}";

        var record = await PassOn(topic, "key=explicit", m => m.Headers[KafkaHeaders.Key] = "order-7");

        record!.Key.Should().Be("explicit");
    }

    [Fact]
    public async Task Producer_KeyFromHeaderFalse_SendsWithoutAKey()
    {
        var topic = $"test-key-off-{Guid.NewGuid():N}";

        var record = await PassOn(topic, "keyFromHeader=false", m => m.Headers[KafkaHeaders.Key] = "order-7");

        record.Should().NotBeNull();
        record!.Key.Should().BeNull();
    }

    [Fact]
    public async Task Producer_PassingARecordOn_SendsOneContentType()
    {
        // Review R7: the consumer keeps the record's content-type header next to ContentType; the producer wrote both.
        var topic = $"test-content-type-{Guid.NewGuid():N}";

        var record = await PassOn(topic, null, m =>
        {
            m.ContentType = "application/json";
            m.Headers["content-type"] = "application/json";
        });

        record.Should().NotBeNull();
        record!.Headers.Where(h => h.Key == "content-type").Should().ContainSingle()
            .Which.GetValueBytes().Should().Equal(Encoding.UTF8.GetBytes("application/json"));
    }

    [Fact]
    public async Task Consumer_BatchMode_GivesARecordTheSameHeadersAsSingleMode()
    {
        // Review R4 (docs/kafka/REVIEW-2026-09-28.md): the batch path left out redbKafka.Key and redbKafka.Timestamp,
        // so a route reading the key worked record by record and got null once maxPollRecords was set.
        var topic = $"test-batch-shape-{Guid.NewGuid():N}";
        await ProduceMessage(topic, "shape-test", key: "order-42");

        var single = (await FirstExchange(topic, "pollTimeoutMs=5000")).In;
        var batch = await FirstExchange(topic, "maxPollRecords=10&pollTimeoutMs=5000");
        var inBatch = batch.In.Body.Should().BeAssignableTo<IReadOnlyList<IMessage>>().Subject[0];

        inBatch.Headers.Keys.Should().BeEquivalentTo(single.Headers.Keys);
        inBatch.Headers[KafkaHeaders.Key].Should().Be("order-42");
        inBatch.Headers[KafkaHeaders.Timestamp].Should().Be(single.Headers[KafkaHeaders.Timestamp]);
    }

    /// <summary>Runs <paramref name="body"/> inside a transacted block, as <c>.Transacted()</c> on a route does.</summary>
    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

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
        // The block commits the deferred KafkaSendAction, i.e. the deferred ProduceAsync; it must NOT throw.
        await InTransaction(exchange, (ex, ct) => producer.Process(ex, ct));

        await producer.Stop();

        var received = await ConsumeOneMessage(topic, $"verify-{Guid.NewGuid():N}");
        received.Should().Be("tx-real-msg");
    }

    [Fact]
    public async Task TransactedProducer_DeferredCommit_RecordsTheSameMetadataAsAnImmediateSend()
    {
        // Review R6 (docs/kafka/REVIEW-2026-09-28.md): the deferred send wrote three of the four redbKafka.Sent.*
        // headers; redbKafka.Sent.Timestamp was there outside .Transacted() and null inside it.
        var topic = $"test-tx-metadata-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "transacted=true&recordMetadata=true");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("tx-meta"));
        await InTransaction(exchange, (ex, ct) => producer.Process(ex, ct));
        await producer.Stop();

        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentTopic);
        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentPartition);
        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentOffset);
        exchange.In.Headers.Should().ContainKey(KafkaHeaders.SentTimestamp);
    }

    [Fact]
    public async Task TransactedProducer_OutsideTransactedBlock_Refuses()
    {
        var topic = $"test-transact-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(topic, "transacted=true");
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        // Nothing would ever commit a send deferred outside .Transacted(), so the producer refuses it.
        var act = () => producer.Process(new Exchange(new Message("transacted-msg")));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Transacted()*");
        await producer.Stop();
    }

    /// <summary>Sends one message from inside a <c>.Transacted()</c> block that then fails.</summary>
    private async Task SendFromAFailedBlock(string topic, string? producerParams)
    {
        var ep = CreateEndpoint(topic, producerParams);
        var producer = (KafkaProducer)ep.CreateProducer();
        await producer.Start();

        var failed = () => InTransaction(new Exchange(new Message("sent-from-a-failed-block")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await producer.Stop();
    }

    [Fact]
    public async Task Producer_without_transacted_joins_the_block_and_sends_nothing_when_it_fails()
    {
        var topic = $"test-join-{Guid.NewGuid():N}";

        await SendFromAFailedBlock(topic, producerParams: null);

        (await ConsumeOneMessage(topic, $"verify-{Guid.NewGuid():N}", timeoutMs: 8000))
            .Should().BeNull("inside .Transacted() a send joins the transaction unless transacted=false opts out");
    }

    [Fact]
    public async Task Producer_with_transacted_false_sends_at_once_even_from_a_failed_block()
    {
        var topic = $"test-optout-{Guid.NewGuid():N}";

        await SendFromAFailedBlock(topic, "transacted=false");

        (await ConsumeOneMessage(topic, $"verify-{Guid.NewGuid():N}")).Should().Be("sent-from-a-failed-block");
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

        // Same UnknownTopicOrPart retry the file's own ConsumeOneMessage helper uses: the topic is
        // auto-created by the produce above, and its metadata may lag under cluster load.
        using var cts = new CancellationTokenSource(15000);
        ConsumeResult<string, string>? result = null;
        while (result is null && !cts.Token.IsCancellationRequested)
        {
            try
            {
                result = consumer.Consume(cts.Token);
            }
            catch (ConsumeException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
            {
                await Task.Delay(500, cts.Token);
            }
        }
        consumer.Close();

        result.Should().NotBeNull();
        result!.Message.Key.Should().Be("ORD-12345");
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

    // ───── ackMode (framework-level offset commit) ─────

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
        // Default ackMode=manual: the consumer must commit the offset inline right after a
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
                "ackMode=manual must commit the offset inline after Process, before any stop");
        }
        finally
        {
            await consumer.Stop();
        }
    }

    [Fact]
    public async Task Consumer_AckModeAuto_CommitsOnReceipt_EvenWhenTheRouteFails()
    {
        // ackMode=auto (at-most-once): the offset is committed before the route runs, so records whose route
        // failed are not read again. The committed offset advances while the consumer still runs.
        var topic = $"test-ackmode-auto-{Guid.NewGuid():N}";
        var groupId = $"ack-auto-{Guid.NewGuid():N}";
        const int n = 3;
        for (int i = 0; i < n; i++) await ProduceMessage(topic, $"msg-{i}");

        var ep = CreateEndpoint(topic, $"groupId={groupId}&autoOffsetReset=Earliest&ackMode=auto");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(ci =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                if (received.Count >= n) allReceived.TrySetResult();
                throw new InvalidOperationException("the route fails");
            });

        var consumer = (KafkaConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
            received.Count.Should().BeGreaterThanOrEqualTo(n);

            long committed = 0;
            for (int i = 0; i < 20 && committed < n; i++)
            {
                await Task.Delay(500);
                committed = GetCommittedTotal(topic, groupId);
            }

            committed.Should().BeGreaterThanOrEqualTo(n,
                "ackMode=auto commits on receipt, whatever the route then does");
        }
        finally
        {
            await consumer.Stop();
        }
    }
}
