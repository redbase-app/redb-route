using System.Collections.Concurrent;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;
using Xunit.Abstractions;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// End-to-end regression tests for the consumer-concurrency fix: <c>ConcurrentConsumers(N)</c> must
/// actually process up to N messages in parallel (it used to be a dead semaphore on a serial receive
/// loop). Expects an AMQP 1.0 broker (ActiveMQ Artemis) at localhost:5673 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpConcurrencyTests
{
    private const string Host = "localhost";
    private const int Port = 5673;
    private const string User = "admin";
    private const string Password = "admin";
    private readonly ITestOutputHelper _output;

    public AmqpConcurrencyTests(ITestOutputHelper output) => _output = output;

    private static AmqpEndpoint CreateEndpoint(string address, string? extraParams = null)
    {
        var qs = $"host={Host}&port={Port}&user={User}&password={Password}";
        if (extraParams is not null) qs += $"&{extraParams}";
        var uri = EndpointUriParser.Parse($"amqp://{address}?{qs}");
        var component = new AmqpComponent();
        return (AmqpEndpoint)component.CreateEndpoint(uri);
    }

    private static async Task PublishAsync(string address, IEnumerable<string> bodies)
    {
        var ep = CreateEndpoint(address);
        var producer = (AmqpProducer)ep.CreateProducer();
        await producer.Start();
        foreach (var b in bodies)
            await producer.Process(new Exchange(new Message(b)));
        await producer.Stop();
        await ep.Stop();
    }

    [Fact]
    public async Task ConcurrentConsumers_ProcessesInParallel()
    {
        // Before the fix: a single serial receive loop awaited Process inline, so max concurrency was
        // always 1 regardless of ConcurrentConsumers. Now N workers = N competing consumers.
        const int concurrency = 5;
        const int messageCount = 20;
        var address = $"conc.{Guid.NewGuid():N}";

        await PublishAsync(address, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        // credit=1 keeps each competing consumer holding a single unsettled message at a time, so the
        // broker load-balances fairly across the N workers (a high credit lets one worker's prefetch
        // buffer hoard the queue and process it serially — real behaviour, tune credit for balancing).
        var epCons = CreateEndpoint(address, $"concurrentConsumers={concurrency}&credit=1");
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(25_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().BeGreaterThan(1,
            "ConcurrentConsumers(5) must process deliveries in parallel — the serial-loop bug caps this at 1");
        maxConcurrent.Should().BeGreaterThanOrEqualTo(3,
            "with 5 competing consumers and 20 queued messages, several should run at once");
    }

    [Fact]
    public async Task ConcurrentConsumersOne_ProcessesSerially()
    {
        // Lower bound: the default (1) must stay strictly serial, proving the knob gates parallelism.
        const int messageCount = 8;
        var address = $"serial.{Guid.NewGuid():N}";

        await PublishAsync(address, Enumerable.Range(0, messageCount).Select(i => $"msg-{i}"));

        var epCons = CreateEndpoint(address, "concurrentConsumers=1");
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

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(15_000));
        await consumer.Stop();
        await epCons.Stop();

        _output.WriteLine($"processed={processed}, maxConcurrent={maxConcurrent}");
        processed.Should().BeGreaterThanOrEqualTo(messageCount);
        maxConcurrent.Should().Be(1, "ConcurrentConsumers(1) must process strictly one message at a time");
    }

    [Fact]
    public async Task ConcurrentConsumers_AllMessagesProcessedExactlyOnce()
    {
        // Competing consumers must SHARE the queue (anycast), not each get a copy: every message is
        // processed, and the total count equals what was published (no duplication, no loss).
        const int concurrency = 4;
        const int messageCount = 40;
        var address = $"share.{Guid.NewGuid():N}";

        await PublishAsync(address, Enumerable.Range(0, messageCount).Select(i => $"m-{i}"));

        var epCons = CreateEndpoint(address, $"concurrentConsumers={concurrency}");
        var received = new ConcurrentBag<string>();
        var allDone = new TaskCompletionSource();

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                received.Add(Encoding.UTF8.GetString((byte[])ci.Arg<IExchange>().In.Body!));
                if (received.Count >= messageCount) allDone.TrySetResult();
                return Task.CompletedTask;
            });

        var consumer = (AmqpConsumer)epCons.CreateConsumer(processor);
        await consumer.Start();
        await Task.WhenAny(allDone.Task, Task.Delay(20_000));
        await consumer.Stop();
        await epCons.Stop();

        var distinct = received.Distinct().ToList();
        _output.WriteLine($"received={received.Count}, distinct={distinct.Count}");
        distinct.Count.Should().Be(messageCount, "every published message is delivered exactly once across the competing consumers");
        received.Count.Should().Be(messageCount, "competing consumers share the queue — no duplicate delivery");
    }
}
