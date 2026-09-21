using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Transactions;
using redb.Route.Processors;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// Integration tests against a real RabbitMQ instance.
/// Expects RabbitMQ at localhost:5672 (guest/guest).
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 5672;
    private readonly ITestOutputHelper _output;

    public RabbitMQIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private RabbitMQEndpoint CreateEndpoint(string queue, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username=admin&password=admin&declare=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"rabbitmq://{queue}?{qs}");
        var component = new RabbitMQComponent();
        return (RabbitMQEndpoint)component.CreateEndpoint(uri);
    }

    // ───── Tests ─────

    [Fact]
    public async Task Producer_SendsMessage_ConsumerReceives()
    {
        var queue = $"test-{Guid.NewGuid():N}";
        _output.WriteLine($"Queue: {queue}");

        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello RabbitMQ"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Consume via Route consumer
        var epCons = CreateEndpoint(queue);
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

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("Hello RabbitMQ");
    }

    [Fact]
    public async Task Consumer_SetsRabbitMQMetadataHeaders()
    {
        var queue = $"test-meta-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("meta-test"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);
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

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers.Should().ContainKey(RmqHeaders.DeliveryTag);
        capturedExchange.In.Headers.Should().ContainKey(RmqHeaders.RoutingKey);
        capturedExchange.In.Headers.Should().ContainKey(RmqHeaders.ConsumerTag);
    }

    [Fact]
    public async Task Producer_ForwardsCustomHeaders()
    {
        var queue = $"test-hdr-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("hdr-data");
        msg.Headers["X-Custom-Id"] = "12345";
        msg.Headers["X-Trace"] = "abc";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);
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

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("X-Custom-Id");
        captured.In.Headers["X-Custom-Id"].Should().Be("12345");
        captured.In.Headers.Should().ContainKey("X-Trace");
    }

    /// <summary>Runs <paramref name="body"/> inside a transacted block, as <c>.Transacted()</c> on a route does.</summary>
    private static Task InTransaction(IExchange exchange, Func<IExchange, CancellationToken, Task> body) =>
        new TransactedProcessor(new DelegateProcessor(body), new TransactionPolicy()).Process(exchange);

    [Fact]
    public async Task TransactedProducer_Commit_DeliversMessage()
    {
        var queue = $"test-tx-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue, "transacted=true");
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted"));
        await InTransaction(exchange, (ex, ct) => producer.Process(ex, ct));   // the send is committed as the block ends

        await producer.Stop();
        await epProd.Stop();

        // Verify the message was delivered
        var epCons = CreateEndpoint(queue);
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("transacted");
    }

    [Fact]
    public async Task TransactedProducer_Rollback_MessageNotDelivered()
    {
        var queue = $"test-rollback-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue, "transacted=true");
        var producer = (RabbitMQProducer)epProd.CreateProducer();
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

        // Try to consume — should timeout with no message
        var epCons = CreateEndpoint(queue);
        var received = new ConcurrentBag<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(3000);
        await consumer.Stop();
        await epCons.Stop();

        received.Should().BeEmpty("message was rolled back, not committed");
    }

    /// <summary>Sends one message from inside a <c>.Transacted()</c> block that then fails; returns what reached the queue.</summary>
    private async Task<IReadOnlyCollection<string>> SendFromAFailedBlock(string queue, string? producerParams)
    {
        var epProd = CreateEndpoint(queue, producerParams);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var failed = () => InTransaction(new Exchange(new Message("sent-from-a-failed-block")), async (ex, ct) =>
        {
            await producer.Process(ex, ct);
            throw new InvalidOperationException("the unit of work failed");
        });
        await failed.Should().ThrowAsync<InvalidOperationException>();
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);
        var received = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });
        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(3000);
        await consumer.Stop();
        await epCons.Stop();
        return received;
    }

    [Fact]
    public async Task Producer_without_transacted_joins_the_block_and_sends_nothing_when_it_fails()
    {
        var received = await SendFromAFailedBlock($"test-join-{Guid.NewGuid():N}", producerParams: null);

        received.Should().BeEmpty("inside .Transacted() a send joins the transaction unless transacted=false opts out");
    }

    [Fact]
    public async Task Producer_with_transacted_false_sends_at_once_even_from_a_failed_block()
    {
        var received = await SendFromAFailedBlock($"test-optout-{Guid.NewGuid():N}", "transacted=false");

        received.Should().ContainSingle().Which.Should().Be("sent-from-a-failed-block");
    }

    [Fact]
    public async Task Deferred_publishes_of_one_producer_commit_together_or_not_at_all()
    {
        var queue = $"test-batch-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue, "routingKey=${header.rk}");
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("batch-first"));
        exchange.In.Headers["rk"] = queue;
        var commitFailed = () => InTransaction(exchange, async (ex, ct) =>
        {
            await producer.Process(ex, ct);                           // routed to the queue
            ex.In.Headers["rk"] = new string('k', 300);               // an AMQP short string holds 255 bytes: this publish fails
            ex.In.Body = "batch-second";
            await producer.Process(ex, ct);
        });
        await commitFailed.Should().ThrowAsync<Exception>();
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);
        var received = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });
        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(3000);
        await consumer.Stop();
        await epCons.Stop();

        received.Should().BeEmpty("the publishes of one producer in a block commit in one channel transaction or not at all");
    }

    [Fact]
    public async Task Consumer_does_not_acknowledge_a_rolled_back_unit_of_work()
    {
        var queue = $"test-rbonly-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("rolled-back")));
        await producer.Stop();
        await epProd.Stop();

        // What .RollbackAll() does inside .Transacted(): mark the unit of work rollback-only and stop the route.
        var deliveries = 0;
        var deliveredAgain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var route = new TransactedProcessor(new DelegateProcessor(ex =>
        {
            if (Interlocked.Increment(ref deliveries) >= 2) deliveredAgain.TrySetResult();
            ex.MarkRollbackOnly();
            ex.Stop();
        }), new TransactionPolicy());

        var epCons = CreateEndpoint(queue);
        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(route);
        await consumer.Start();
        await Task.WhenAny(deliveredAgain.Task, Task.Delay(10_000));
        await consumer.Stop();
        await epCons.Stop();

        deliveries.Should().BeGreaterThanOrEqualTo(2,
            "a rolled-back unit of work is not acknowledged, so the broker delivers the message again");
    }

    [Fact]
    public void Request_reply_producer_refuses_transacted_true()
    {
        var endpoint = CreateEndpoint($"test-rpc-{Guid.NewGuid():N}", "replyTo=true&transacted=true");

        var act = () => endpoint.CreateProducer();

        act.Should().Throw<InvalidOperationException>().WithMessage("*replyTo*transacted*");
    }

    [Fact]
    public async Task WithExchange_TopicRouting_RoutesCorrectly()
    {
        var exchangeName = $"ex-{Guid.NewGuid():N}";
        var queue = $"q-{Guid.NewGuid():N}";

        var epProd = CreateEndpoint(queue,
            $"exchange={exchangeName}&exchangeType=topic&routingKey=order.created");
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("order-data"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue,
            $"exchange={exchangeName}&exchangeType=topic&routingKey=order.*");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("order-data");
    }

    [Fact]
    public async Task Roundtrip_MultipleMessages()
    {
        var queue = $"test-rt-{Guid.NewGuid():N}";
        const int messageCount = 10;

        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                if (received.Count >= messageCount) allReceived.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
        for (int i = 0; i < messageCount; i++)
            received.Should().Contain($"msg-{i}");
    }

    [Fact]
    public async Task TransactedConsumer_CommitsOnSuccess()
    {
        var queue = $"test-txcons-{Guid.NewGuid():N}";

        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("tx-consumer-msg")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue, "transacted=true");
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

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])captured!.In.Body!).Should().Be("tx-consumer-msg");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Simulates what a route-level <c>.Transacted()</c> does at its boundary: commit every deferred
    /// <see cref="ITransactedAction"/> registered on the exchange (which includes the RabbitMQ ack)
    /// — DURING Process, i.e. before the consumer's own post-process settle runs.
    /// </summary>
    private static async Task SimulateRouteTransactionAsync(
        IExchange ex, ConcurrentBag<string> received, TaskCompletionSource tcs, int expected)
    {
        received.Add(Encoding.UTF8.GetString((byte[])ex.In.Body!));
        if (ex.Properties.TryGetValue("TRANSACT_ACTION", out var o) &&
            o is ConcurrentDictionary<string, ITransactedAction> acts)
        {
            foreach (var a in acts.Values)
                await a.Commit();
        }
        if (received.Count >= expected) tcs.TrySetResult();
    }

    [Fact]
    public async Task Consumer_RouteTransactionAcksDuringProcess_NoDoubleAck()
    {
        // Reproduces the double-BasicAck: a route-level .Transacted() commits the deferred ack
        // (BasicAck #1) during Process, then the consumer's inline settle would BasicAck the SAME
        // delivery tag again (BasicAck #2). The broker rejects the second with PRECONDITION_FAILED
        // and tears down the whole channel, so the SECOND message never gets processed.
        // With the Settled guard, the inline settle is skipped → channel survives → both processed.
        var queue = $"test-double-ack-{Guid.NewGuid():N}";

        var epProd = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("msg-0")));
        await producer.Process(new Exchange(new Message("msg-1")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(queue);   // NOT a ?transacted=true endpoint
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => SimulateRouteTransactionAsync(ci.Arg<IExchange>(), received, tcs, expected: 2));

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("msg-0");
        received.Should().Contain("msg-1", "the channel must survive the first ack (no double-ack tear-down)");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Rpc_RequestReply_ReturnsResponse()
    {
        var queue = $"test-rpc-{Guid.NewGuid():N}";

        // Start a "server" consumer that echoes back with a prefix
        var epServer = CreateEndpoint(queue);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                ex.Out = new Message($"ECHO:{body}");
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epServer.CreateConsumer(processor);
        await consumer.Start();

        // RPC client
        var epClient = CreateEndpoint(queue, "replyTo=true&timeout=15");
        var rpcProducer = (RabbitMQProducer)epClient.CreateProducer();
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
}
