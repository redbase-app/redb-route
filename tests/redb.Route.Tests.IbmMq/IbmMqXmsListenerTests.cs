using System.Collections.Concurrent;
using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;
using redb.Route.Telemetry;
using Xunit.Abstractions;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// Integration tests for the event-driven XMS <c>MessageListener</c> receive path
/// (<c>receiveMode=listener</c>) against a real IBM MQ broker.
/// Expects IBM MQ at localhost:1414, QM1, channel DEV.APP.SVRCONN, user app/admin.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqXmsListenerTests
{
    private const string Host = "localhost";
    private const int Port = 1414;
    private const string Channel = "DEV.APP.SVRCONN";
    private const string QueueManager = "QM1";
    private const string User = "app";
    private const string Password = "admin";

    private readonly ITestOutputHelper _output;

    public IbmMqXmsListenerTests(ITestOutputHelper output) => _output = output;

    private IbmMqEndpoint CreateEndpoint(string destination, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&channel={Channel}&queueManager={QueueManager}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"wmq:{destination}?{qs}");
        var component = new IbmMqComponent();
        return (IbmMqEndpoint)component.CreateEndpoint(uri);
    }

    [Fact]
    public async Task Listener_ReceivesMessage()
    {
        var queue = "DEV.QUEUE.1";

        var epCons = CreateEndpoint(queue, "receiveMode=listener");
        string? received = null;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                received = ex.In.Body?.ToString();
                tcs.TrySetResult(received ?? "");
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        try
        {
            // Publish AFTER the listener is up.
            await SendAsync(queue, "Hello XMS listener");

            await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        received.Should().Be("Hello XMS listener");
        consumer.ProcessedCount.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// The acceptance criterion for the fix: publish→receive latency must collapse from the
    /// poll-path floor (~250–500&#160;ms, the managed client's internal MQGET tick) to &lt;50&#160;ms.
    /// Measures several warm round-trips and takes the best to filter GC/scheduling jitter.
    /// </summary>
    [Fact]
    public async Task Listener_DeliveryLatency_IsUnder50ms()
    {
        var queue = "DEV.QUEUE.2";

        var epCons = CreateEndpoint(queue, "receiveMode=listener");
        TaskCompletionSource<long> current = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                current.TrySetResult(Stopwatch.GetTimestamp());
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        // Reuse a single warm producer so the measurement is pure publish→receive, not connect+publish.
        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();

        var samples = new List<double>();
        try
        {
            const int rounds = 5;
            for (var i = 0; i < rounds; i++)
            {
                current = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

                var sentAt = Stopwatch.GetTimestamp();
                await producer.Process(new Exchange(new Message($"latency-{i}")));

                var completed = await Task.WhenAny(current.Task, Task.Delay(20_000));
                completed.Should().Be(current.Task, "the XMS listener must deliver the message");

                var receivedAt = await current.Task;
                var ms = Stopwatch.GetElapsedTime(sentAt, receivedAt).TotalMilliseconds;
                samples.Add(ms);
                _output.WriteLine($"round {i}: {ms:F1} ms");

                // The first round warms the channel; ignore it in the assertion below.
            }
        }
        finally
        {
            await producer.Stop();
            await epProd.Stop();
            await consumer.Stop();
            await epCons.Stop();
        }

        // Warm rounds (skip the first) — the best must be well under the poll-path floor.
        var best = samples.Skip(1).Min();
        _output.WriteLine($"best warm latency: {best:F1} ms (poll-path floor is ~250–500 ms)");
        best.Should().BeLessThan(50, "event-driven XMS push should deliver in <50 ms");
    }

    /// <summary>
    /// Transacted listener, happy path: a message that processes cleanly is committed on the delivering
    /// session and is NOT redelivered.
    /// </summary>
    [Fact]
    public async Task Listener_Transacted_CommitsOnSuccess_NoRedelivery()
    {
        var queue = "DEV.QUEUE.3";
        await DrainQueueAsync(queue);

        var epCons = CreateEndpoint(queue, "receiveMode=listener&transacted=true");
        var deliveries = 0;
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref deliveries);
                firstSeen.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await SendAsync(queue, "commit-me");
            await Task.WhenAny(firstSeen.Task, Task.Delay(20_000));
            // Grace window: if the message were rolled back it would be redelivered here.
            await Task.Delay(1500);
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        Volatile.Read(ref deliveries).Should().Be(1, "a committed message must not be redelivered");
    }

    /// <summary>
    /// Transacted listener, failure path: a processing error rolls back the delivering session, so the
    /// broker redelivers the message. The processor throws on the first attempt and succeeds on the
    /// next — proving rollback → redelivery.
    /// </summary>
    [Fact]
    public async Task Listener_Transacted_RollsBackAndRedelivers_OnError()
    {
        var queue = "DEV.QUEUE.4";
        await DrainQueueAsync(queue);

        var epCons = CreateEndpoint(queue, "receiveMode=listener&transacted=true");
        var attempts = 0;
        var succeeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var n = Interlocked.Increment(ref attempts);
                if (n == 1)
                    throw new InvalidOperationException("boom — force rollback on first delivery");
                succeeded.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await SendAsync(queue, "redeliver-me");
            var done = await Task.WhenAny(succeeded.Task, Task.Delay(20_000));
            done.Should().Be(succeeded.Task, "the rolled-back message must be redelivered and eventually succeed");
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        Volatile.Read(ref attempts).Should().BeGreaterThanOrEqualTo(2, "rollback must trigger at least one redelivery");
    }

    /// <summary>
    /// ConcurrentConsumers=N spins up N XMS sessions (competing consumers): messages are processed in
    /// parallel (observed in-flight &gt; 1) and each queue message is delivered exactly once (no
    /// duplication across the N sessions).
    /// </summary>
    [Fact]
    public async Task Listener_ConcurrentConsumers_ProcessInParallel_NoDuplication()
    {
        var queue = "DEV.QUEUE.5";
        await DrainQueueAsync(queue);

        const int messageCount = 8;
        var epCons = CreateEndpoint(queue, "receiveMode=listener&concurrentConsumers=4");
        var received = new ConcurrentBag<string>();
        var inFlight = 0;
        var maxInFlight = 0;
        var countdown = new CountdownEvent(messageCount);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var cur = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref maxInFlight, cur);

                received.Add(callInfo.Arg<IExchange>().In.Body?.ToString() ?? "");
                await Task.Delay(400); // hold the slot so parallel sessions overlap

                Interlocked.Decrement(ref inFlight);
                countdown.Signal();
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            for (var i = 0; i < messageCount; i++)
                await SendAsync(queue, $"msg-{i}");

            countdown.Wait(30_000);
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        received.Count.Should().Be(messageCount, "every queue message is delivered exactly once (no duplication, no loss)");
        received.Distinct().Should().HaveCount(messageCount, "no message is delivered to more than one competing consumer");
        Volatile.Read(ref maxInFlight).Should().BeGreaterThanOrEqualTo(2, "N sessions must process messages in parallel (a single session would peak at 1)");
    }

    /// <summary>
    /// Request-reply on the listener path: a request carrying a reply destination gets an <c>InOut</c>
    /// exchange, and the route's <c>Out</c> body is sent back correlated to the request — so the standard
    /// RPC producer receives the response.
    /// </summary>
    [Fact]
    public async Task Listener_Rpc_RepliesToRequest()
    {
        var queue = "DEV.QUEUE.1";
        await DrainQueueAsync(queue);

        // Server: XMS listener echoes the request back through Out.
        var epServer = CreateEndpoint(queue, "receiveMode=listener");
        var seenPattern = ExchangePattern.InOnly;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                seenPattern = ex.Pattern;
                ex.Out = new Message($"ECHO:{ex.In.Body}");
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epServer.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            // Client: standard RPC producer (dynamic reply queue).
            var epClient = CreateEndpoint(queue, "replyTo=true&timeout=15");
            var rpcProducer = (IbmMqProducer)epClient.CreateProducer();
            await rpcProducer.Start();

            var exchange = new Exchange(new Message("ping"));
            await rpcProducer.Process(exchange);

            await rpcProducer.Stop();
            await epClient.Stop();

            seenPattern.Should().Be(ExchangePattern.InOut, "the listener must see JMSReplyTo and mark the exchange request-reply");
            exchange.HasOut.Should().BeTrue("the XMS listener must send an RPC reply");
            exchange.Out!.Body?.ToString().Should().Be("ECHO:ping");
        }
        finally
        {
            await consumer.Stop();
            await epServer.Stop();
        }
    }

    /// <summary>
    /// The listener path carries the same headers as the poll path: application (user) headers the
    /// producer stamped, plus <c>redbIbmMq.*</c> MQMD metadata (Destination, MsgId).
    /// </summary>
    [Fact]
    public async Task Listener_MapsUserAndMqmdHeaders()
    {
        var queue = "DEV.QUEUE.1";
        await DrainQueueAsync(queue);

        var epCons = CreateEndpoint(queue, "receiveMode=listener");
        IExchange? captured = null;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
        try
        {
            var epProd = CreateEndpoint(queue);
            var producer = (IbmMqProducer)epProd.CreateProducer();
            await producer.Start();

            var outgoing = new Message("hdr-test");
            outgoing.Headers["x-order-id"] = "42";
            outgoing.Headers["x-tenant"] = "acme";
            await producer.Process(new Exchange(outgoing));

            await producer.Stop();
            await epProd.Stop();

            await Task.WhenAny(tcs.Task, Task.Delay(20_000));
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        captured.Should().NotBeNull();
        captured!.In.Headers.Should().ContainKey("x-order-id");
        captured.In.Headers["x-order-id"]!.ToString().Should().Be("42");
        captured.In.Headers.Should().ContainKey("x-tenant");
        captured.In.Headers.Should().ContainKey(IbmMqHeaders.Destination);
        captured.In.Headers.Should().ContainKey(IbmMqHeaders.MsgId);
    }

    /// <summary>
    /// Event-driven reply reception on the RPC <b>client</b> (`receiveMode=listener` on the producer): the
    /// reply is delivered via an XMS listener on the reply queue instead of the poll-MQGET loop, so the
    /// full request→reply round-trip is fast (the poll loop carried the managed client's ~500 ms tick).
    /// </summary>
    [Fact]
    public async Task Listener_RpcClient_ReplyReception_IsEventDrivenAndFast()
    {
        var queue = "DEV.QUEUE.3";
        await DrainQueueAsync(queue);

        // Server: XMS listener echoes the request back through Out.
        var epServer = CreateEndpoint(queue, "receiveMode=listener");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ex = callInfo.Arg<IExchange>();
                ex.Out = new Message($"ECHO:{ex.In.Body}");
                return Task.CompletedTask;
            });
        var consumer = (IbmMqConsumer)epServer.CreateConsumer(processor);
        await consumer.Start();

        // Client: RPC producer with EVENT-DRIVEN reply reception.
        var epClient = CreateEndpoint(queue, "replyTo=true&timeout=15&receiveMode=listener");
        var rpc = (IbmMqProducer)epClient.CreateProducer();
        await rpc.Start();

        var samples = new List<double>();
        try
        {
            for (var i = 0; i < 5; i++)
            {
                var exchange = new Exchange(new Message($"ping-{i}"));

                var sentAt = Stopwatch.GetTimestamp();
                await rpc.Process(exchange);
                var ms = Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds;

                exchange.HasOut.Should().BeTrue("the RPC client must receive the reply");
                exchange.Out!.Body?.ToString().Should().Be($"ECHO:ping-{i}");
                samples.Add(ms);
                _output.WriteLine($"round-trip {i}: {ms:F1} ms");
            }
        }
        finally
        {
            await rpc.Stop();
            await epClient.Stop();
            await consumer.Stop();
            await epServer.Stop();
        }

        var best = samples.Skip(1).Min();
        _output.WriteLine($"best round-trip: {best:F1} ms (poll reply loop floor is ~250–500 ms)");
        best.Should().BeLessThan(50, "event-driven reply reception makes the full RPC round-trip <50 ms");
    }

    /// <summary>
    /// The listener path continues the W3C distributed trace: a <c>traceparent</c> carried on the message
    /// (injected by the producer under an ambient activity) is picked up so the consumer-side activity
    /// shares the same trace id.
    /// </summary>
    [Fact]
    public async Task Listener_PropagatesW3CTraceContext()
    {
        var queue = "DEV.QUEUE.2";
        await DrainQueueAsync(queue);

        var consumerActivities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { if (a.Kind == ActivityKind.Consumer) consumerActivities.Add(a); },
        };
        ActivitySource.AddActivityListener(listener);

        var epCons = CreateEndpoint(queue, "receiveMode=listener");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ => { tcs.TrySetResult(); return Task.CompletedTask; });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();

        string expectedTraceId;
        try
        {
            // Produce under an ambient activity so the producer injects traceparent into the message.
            using (var root = RouteActivitySource.Source.StartActivity("test-root", ActivityKind.Producer))
            {
                root.Should().NotBeNull("the ActivityListener must sample so the producer injects trace context");
                expectedTraceId = root!.TraceId.ToString();
                await SendAsync(queue, "traced");
            }

            await Task.WhenAny(tcs.Task, Task.Delay(20_000));
            await Task.Delay(300); // let the consumer activity stop and register
        }
        finally
        {
            await consumer.Stop();
            await epCons.Stop();
        }

        consumerActivities.Should().Contain(
            a => a.TraceId.ToString() == expectedTraceId,
            "the consumer activity must continue the trace carried in the message");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                return;
        }
    }

    /// <summary>Destructively drains any leftover messages so a test starts from an empty queue.</summary>
    private async Task DrainQueueAsync(string queue)
    {
        var ep = CreateEndpoint(queue, "waitInterval=200");
        var drained = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ => { Interlocked.Increment(ref drained); return Task.CompletedTask; });

        var consumer = (IbmMqConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(800); // let the poll loop sweep whatever is queued
        await consumer.Stop();
        await ep.Stop();
    }

    /// <summary>Sends one message to <paramref name="queue"/> via the standard (poll/WMQ) producer.</summary>
    private async Task SendAsync(string queue, string body)
    {
        var epProd = CreateEndpoint(queue);
        var producer = (IbmMqProducer)epProd.CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message(body)));
        }
        finally
        {
            await producer.Stop();
            await epProd.Stop();
        }
    }
}
