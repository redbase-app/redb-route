using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using Xunit.Abstractions;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// End-to-end regression tests for the 3.2.2 RabbitMQ fixes against a real broker
/// (expects RabbitMQ at localhost:5672, admin/admin):
/// <list type="number">
///   <item>consumer dispatch concurrency was pinned to 1 (ConcurrentConsumers had no effect);</item>
///   <item>a per-route Stop/Start leaked one consume channel each cycle;</item>
///   <item>the new AutoAck consumer option (broker settles on hand-off, no requeue).</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQConcurrencyLeakAutoAckTests
{
    private const string Host = "localhost";
    private const int Port = 5672;
    private readonly ITestOutputHelper _output;

    public RabbitMQConcurrencyLeakAutoAckTests(ITestOutputHelper output) => _output = output;

    private static RabbitMQEndpoint CreateEndpoint(string queue, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&username=admin&password=admin&declare=true";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"rabbitmq://{queue}?{qs}");
        var component = new RabbitMQComponent();
        return (RabbitMQEndpoint)component.CreateEndpoint(uri);
    }

    private static async Task PublishAsync(string queue, IEnumerable<string> bodies)
    {
        var ep = CreateEndpoint(queue);
        var producer = (RabbitMQProducer)ep.CreateProducer();
        await producer.Start();
        foreach (var b in bodies)
            await producer.Process(new Exchange(new Message(b)));
        await producer.Stop();
        await ep.Stop();
    }

    // ───── Bug #1: dispatch concurrency ─────

    [Fact]
    public async Task Consumer_ConcurrentConsumers_ProcessesInParallel()
    {
        // Before the fix the consume channel was created with ConsumerDispatchConcurrency=1 (the
        // CreateChannelOptions ctor default), so the client dispatched deliveries strictly one at a
        // time and the max observed concurrency was always 1 — regardless of ConcurrentConsumers.
        const int concurrency = 5;
        const int messageCount = 20;
        var queue = $"test-parallel-{Guid.NewGuid():N}";

        await PublishAsync(queue, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        var epCons = CreateEndpoint(queue, $"concurrentConsumers={concurrency}");
        var current = 0;
        var maxConcurrent = 0;
        var processed = 0;
        var gate = new object();
        var allDone = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var now = Interlocked.Increment(ref current);
                lock (gate) { if (now > maxConcurrent) maxConcurrent = now; }
                await Task.Delay(250);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref processed) >= messageCount) allDone.TrySetResult();
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(25_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().BeGreaterThan(1,
            "ConcurrentConsumers(5) must dispatch deliveries in parallel — the dispatch=1 bug caps this at 1");
        maxConcurrent.Should().BeGreaterThanOrEqualTo(3,
            "with 5 concurrent slots and 20 queued messages, several should run at once");
    }

    [Fact]
    public async Task Consumer_ConcurrentConsumersOne_ProcessesSerially()
    {
        // The knob's lower bound: ConcurrentConsumers(1) (the default) must keep processing serial,
        // proving ConcurrentConsumers actually gates parallelism (not just left wide open).
        const int messageCount = 8;
        var queue = $"test-serial-{Guid.NewGuid():N}";

        await PublishAsync(queue, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        var epCons = CreateEndpoint(queue, "concurrentConsumers=1");
        var current = 0;
        var maxConcurrent = 0;
        var processed = 0;
        var gate = new object();
        var allDone = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                var now = Interlocked.Increment(ref current);
                lock (gate) { if (now > maxConcurrent) maxConcurrent = now; }
                await Task.Delay(100);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref processed) >= messageCount) allDone.TrySetResult();
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().Be(1, "ConcurrentConsumers(1) must process strictly one message at a time");
    }

    // ───── Bug #2: channel leak on per-route Stop/Start ─────

    [Fact]
    public async Task Consumer_StopStartCycles_DoNotLeakChannels()
    {
        // Reproduces the prod leak: RabbitMQConsumer.Stop() used to leave its consume channel open and
        // registered on the endpoint (endpoint.Stop() — the only place channels were closed — is not
        // called on a per-route Stop/Start). Each cycle therefore leaked one idle channel. With the fix
        // the consumer releases its own channel on Stop, so the endpoint's tracked-channel count returns
        // to 0 after every Stop instead of climbing.
        var queue = $"test-leak-{Guid.NewGuid():N}";
        var ep = CreateEndpoint(queue);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var consumer = (RabbitMQConsumer)ep.CreateConsumer(processor);

        const int cycles = 5;
        for (int i = 0; i < cycles; i++)
        {
            await consumer.Start();
            ep.TrackedChannelCount.Should().BeGreaterThanOrEqualTo(1,
                $"cycle {i}: the running consumer holds its consume channel");
            await consumer.Stop();
            ep.TrackedChannelCount.Should().Be(0,
                $"cycle {i}: the consumer must release its channel on Stop (no leak)");
        }

        await ep.Stop();
        ep.TrackedChannelCount.Should().Be(0);
    }

    // ───── AutoAck ─────

    [Fact]
    public async Task AutoAck_DeliversMessage()
    {
        var queue = $"test-autoack-ok-{Guid.NewGuid():N}";
        await PublishAsync(queue, new[] { "autoack-msg" });

        var epCons = CreateEndpoint(queue, "autoAck=true");
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        received.Should().Contain("autoack-msg");
    }

    [Fact]
    public async Task AutoAck_ProcessorThrows_MessageNotRequeued()
    {
        // With autoAck the broker settles the delivery on hand-off. A throw in the processor therefore
        // must NOT requeue the message (at-most-once) — the opposite of the manual-ack default.
        var queue = $"test-autoack-throw-{Guid.NewGuid():N}";
        await PublishAsync(queue, new[] { "poison" });

        var epCons = CreateEndpoint(queue, "autoAck=true");
        var attempts = 0;
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref attempts);
                throw new InvalidOperationException("boom");
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(3_000);      // let it deliver + throw
        await consumer.Stop();
        await epCons.Stop();

        attempts.Should().BeGreaterThanOrEqualTo(1);

        // The message must be gone (auto-acked despite the throw) — a fresh consumer sees nothing.
        var leftover = await DrainQueueAsync(queue, TimeSpan.FromSeconds(2));
        leftover.Should().BeEmpty("autoAck settles on hand-off, so a failed turn does not requeue");
    }

    [Fact]
    public async Task ManualAck_ProcessorThrows_MessageRequeued()
    {
        // Control for the autoAck test: the default (manual ack) nack-requeues on failure, so the
        // message survives a failed turn and is redelivered. Throw only on the first attempt to avoid
        // a poison loop; the second attempt succeeds.
        var queue = $"test-manual-throw-{Guid.NewGuid():N}";
        await PublishAsync(queue, new[] { "retry-me" });

        var epCons = CreateEndpoint(queue);   // autoAck defaults to false
        var attempts = 0;
        var received = new ConcurrentBag<string>();
        var tcs = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                    throw new InvalidOperationException("first attempt fails");
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                tcs.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(tcs.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        attempts.Should().BeGreaterThanOrEqualTo(2, "the nacked message must be redelivered");
        received.Should().Contain("retry-me");
    }

    /// <summary>Consumes whatever is currently on the queue for the given window and returns the bodies.</summary>
    private static async Task<List<string>> DrainQueueAsync(string queue, TimeSpan window)
    {
        var ep = CreateEndpoint(queue);
        var got = new ConcurrentBag<string>();
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                got.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                return Task.CompletedTask;
            });

        var consumer = (RabbitMQConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(window);
        await consumer.Stop();
        await ep.Stop();
        return got.ToList();
    }
}
