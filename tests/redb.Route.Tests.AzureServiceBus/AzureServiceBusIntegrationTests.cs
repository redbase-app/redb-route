// Integration tests run on a single TFM to avoid cross-TFM interference
// (all TFMs share the same emulator queue and can steal each other's messages).
#if NET9_0

using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;
using Xunit.Abstractions;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// Integration tests against Azure Service Bus Emulator.
/// Expects the emulator at localhost:5300 with pre-created entities:
///   - queue.1 (no sessions, MaxDeliveryCount=3)
///   - topic.1 with subscription.3 (no filter rules)
/// Each test uses unique message tags to prevent cross-TFM interference
/// when dotnet test runs all target frameworks in parallel.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureServiceBusIntegrationTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Queue = "queue.1";
    private const string Topic = "topic.1";
    private const string Subscription = "subscription.3";

    private readonly ITestOutputHelper _output;

    public AzureServiceBusIntegrationTests(ITestOutputHelper output) => _output = output;

    // ───── Helpers ─────

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private AzureServiceBusEndpoint CreateEndpoint(string entity, string? extraParams = null)
    {
        var qs = $"connectionString={ConnectionString}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"asb://{entity}?{qs}");
        return (AzureServiceBusEndpoint)new AzureServiceBusComponent().CreateEndpoint(uri);
    }

    /// <summary>Drains leftover messages from an entity (best-effort cleanup).</summary>
    private async Task DrainAsync(string entity, string? extraParams = null)
    {
        var ep = CreateEndpoint(entity,
            $"receiveMode=ReceiveAndDelete&maxConcurrentCalls=10{(extraParams is not null ? $"&{extraParams}" : "")}");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var consumer = ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(2000);
        await consumer.Stop();
        await ep.Stop();
    }

    // ───── Queue: Basic roundtrip ─────

    [Fact]
    public async Task Producer_SendsMessage_ConsumerReceives()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"roundtrip-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue);
        var tcs = new TaskCompletionSource<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) tcs.TrySetResult(body);
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        completed.Should().Be(tcs.Task, "message should be received within timeout");
        tcs.Task.Result.Should().Contain(tag);
    }

    // ───── Queue: Metadata headers ─────

    [Fact]
    public async Task Consumer_SetsAsbMetadataHeaders()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();

        var msg = new Message($"meta-{tag}");
        msg.Headers[AzureServiceBusHeaders.CorrelationId] = "corr-123";
        msg.Headers[AzureServiceBusHeaders.Subject] = "order.created";
        await producer.Process(new Exchange(msg));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue);
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    captured = ex;
                    tcs.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey(AzureServiceBusHeaders.MessageId);
        captured.In.Headers.Should().ContainKey(AzureServiceBusHeaders.SequenceNumber);
        captured.In.Headers.Should().ContainKey(AzureServiceBusHeaders.EnqueuedTime);
        captured.In.Headers.Should().ContainKey(AzureServiceBusHeaders.DeliveryCount);
        captured.In.Headers[AzureServiceBusHeaders.CorrelationId].Should().Be("corr-123");
        captured.In.Headers[AzureServiceBusHeaders.Subject].Should().Be("order.created");
    }

    // ───── Queue: Custom application properties ─────

    [Fact]
    public async Task Producer_ForwardsCustomHeaders()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();

        var msg = new Message($"hdr-{tag}");
        msg.Headers["X-Custom-Id"] = "12345";
        msg.Headers["X-Trace"] = "abc";
        await producer.Process(new Exchange(msg));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue);
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    captured = ex;
                    tcs.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("X-Custom-Id");
        captured.In.Headers["X-Custom-Id"].Should().Be("12345");
        captured.In.Headers.Should().ContainKey("X-Trace");
        captured.In.Headers["X-Trace"].Should().Be("abc");
    }

    // ───── Queue: ReceiveAndDelete mode ─────

    [Fact]
    public async Task Consumer_ReceiveAndDelete_ReceivesMessage()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"rad-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue, "receiveMode=ReceiveAndDelete");
        var tcs = new TaskCompletionSource<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) tcs.TrySetResult(body);
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        completed.Should().Be(tcs.Task);
        tcs.Task.Result.Should().Contain(tag);
    }

    // ───── Queue: PeekLock auto dead-letter ─────

    [Fact]
    public async Task Consumer_AutoDeadLetter_MovesToDeadLetterQueue()
    {
        await DrainAsync(Queue);
        await DrainAsync(Queue, "subQueue=deadletter");
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"dl-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        // Consumer that sets an exception on our message → triggers dead-letter
        var epCons = CreateEndpoint(Queue, "autoDeadLetter=true&deadLetterReason=TestError");
        var processedCount = 0;

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    ex.Exception = new InvalidOperationException("Simulated error");
                    Interlocked.Increment(ref processedCount);
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        // Wait enough for processing + dead-letter
        await Task.Delay(5000);
        await consumer.Stop();
        await epCons.Stop();

        processedCount.Should().BeGreaterThanOrEqualTo(1, "our tagged message should have been processed");

        // Read from dead-letter sub-queue
        var epDlq = CreateEndpoint(Queue, "subQueue=deadletter");
        var tcs = new TaskCompletionSource<string>();

        var dlqProcessor = Substitute.For<IProcessor>();
        dlqProcessor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) tcs.TrySetResult(body);
                return Task.CompletedTask;
            });

        var dlqConsumer = epDlq.CreateConsumer(dlqProcessor);
        await dlqConsumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await dlqConsumer.Stop();
        await epDlq.Stop();

        tcs.Task.IsCompletedSuccessfully.Should().BeTrue("message should appear in DLQ");
    }

    // ───── Queue: Transacted consumer — commit ─────

    [Fact]
    public async Task TransactedConsumer_Commit_CompletesMessage()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"tx-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue, "transacted=true");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    captured = ex;
                    tcs.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));

        captured.Should().NotBeNull();
        captured!.Properties.Should().ContainKey("TRANSACT_ACTION");

        var actions = captured.Properties["TRANSACT_ACTION"] as ConcurrentDictionary<string, ITransactedAction>;
        actions.Should().NotBeNull();
        actions!.Should().NotBeEmpty();

        // Commit — message should be completed
        foreach (var action in actions.Values)
            await action.Commit();

        await consumer.Stop();
        await epCons.Stop();

        // Verify our specific message is gone (no re-delivery)
        var epVerify = CreateEndpoint(Queue, "receiveMode=ReceiveAndDelete");
        var foundOurs = false;

        var verifyProcessor = Substitute.For<IProcessor>();
        verifyProcessor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) foundOurs = true;
                return Task.CompletedTask;
            });

        var verifyConsumer = epVerify.CreateConsumer(verifyProcessor);
        await verifyConsumer.Start();
        await Task.Delay(3000);
        await verifyConsumer.Stop();
        await epVerify.Stop();

        foundOurs.Should().BeFalse("committed message should not be re-delivered");
    }

    // ───── Queue: Transacted consumer — rollback ─────

    [Fact]
    public async Task TransactedConsumer_Rollback_DoesNotThrow()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"rb-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue, "transacted=true");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    captured = ex;

                    // Rollback inside callback while ProcessMessageEventArgs is still valid
                    var actions = captured.Properties["TRANSACT_ACTION"]
                        as ConcurrentDictionary<string, ITransactedAction>;
                    foreach (var act in actions!.Values)
                        act.Rollback().GetAwaiter().GetResult();

                    tcs.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.Properties.Should().ContainKey("TRANSACT_ACTION");
    }

    // ───── Queue: Multiple messages ─────

    [Fact]
    public async Task Roundtrip_MultipleMessages()
    {
        await DrainAsync(Queue);
        var tag = Tag();
        const int messageCount = 10;

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();

        for (int i = 0; i < messageCount; i++)
            await producer.Process(new Exchange(new Message($"multi-{tag}-{i}")));

        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue, "maxConcurrentCalls=5");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag))
                {
                    received.Add(body);
                    if (received.Count >= messageCount) allReceived.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(30_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().BeGreaterThanOrEqualTo(messageCount);
        for (int i = 0; i < messageCount; i++)
            received.Should().Contain($"multi-{tag}-{i}");
    }

    // ───── Topic/Subscription: Roundtrip ─────

    [Fact]
    public async Task Topic_Subscription_Roundtrip()
    {
        await DrainAsync(Topic, $"subscriptionName={Subscription}");
        var tag = Tag();

        var epProd = CreateEndpoint(Topic);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"topic-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Topic, $"subscriptionName={Subscription}");
        var tcs = new TaskCompletionSource<string>();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) tcs.TrySetResult(body);
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        tcs.Task.IsCompletedSuccessfully.Should().BeTrue();
        tcs.Task.Result.Should().Contain(tag);
    }

    // ───── Batch: Send multiple ─────

    [Fact]
    public async Task Batch_SendMultiple_ConsumerReceivesAll()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue, "enableBatch=true&batchMaxMessages=10");
        var producer = epProd.CreateProducer();
        await producer.Start();

        var items = Enumerable.Range(0, 5).Select(i => $"batch-{tag}-{i}").ToList();
        var exchange = new Exchange(new Message(items));
        await producer.Process(exchange);

        exchange.In.Headers.Should().ContainKey(AzureServiceBusHeaders.BatchMessageCount);
        ((int)exchange.In.Headers[AzureServiceBusHeaders.BatchMessageCount]!).Should().Be(5);

        await producer.Stop();
        await epProd.Stop();

        // Consume
        var epCons = CreateEndpoint(Queue, "maxConcurrentCalls=5");
        var received = new ConcurrentBag<string>();
        var allReceived = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag))
                {
                    received.Add(body);
                    if (received.Count >= 5) allReceived.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(allReceived.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Count.Should().Be(5);
    }

    // ───── Producer: MessageId preserved ─────

    [Fact]
    public async Task Producer_SetsMessageId_ConsumerReceivesIt()
    {
        await DrainAsync(Queue);
        var tag = Tag();
        var customId = $"custom-{tag}";

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();

        var msg = new Message($"id-{tag}");
        msg.Headers[AzureServiceBusHeaders.MessageId] = customId;
        await producer.Process(new Exchange(msg));
        await producer.Stop();
        await epProd.Stop();

        var epCons = CreateEndpoint(Queue);
        IExchange? captured = null;
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                var body = Encoding.UTF8.GetString((byte[])ex.In.Body!);
                if (body.Contains(tag))
                {
                    captured = ex;
                    tcs.TrySetResult();
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        captured.Should().NotBeNull();
        captured!.In.Headers[AzureServiceBusHeaders.MessageId].Should().Be(customId);
    }

    // ───── Consumer: PeekLock completes on successful processing ─────

    [Fact]
    public async Task Consumer_PeekLock_CompletesOnSuccess()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"pl-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        // First consumer — PeekLock, processes successfully → should complete
        var epCons = CreateEndpoint(Queue);
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        tcs.Task.IsCompletedSuccessfully.Should().BeTrue("message should be processed");

        // Verify our specific message is not re-delivered
        var epVerify = CreateEndpoint(Queue, "receiveMode=ReceiveAndDelete");
        var foundOurs = false;

        var verifyProcessor = Substitute.For<IProcessor>();
        verifyProcessor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) foundOurs = true;
                return Task.CompletedTask;
            });

        var verifyConsumer = epVerify.CreateConsumer(verifyProcessor);
        await verifyConsumer.Start();
        await Task.Delay(3000);
        await verifyConsumer.Stop();
        await epVerify.Stop();

        foundOurs.Should().BeFalse("completed message should not be re-delivered");
    }

    // ───── Consumer: Processing error abandons message ─────

    [Fact]
    public async Task Consumer_ProcessingError_AbandonsMessage()
    {
        await DrainAsync(Queue);
        var tag = Tag();

        var epProd = CreateEndpoint(Queue);
        var producer = epProd.CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message($"err-{tag}")));
        await producer.Stop();
        await epProd.Stop();

        // Consumer that throws on our message — should be abandoned
        var epCons = CreateEndpoint(Queue);
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns<Task>(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag))
                {
                    tcs.TrySetResult();
                    throw new InvalidOperationException("simulated failure");
                }
                return Task.CompletedTask;
            });

        var consumer = epCons.CreateConsumer(processor);
        await consumer.Start();

        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        // Message should be re-delivered (abandoned, not completed)
        var epRetry = CreateEndpoint(Queue, "receiveMode=ReceiveAndDelete");
        var retryTcs = new TaskCompletionSource<string>();

        var retryProcessor = Substitute.For<IProcessor>();
        retryProcessor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var body = Encoding.UTF8.GetString((byte[])callInfo.Arg<IExchange>().In.Body!);
                if (body.Contains(tag)) retryTcs.TrySetResult(body);
                return Task.CompletedTask;
            });

        var retryConsumer = epRetry.CreateConsumer(retryProcessor);
        await retryConsumer.Start();

        await Task.WhenAny(retryTcs.Task, Task.Delay(15_000));
        await retryConsumer.Stop();
        await epRetry.Stop();

        retryTcs.Task.IsCompletedSuccessfully.Should().BeTrue("abandoned message should be re-delivered");
        retryTcs.Task.Result.Should().Contain(tag);
    }
}

#endif

