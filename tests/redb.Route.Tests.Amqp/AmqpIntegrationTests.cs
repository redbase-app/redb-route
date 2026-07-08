using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;
using Xunit.Abstractions;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// Integration tests against a real AMQP 1.0 broker (ActiveMQ Artemis).
/// Expects Artemis at localhost:5673 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpIntegrationTests
{
    private const string Host = "localhost";
    private const int Port = 5673;
    private const string User = "admin";
    private const string Password = "admin";
    private readonly ITestOutputHelper _output;

    public AmqpIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private AmqpEndpoint CreateEndpoint(string address, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"amqp://{address}?{qs}");
        var component = new AmqpComponent();
        return (AmqpEndpoint)component.CreateEndpoint(uri);
    }

    // ───── Tests ─────

    [Fact]
    public async Task Producer_SendsMessage_ConsumerReceives()
    {
        var address = $"test.{Guid.NewGuid():N}";
        _output.WriteLine($"Address: {address}");

        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("Hello AMQP"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Consume
        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("Hello AMQP");
    }

    [Fact]
    public async Task Consumer_SetsAmqpMetadataHeaders()
    {
        var address = $"test.meta.{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("meta-test"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        capturedExchange.Should().NotBeNull();
        capturedExchange!.In.Headers.Should().ContainKey(AmqpHeaders.Address);
        capturedExchange.In.Headers.Should().ContainKey(AmqpHeaders.MessageId);
        capturedExchange.In.Headers.Should().ContainKey(AmqpHeaders.Durable);
        capturedExchange.In.Headers.Should().ContainKey(AmqpHeaders.Priority);
    }

    [Fact]
    public async Task Producer_ForwardsCustomHeaders()
    {
        var address = $"test.hdr.{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("hdr-data");
        msg.Headers["X-Custom-Id"] = "12345";
        msg.Headers["X-Trace"] = "abc";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("X-Custom-Id");
        captured.In.Headers["X-Custom-Id"].Should().Be("12345");
        captured.In.Headers.Should().ContainKey("X-Trace");
    }

    [Fact]
    public async Task TransactedProducer_Commit_DeliversMessage()
    {
        var address = $"test.tx.{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(address, "transacted=true");
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted"));
        await producer.Process(exchange);

        exchange.Properties.Should().ContainKey("TRANSACT_ACTION");
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        actions!.Should().NotBeEmpty();

        // Commit
        foreach (var action in actions.Values)
            await action.Commit();

        await producer.Stop();
        await epProd.Stop();

        // Verify delivered
        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("transacted");
    }

    [Fact]
    public async Task TransactedProducer_Rollback_MessageNotDelivered()
    {
        var address = $"test.rollback.{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(address, "transacted=true");
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("should-not-arrive"));
        await producer.Process(exchange);

        // Rollback
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var action in actions!.Values)
            await action.Rollback();

        await producer.Stop();
        await epProd.Stop();

        // Should timeout with no message
        var epCons = CreateEndpoint(address);
        var received = new ConcurrentBag<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(3000);
        await consumer.Stop();
        await epCons.Stop();

        received.Should().BeEmpty("message was rolled back, not committed");
    }

    [Fact]
    public async Task Roundtrip_MultipleMessages()
    {
        var address = $"test.rt.{Guid.NewGuid():N}";
        const int messageCount = 10;

        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"msg-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
        for (int i = 0; i < messageCount; i++)
            received.Should().Contain($"msg-{i}");
    }

    [Fact]
    public async Task Rpc_RequestReply_ReturnsResponse()
    {
        var address = $"test.rpc.{Guid.NewGuid():N}";

        // Start a "server" consumer that echoes back with a prefix
        var epServer = CreateEndpoint(address);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                ex.Out = new Message($"ECHO:{body}");
                return Task.CompletedTask;
            });

        var consumer = (AmqpConsumer)epServer.CreateConsumer(processor);
        await consumer.Start();

        // RPC client
        var epClient = CreateEndpoint(address, "replyTo=true&timeout=15");
        var rpcProducer = (AmqpProducer)epClient.CreateProducer();
        await rpcProducer.Start();

        var exchange = new Exchange(new Message("ping"));
        await rpcProducer.Process(exchange);

        await rpcProducer.Stop();
        await epClient.Stop();
        await consumer.Stop();
        await epServer.Stop();

        exchange.HasOut.Should().BeTrue();
        Encoding.UTF8.GetString((byte[])exchange.Out!.Body!).Should().Be("ECHO:ping");
    }

    [Fact]
    public async Task ConcurrentConsumers_ProcessInParallel()
    {
        var address = $"test.concurrent.{Guid.NewGuid():N}";
        const int messageCount = 20;

        // Send messages
        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
        {
            var exchange = new Exchange(new Message($"concurrent-{i}"));
            await producer.Process(exchange);
        }
        await producer.Stop();
        await epProd.Stop();

        // Consume with concurrency=4
        var epCons = CreateEndpoint(address, "concurrentConsumers=4&credit=20");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!));
                await Task.Delay(50); // simulate work
                if (received.Count >= messageCount) allReceived.TrySetResult();
            });

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
    }

    [Fact]
    public async Task ProducerDefaults_DurableAndPriority_SetCorrectly()
    {
        var address = $"test.defaults.{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(address, "messageDurable=true&messagePriority=7");
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("priority-msg"));
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers[AmqpHeaders.Durable].Should().Be(true);
        captured.In.Headers[AmqpHeaders.Priority].Should().Be((byte)7);
    }

    [Fact]
    public async Task TransactedConsumer_CommitsOnSuccess()
    {
        var address = $"test.txcons.{Guid.NewGuid():N}";

        // Publish a message normally (non-transacted producer)
        var epProd = CreateEndpoint(address);
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("tx-consumer-msg")));
        await producer.Stop();
        await epProd.Stop();

        // Consume with transacted=true
        var epCons = CreateEndpoint(address, "transacted=true");
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));

        // Allow ProcessMessageAsync to fully complete (accept + increment counter)
        // after the mock fires tcs — there's a window between processor return and ProcessedCount++
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (consumer.ProcessedCount < 1 && sw.ElapsedMilliseconds < 3_000)
            await Task.Delay(50);

        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        Encoding.UTF8.GetString((byte[])captured!.In.Body!).Should().Be("tx-consumer-msg");
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Expression resolution roundtrip ─────────────────────────────

    [Fact]
    public async Task Producer_ExpressionSubjectAndPriority_ResolvedOnSend()
    {
        var address = $"test.expr.{Guid.NewGuid():N}";
        // Subject and priority come from expressions
        var epProd = CreateEndpoint(address,
            "subject=${header.subj}&messagePriorityExpression=${header.prio}");
        var producer = (AmqpProducer)epProd.CreateProducer();
        await producer.Start();

        var msg = new Message("expr-test");
        msg.Headers["subj"] = "order-42";
        msg.Headers["prio"] = "7";
        var exchange = new Exchange(msg);
        await producer.Process(exchange);
        await producer.Stop();
        await epProd.Stop();

        // Consume and verify resolved values
        var epCons = CreateEndpoint(address);
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers[AmqpHeaders.Subject].Should().Be("order-42");
        captured.In.Headers[AmqpHeaders.Priority].Should().BeOneOf((byte)7, (int)7);
    }
}
