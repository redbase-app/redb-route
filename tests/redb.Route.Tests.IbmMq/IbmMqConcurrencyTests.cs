using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;
using Xunit.Abstractions;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// End-to-end regression tests for the consumer-concurrency fix: <c>ConcurrentConsumers(N)</c> must
/// actually process up to N messages in parallel (it used to be a dead semaphore on a serial MQGET
/// loop). Each worker runs its own dedicated MQQueueManager, so N workers on a queue opened
/// INPUT_SHARED are real competing consumers. Expects IBM MQ at localhost:1414 (QM1,
/// DEV.APP.SVRCONN, app/admin).
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqConcurrencyTests
{
    private const string Host = "localhost";
    private const int Port = 1414;
    private const string Channel = "DEV.APP.SVRCONN";
    private const string QueueManager = "QM1";
    private const string User = "app";
    private const string Password = "admin";

    private readonly ITestOutputHelper _output;

    public IbmMqConcurrencyTests(ITestOutputHelper output) => _output = output;

    private static IbmMqEndpoint CreateEndpoint(string destination, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&channel={Channel}&queueManager={QueueManager}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"wmq:{destination}?{qs}");
        var component = new IbmMqComponent();
        return (IbmMqEndpoint)component.CreateEndpoint(uri);
    }

    private static async Task PublishAsync(string queue, IEnumerable<string> bodies)
    {
        var ep = CreateEndpoint(queue);
        var producer = (IbmMqProducer)ep.CreateProducer();
        await producer.Start();
        foreach (var b in bodies)
            await producer.Process(new Exchange(new Message(b)));
        await producer.Stop();
        await ep.Stop();
    }

    /// <summary>Drains any leftover messages from the shared dev queue so the test starts clean.</summary>
    private static async Task DrainAsync(string queue)
    {
        var ep = CreateEndpoint(queue, "waitInterval=1500");
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var consumer = (IbmMqConsumer)ep.CreateConsumer(processor);
        await consumer.Start();
        await Task.Delay(2500);
        await consumer.Stop();
        await ep.Stop();
    }

    [Fact]
    public async Task ConcurrentConsumers_ProcessesInParallel()
    {
        // Before the fix: a single serial MQGET loop awaited Process inline, so max concurrency was
        // always 1 regardless of ConcurrentConsumers. Now N workers (each its own queue-manager
        // connection) are real competing consumers on the INPUT_SHARED queue.
        const int concurrency = 5;
        const int messageCount = 20;
        const string queue = "DEV.QUEUE.2";

        await DrainAsync(queue);
        await PublishAsync(queue, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        var epCons = CreateEndpoint(queue, $"concurrentConsumers={concurrency}&waitInterval=2000");
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
                await Task.Delay(300);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref processed) >= messageCount) allDone.TrySetResult();
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(40_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().BeGreaterThan(1,
            "ConcurrentConsumers(5) must process messages in parallel — the serial-loop bug caps this at 1");
        maxConcurrent.Should().BeGreaterThanOrEqualTo(3,
            "with 5 competing consumers on an INPUT_SHARED queue, several should run at once");
    }

    [Fact]
    public async Task ConcurrentConsumersOne_ProcessesSerially()
    {
        // Lower bound: the default (1) must stay strictly serial, proving the knob gates parallelism.
        const int messageCount = 8;
        const string queue = "DEV.QUEUE.3";

        await DrainAsync(queue);
        await PublishAsync(queue, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        var epCons = CreateEndpoint(queue, "concurrentConsumers=1&waitInterval=2000");
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
                await Task.Delay(150);
                Interlocked.Decrement(ref current);
                if (Interlocked.Increment(ref processed) >= messageCount) allDone.TrySetResult();
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(30_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().Be(1, "ConcurrentConsumers(1) must process strictly one message at a time");
    }

    [Fact]
    public async Task ConcurrentConsumers_AllMessagesProcessedExactlyOnce()
    {
        // Competing consumers must SHARE the queue: every message processed once, no duplication/loss.
        const int concurrency = 4;
        const int messageCount = 24;
        const string queue = "DEV.QUEUE.2";

        await DrainAsync(queue);
        await PublishAsync(queue, Enumerable.Range(0, messageCount).Select(i => $"m-{i}"));

        var epCons = CreateEndpoint(queue, $"concurrentConsumers={concurrency}&waitInterval=2000");
        var received = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(ci.Arg<IExchange>().In.Body?.ToString() ?? "");
                if (received.Count >= messageCount) allDone.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (IbmMqConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(40_000));
        await consumer.Stop();
        await epCons.Stop();

        var distinct = received.Distinct().ToList();
        _output.WriteLine($"received={received.Count}, distinct={distinct.Count}");
        distinct.Count.Should().Be(messageCount, "every message delivered exactly once across competing consumers");
        received.Count.Should().Be(messageCount, "competing consumers share the queue — no duplicate delivery");
    }
}
