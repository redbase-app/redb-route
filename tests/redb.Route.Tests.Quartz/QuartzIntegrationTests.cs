using System.Collections.Concurrent;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Quartz;

namespace redb.Route.Tests.Quartz;

/// <summary>
/// End-to-end integration tests for Quartz connectors:
/// multiple consumers on shared schedulers, cross-component interaction,
/// pipeline processing, and concurrent lifecycle management.
/// </summary>
public class QuartzIntegrationTests : IAsyncLifetime
{
    private RouteContext? _context;
    private readonly List<IConsumer> _consumers = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _consumers)
        {
            try { await c.Stop(); } catch { }
        }
        _context?.Dispose();
    }

    private RouteContext CreateContext(string name)
    {
        _context = new RouteContext(name);
        return _context;
    }

    private void TrackConsumer(IConsumer consumer) => _consumers.Add(consumer);

    // ── Multi-consumer on shared scheduler ──────────────────────────

    [Fact]
    public async Task SharedScheduler_MultipleTimers_AllFireIndependently()
    {
        var context = CreateContext("multi-timer");
        var component = new QuartzTimerComponent();
        context.AddComponent(component);

        var counts = new ConcurrentDictionary<string, int>();
        counts["fast"] = 0;
        counts["slow"] = 0;

        var fastConsumer = CreateTimerConsumer(component, "fast", period: 100, counts);
        var slowConsumer = CreateTimerConsumer(component, "slow", period: 300, counts);
        TrackConsumer(fastConsumer);
        TrackConsumer(slowConsumer);

        await fastConsumer.Start();
        await slowConsumer.Start();

        // Wait enough for both to fire
        await WaitForCondition(() => counts["fast"] >= 3 && counts["slow"] >= 2, 5000);

        await fastConsumer.Stop();
        await slowConsumer.Stop();

        counts["fast"].Should().BeGreaterThanOrEqualTo(3, "fast timer should fire frequently");
        counts["slow"].Should().BeGreaterThanOrEqualTo(2, "slow timer should fire at its own rate");
        counts["fast"].Should().BeGreaterThan(counts["slow"], "fast timer should fire more than slow");
    }

    [Fact]
    public async Task SharedScheduler_CronAndTimer_CoexistOnSameContext()
    {
        var context = CreateContext("cron-timer-mixed");
        var cronComponent = new CronComponent();
        var timerComponent = new QuartzTimerComponent();
        context.AddComponent(cronComponent);
        context.AddComponent(timerComponent);

        var cronExchanges = new ConcurrentBag<IExchange>();
        var timerExchanges = new ConcurrentBag<IExchange>();

        // Cron: every second
        var cronConsumer = CreateCronConsumer(cronComponent, "cronJob", "* * * * * ?", cronExchanges);
        // Timer: every 200ms
        var timerConsumer = CreateTimerConsumerCapture(timerComponent, "timerJob", 200, timerExchanges);
        TrackConsumer(cronConsumer);
        TrackConsumer(timerConsumer);

        await cronConsumer.Start();
        await timerConsumer.Start();

        await WaitForCondition(() => cronExchanges.Count >= 1 && timerExchanges.Count >= 2, 5000);

        await cronConsumer.Stop();
        await timerConsumer.Stop();

        cronExchanges.Should().NotBeEmpty();
        timerExchanges.Should().NotBeEmpty();

        // Validate cron exchange properties
        var cronEx = cronExchanges.First();
        cronEx.Properties.Should().ContainKey("CamelCronSchedule");
        cronEx.Properties.Should().ContainKey("CamelCronName");
        cronEx.Properties["CamelCronName"].Should().Be("cronJob");

        // Validate timer exchange properties
        var timerEx = timerExchanges.First();
        timerEx.Properties.Should().ContainKey("CamelTimerName");
        timerEx.Properties["CamelTimerName"].Should().Be("timerJob");
    }

    // ── Stop one, other continues ───────────────────────────────────

    [Fact]
    public async Task StopOneConsumer_OtherContinuesFiring()
    {
        var context = CreateContext("stop-one");
        var component = new QuartzTimerComponent();
        context.AddComponent(component);

        var counts = new ConcurrentDictionary<string, int>();
        counts["a"] = 0;
        counts["b"] = 0;

        var consumerA = CreateTimerConsumer(component, "a", 100, counts);
        var consumerB = CreateTimerConsumer(component, "b", 100, counts);
        TrackConsumer(consumerA);
        TrackConsumer(consumerB);

        await consumerA.Start();
        await consumerB.Start();

        await WaitForCondition(() => counts["a"] >= 2 && counts["b"] >= 2, 5000);

        // Stop A, B should keep firing
        await consumerA.Stop();
        var bCountAtStop = counts["b"];

        await WaitForCondition(() => counts["b"] >= bCountAtStop + 3, 5000);

        await consumerB.Stop();

        counts["b"].Should().BeGreaterThan(bCountAtStop, "consumer B should continue after A stopped");
    }

    // ── Processing pipeline ─────────────────────────────────────────

    [Fact]
    public async Task CronConsumer_ProcessorTransformsData_OutputAccumulated()
    {
        var context = CreateContext("pipeline");
        var component = new CronComponent();
        context.AddComponent(component);

        var results = new ConcurrentBag<string>();

        var uri = EndpointUriParser.Parse("cron://transform?schedule=* * * * * ?");
        var endpoint = component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                var ex = ci.Arg<IExchange>();
                var fireTime = ex.Properties["CamelQuartzFireTime"];
                results.Add($"fired-at-{fireTime}");
            });

        var consumer = endpoint.CreateConsumer(processor);
        TrackConsumer(consumer);
        await consumer.Start();

        await WaitForCondition(() => results.Count >= 2, 5000);
        await consumer.Stop();

        results.Should().HaveCountGreaterThanOrEqualTo(2);
        results.Should().AllSatisfy(r => r.Should().StartWith("fired-at-"));
    }

    // ── Restart ─────────────────────────────────────────────────────

    [Fact]
    public async Task Consumer_StopAndRestart_ContinuesProcessing()
    {
        var context = CreateContext("restart");
        var component = new QuartzTimerComponent();
        context.AddComponent(component);

        var fireCount = 0;
        var uri = EndpointUriParser.Parse("qtimer://restartable?period=100");
        var endpoint = component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref fireCount));

        var consumer = endpoint.CreateConsumer(processor);
        TrackConsumer(consumer);

        // First run
        await consumer.Start();
        await WaitForCondition(() => fireCount >= 2, 10_000);
        await consumer.Stop();

        var countAfterFirstRun = fireCount;
        countAfterFirstRun.Should().BeGreaterThanOrEqualTo(2);

        // Restart
        await consumer.Start();
        await WaitForCondition(() => fireCount >= countAfterFirstRun + 2, 10_000);
        await consumer.Stop();

        fireCount.Should().BeGreaterThan(countAfterFirstRun, "fires should resume after restart");
    }

    // ── Separate contexts have separate schedulers ──────────────────

    [Fact]
    public async Task SeparateContexts_HaveSeparateSchedulers()
    {
        var context1 = new RouteContext("ctx1");
        var context2 = new RouteContext("ctx2");
        var comp1 = new QuartzTimerComponent();
        var comp2 = new QuartzTimerComponent();
        context1.AddComponent(comp1);
        context2.AddComponent(comp2);

        var count1 = 0;
        var count2 = 0;

        var uri1 = EndpointUriParser.Parse("qtimer://isolated1?period=100");
        var ep1 = comp1.CreateEndpoint(uri1);
        var p1 = Substitute.For<IProcessor>();
        p1.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref count1));
        var c1 = ep1.CreateConsumer(p1);
        _consumers.Add(c1);

        var uri2 = EndpointUriParser.Parse("qtimer://isolated2?period=100");
        var ep2 = comp2.CreateEndpoint(uri2);
        var p2 = Substitute.For<IProcessor>();
        p2.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref count2));
        var c2 = ep2.CreateConsumer(p2);
        _consumers.Add(c2);

        await c1.Start();
        await c2.Start();

        await WaitForCondition(() => count1 >= 2 && count2 >= 2, 5000);

        // Stop only context1's consumer
        await c1.Stop();
        context1.Dispose();

        var count2AtStop = count2;
        await WaitForCondition(() => count2 >= count2AtStop + 2, 5000);

        await c2.Stop();
        context2.Dispose();

        count2.Should().BeGreaterThan(count2AtStop, "context2's scheduler should be unaffected by context1 disposal");
    }

    // ── Processor exception does not stop other consumers ───────────

    [Fact]
    public async Task ProcessorException_DoesNotAffectOtherConsumers()
    {
        var context = CreateContext("error-isolation");
        var component = new QuartzTimerComponent();
        context.AddComponent(component);

        var errorCount = 0;
        var successCount = 0;

        // Failing consumer
        var failUri = EndpointUriParser.Parse("qtimer://failing?period=100");
        var failEp = component.CreateEndpoint(failUri);
        var failProc = Substitute.For<IProcessor>();
        failProc.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref errorCount);
                throw new InvalidOperationException("Intentional test error");
            });
        var failConsumer = failEp.CreateConsumer(failProc);
        TrackConsumer(failConsumer);

        // Healthy consumer
        var okUri = EndpointUriParser.Parse("qtimer://healthy?period=100");
        var okEp = component.CreateEndpoint(okUri);
        var okProc = Substitute.For<IProcessor>();
        okProc.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => Interlocked.Increment(ref successCount));
        var okConsumer = okEp.CreateConsumer(okProc);
        TrackConsumer(okConsumer);

        await failConsumer.Start();
        await okConsumer.Start();

        await WaitForCondition(() => errorCount >= 2 && successCount >= 2, 5000);

        await failConsumer.Stop();
        await okConsumer.Stop();

        errorCount.Should().BeGreaterThanOrEqualTo(2, "failing consumer should keep firing despite errors");
        successCount.Should().BeGreaterThanOrEqualTo(2, "healthy consumer unaffected by sibling errors");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static IConsumer CreateTimerConsumer(
        QuartzTimerComponent component, string name, int period,
        ConcurrentDictionary<string, int> counts)
    {
        var uri = EndpointUriParser.Parse($"qtimer://{name}?period={period}");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => counts.AddOrUpdate(name, 1, (_, v) => v + 1));
        return endpoint.CreateConsumer(processor);
    }

    private static IConsumer CreateTimerConsumerCapture(
        QuartzTimerComponent component, string name, int period,
        ConcurrentBag<IExchange> bag)
    {
        var uri = EndpointUriParser.Parse($"qtimer://{name}?period={period}");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => bag.Add(ci.Arg<IExchange>()));
        return endpoint.CreateConsumer(processor);
    }

    private static IConsumer CreateCronConsumer(
        CronComponent component, string name, string schedule,
        ConcurrentBag<IExchange> bag)
    {
        var uri = EndpointUriParser.Parse($"cron://{name}?schedule={schedule}");
        var endpoint = component.CreateEndpoint(uri);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => bag.Add(ci.Arg<IExchange>()));
        return endpoint.CreateConsumer(processor);
    }

    private static async Task WaitForCondition(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(50);
        }
    }
}
