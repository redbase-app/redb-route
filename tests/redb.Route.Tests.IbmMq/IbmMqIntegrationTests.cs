using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;
using redb.Route.Processors;
using redb.Route.IbmMq;
using Xunit.Abstractions;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Integration tests against a real IBM MQ broker (Developer Edition).
/// Expects IBM MQ at localhost:1414, QM1, channel DEV.APP.SVRCONN, user app/admin.
/// Docker: docker compose -f C:\Work\yaml\wmq\docker-compose.yml up -d
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 1414;
    private const string Channel = "DEV.APP.SVRCONN";
    private const string QueueManager = "QM1";
    private const string User = "app";
    private const string Password = "admin";

    private readonly ITestOutputHelper _output;

    public IbmMqIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private IbmMqEndpoint CreateEndpoint(string destination, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&channel={Channel}&queueManager={QueueManager}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"wmq:{destination}?{qs}");
        var component = new IbmMqComponent();
        return (IbmMqEndpoint)component.CreateEndpoint(uri);
    }

    // ───── Basic send/receive ─────

    [Fact]
    public async Task Producer_SendsMessage_ConsumerReceives()
    {
        var queue = "DEV.QUEUE.1";
        _output.WriteLine($"Queue: {queue}");

        // Send
        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello IBM MQ"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Receive
        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received.Add(ex.In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("Hello IBM MQ");
    }

    // ───── Metadata headers ─────

    [Fact]
    public async Task Consumer_SetsIbmMqMetadataHeaders()
    {
        var queue = "DEV.QUEUE.2";

        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("meta-test"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=5000");
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

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers.Should().ContainKey(IbmMqHeaders.Destination);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.QueueManager);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.MsgId);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.Format);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.Persistence);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.Priority);
        capturedExchange.In.Headers.Should().ContainKey(IbmMqHeaders.MsgType);

        capturedExchange.In.Headers[IbmMqHeaders.Destination].Should().Be(queue);
        capturedExchange.In.Headers[IbmMqHeaders.QueueManager].Should().Be(QueueManager);
    }

    // ───── Custom headers (RFH2 user properties) ─────

    [Fact]
    public async Task Producer_ForwardsCustomHeaders()
    {
        var queue = "DEV.QUEUE.3";

        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("hdr-data");
        msg.Headers["X-Custom-Id"] = "12345";
        msg.Headers["X-Trace"] = "abc";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("X-Custom-Id");
        captured.In.Headers["X-Custom-Id"].Should().Be("12345");
        captured.In.Headers.Should().ContainKey("X-Trace");
    }

    // ───── Transacted producer — commit ─────

    /// <summary>Runs <paramref name="body"/> inside a transacted block, as <c>.Transacted()</c> on a route does.</summary>
    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    [Fact]
    public async Task TransactedProducer_Commit_DeliversMessage()
    {
        var queue = "DEV.QUEUE.1";

        var epProd = CreateEndpoint(queue, "transacted=true");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted-commit"));
        await InTransaction(exchange, (ex, ct) => producer.Process(ex, ct));   // the send is committed as the block ends

        await producer.Stop();
        await epProd.Stop();

        // Verify message arrived
        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("transacted-commit");
    }

    // ───── Transacted producer — rollback ─────

    [Fact]
    public async Task TransactedProducer_Rollback_MessageNotDelivered()
    {
        var queue = "DEV.QUEUE.2";

        var epProd = CreateEndpoint(queue, "transacted=true");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("should-not-arrive"));
        var failed = () => InTransaction(exchange, async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();   // the send is rolled back with it

        await producer.Stop();
        await epProd.Stop();

        // Should timeout with no message
        var epCons = CreateEndpoint(queue, "waitInterval=2000");
        var received = new ConcurrentBag<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(5_000);
        await consumer.Stop();
        await epCons.Stop();

        received.Should().NotContain("should-not-arrive", "message was rolled back");
    }

    /// <summary>
    /// Sends one uniquely marked message from inside a <c>.Transacted()</c> block that then fails; returns what reached
    /// the shared queue during the receive window.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> SendFromAFailedBlock(string body, string? producerParams)
    {
        const string queue = "DEV.QUEUE.2";
        var epProd = CreateEndpoint(queue, producerParams);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var failed = () => InTransaction(new Exchange(new Message(body)), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=2000");
        var received = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                return Task.CompletedTask;
            });
        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(5_000);
        await consumer.Stop();
        await epCons.Stop();
        return received;
    }

    [Fact]
    public async Task Producer_without_transacted_joins_the_block_and_sends_nothing_when_it_fails()
    {
        var body = $"join-{Guid.NewGuid():N}";

        var received = await SendFromAFailedBlock(body, producerParams: null);

        received.Should().NotContain(body, "inside .Transacted() a send joins the transaction unless transacted=false opts out");
    }

    [Fact]
    public async Task Producer_with_transacted_false_sends_at_once_even_from_a_failed_block()
    {
        var body = $"optout-{Guid.NewGuid():N}";

        var received = await SendFromAFailedBlock(body, "transacted=false");

        received.Should().Contain(body);
    }

    [Fact]
    public async Task Deferred_puts_of_one_producer_commit_together_or_not_at_all()
    {
        const string queue = "DEV.QUEUE.2";
        var first = $"batch-first-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        // Two puts in one block; the second is larger than the queue and channel accept, so it fails at the commit.
        var commitFailed = () => InTransaction(new Exchange(new Message(first)), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            ex.In.Body = new string('x', 5 * 1024 * 1024);
            await producer.Process(ex, ct);
        });
        await commitFailed.Should().ThrowAsync<Exception>();
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=2000");
        var received = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                return Task.CompletedTask;
            });
        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(5_000);
        await consumer.Stop();
        await epCons.Stop();

        received.Should().NotContain(first, "the puts of one producer in a block commit in one unit of work or not at all");
    }

    [Fact]
    public void Request_reply_producer_refuses_transacted_true()
    {
        var endpoint = CreateEndpoint("DEV.QUEUE.1", "replyTo=true&transacted=true");

        var act = () => endpoint.CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage("*replyTo*transacted*");
    }

    // ───── Multiple messages roundtrip ─────

    [Fact]
    public async Task Roundtrip_MultipleMessages()
    {
        var queue = "DEV.QUEUE.3";
        const int messageCount = 10;

        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                if (received.Count >= messageCount) allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
        for (int i = 0; i < messageCount; i++)
            received.Should().Contain($"msg-{i}");
    }

    // ───── RPC request/reply ─────

    [Fact]
    public async Task Rpc_RequestReply_ReturnsResponse()
    {
        var queue = "DEV.QUEUE.4";

        // Server: consumer echoes back
        var epServer = CreateEndpoint(queue);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = ex.In.Body?.ToString() ?? "";
                ex.Out = new Message($"ECHO:{body}");
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epServer.CreateConsumer(processor);
        await consumer.Start();

        // Client: RPC call
        var epClient = CreateEndpoint(queue, "replyTo=true&timeout=30");
        var rpcProducer = (IbmMqProducer)epClient.CreateProducer();
        await rpcProducer.Start();

        var exchange = new Exchange(new Message("ping"));
        await rpcProducer.Process(exchange);

        await rpcProducer.Stop();
        await epClient.Stop();
        await consumer.Stop();
        await epServer.Stop();

        exchange.HasOut.Should().BeTrue();
        exchange.Out!.Body?.ToString().Should().Be("ECHO:ping");
    }

    // ───── Concurrent consumers ─────

    [Fact]
    public async Task ConcurrentConsumers_ProcessInParallel()
    {
        var queue = "DEV.QUEUE.5";
        const int messageCount = 20;

        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"concurrent-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "concurrentConsumers=4&waitInterval=5000");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                await Task.Delay(50); // simulate work
                if (received.Count >= messageCount) allReceived.TrySetResult();
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
    }

    // ───── Persistent message ─────

    [Fact]
    public async Task PersistentMessage_SurvivesAndDelivers()
    {
        var queue = "DEV.QUEUE.1";

        var epProd = CreateEndpoint(queue, "persistence=Persistent");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("persistent-msg"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Body?.ToString().Should().Be("persistent-msg");
    }

    // ───── Transacted consumer ─────

    [Fact]
    public async Task TransactedConsumer_CommitsOnSuccess()
    {
        var queue = "DEV.QUEUE.2";

        // Send a message normally
        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("tx-consumer-msg")));
        await producer.Stop();
        await epProd.Stop();

        // Consume with transacted=true
        var epCons = CreateEndpoint(queue, "transacted=true&waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));

        // Wait for ProcessedCount to increment
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (consumer.ProcessedCount < 1 && sw.ElapsedMilliseconds < 3_000)
            await Task.Delay(50);

        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Body?.ToString().Should().Be("tx-consumer-msg");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ───── Topic pub/sub ─────

    [Fact]
    public async Task Topic_PublishSubscribe()
    {
        var topic = "DEV/TEST/EVENTS";

        // Start subscriber first
        var epSub = CreateEndpoint(topic, "destinationType=Topic&waitInterval=5000");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epSub.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(2000); // let subscription establish

        // Publish
        var epPub = CreateEndpoint(topic, "destinationType=Topic");
        var producer = (IbmMqProducer)epPub.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("topic-event"));
        await producer.Process(exchange);
        await producer.Stop();
        await epPub.Stop();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epSub.Stop();

        received.Should().Contain("topic-event");
    }

    // ───── Binary payload ─────

    [Fact]
    public async Task BinaryPayload_RoundTrip()
    {
        var queue = "DEV.QUEUE.4";

        var epProd = CreateEndpoint(queue, "targetClient=Mq");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var payload = new byte[] { 0x01, 0x02, 0x03, 0xFE, 0xFF };
        await producer.Process(new Exchange(new Message(payload)));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Body.Should().BeOfType<byte[]>();
        ((byte[])captured.In.Body!).Should().BeEquivalentTo(payload);
    }

    // ───── Expression support: Priority + Expiry from exchange headers ─────

    [Fact]
    public async Task Producer_ExpressionPriorityAndExpiry_ResolvedOnSend()
    {
        var queue = "DEV.QUEUE.1";

        // Producer with expression-based priority and expiry
        var epProd = CreateEndpoint(queue, "priorityExpression=${header.prio}&expiryExpression=${header.exp}");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("expression-mqmd");
        msg.Headers["prio"] = "7";
        msg.Headers["exp"] = "3000";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Receive and verify MQMD fields
        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Body?.ToString().Should().Be("expression-mqmd");
        captured.In.Headers[IbmMqHeaders.Priority].Should().Be(7);
        captured.In.Headers[IbmMqHeaders.Expiry].Should().BeOfType<int>()
            .Which.Should().BeGreaterThanOrEqualTo(1); // Expiry ticks down from 3000
    }

    // ───── Expression support: Persistence from exchange headers ─────

    [Fact]
    public async Task Producer_ExpressionPersistence_ResolvedOnSend()
    {
        var queue = "DEV.QUEUE.2";

        // Producer with expression-based persistence (by enum name)
        var epProd = CreateEndpoint(queue, "persistenceExpression=${header.pers}");
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("expression-persist");
        msg.Headers["pers"] = "Persistent";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Receive and verify MQMD persistence
        var epCons = CreateEndpoint(queue, "waitInterval=5000");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                captured = callInfo.Arg<IExchange>();
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Body?.ToString().Should().Be("expression-persist");
        // MQC.MQPER_PERSISTENT = 1
        captured.In.Headers[IbmMqHeaders.Persistence].Should().Be(1);
    }

    // ── Часть B плана KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN: targetClient ──

    [Fact]
    public async Task TargetClientMq_SendsRawMessageWithoutProperties()
    {
        // targetClient=Mq: a legacy MQ app expects raw MQMD+body. Message properties are what
        // materializes as MQRFH2 on property-aware paths - in Mq mode none may be written.
        // The option was declared and read by nothing.
        var queue = "DEV.QUEUE.2";
        var marker = Guid.NewGuid().ToString("N");

        var endpoint = CreateEndpoint(queue, "targetClient=Mq");
        var producer = (IbmMqProducer)endpoint.CreateProducer();
        await producer.Start();
        var msg = new Message(marker);
        msg.Headers["X-Custom"] = "must-not-travel";
        await producer.Process(new Exchange(msg));
        await producer.Stop();

        // Raw read via MQ classes: the message must carry NO custom properties.
        var props = new System.Collections.Hashtable
        {
            [IBM.WMQ.MQC.HOST_NAME_PROPERTY] = Host,
            [IBM.WMQ.MQC.PORT_PROPERTY] = Port,
            [IBM.WMQ.MQC.CHANNEL_PROPERTY] = Channel,
            [IBM.WMQ.MQC.USER_ID_PROPERTY] = User,
            [IBM.WMQ.MQC.PASSWORD_PROPERTY] = Password,
            [IBM.WMQ.MQC.TRANSPORT_PROPERTY] = IBM.WMQ.MQC.TRANSPORT_MQSERIES_MANAGED,
        };
        using var qm = new IBM.WMQ.MQQueueManager(QueueManager, props);
        var q = qm.AccessQueue(queue, IBM.WMQ.MQC.MQOO_INPUT_SHARED | IBM.WMQ.MQC.MQOO_FAIL_IF_QUIESCING);
        try
        {
            var raw = new IBM.WMQ.MQMessage();
            var gmo = new IBM.WMQ.MQGetMessageOptions { Options = IBM.WMQ.MQC.MQGMO_WAIT, WaitInterval = 10000 };
            string? body = null;
            while (body != marker)
            {
                raw = new IBM.WMQ.MQMessage();
                q.Get(raw, gmo);
                body = raw.ReadString(raw.MessageLength);
            }

            var catalogue = (string?)null;
            try { catalogue = raw.GetStringProperty(IbmMqHeaders.HeaderCatalogue); }
            catch (IBM.WMQ.MQException) { /* property absent - exactly what Mq mode promises */ }

            catalogue.Should().BeNull(
                "targetClient=Mq обязан отдавать голый MQMD+тело без свойств сообщения");
        }
        finally
        {
            q.Close();
            qm.Disconnect();
        }
    }
}
