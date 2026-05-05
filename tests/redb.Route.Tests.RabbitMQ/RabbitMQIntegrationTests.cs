using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
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

    [Fact]
    public async Task TransactedProducer_Commit_DeliversMessage()
    {
        var queue = $"test-tx-{Guid.NewGuid():N}";
        var epProd = CreateEndpoint(queue, "transacted=true");
        var producer = (RabbitMQProducer)epProd.CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("transacted"));
        await producer.Process(exchange);

        exchange.Properties.Should().ContainKey("TRANSACT_ACTION");
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        actions!.Should().NotBeEmpty();

        // Commit — message should be published
        foreach (var action in actions.Values)
            await action.Commit();

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
        await producer.Process(exchange);

        // Rollback instead of commit
        var actions = exchange.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        foreach (var action in actions!.Values)
            await action.Rollback();

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
