using System.Diagnostics;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for KeyedThrottleProcessor, DSL, and step creation.
/// </summary>
public class KeyedThrottleTests
{
    private static IExchange CreateExchange(object? body = null, string? key = null)
    {
        var ex = Exchange.Create(new Message(body), null);
        if (key != null) ex.In.Headers["ThrottleKey"] = key;
        return ex;
    }

    private static Func<IExchange, string> KeyFromHeader =>
        e => e.In.Headers.TryGetValue("ThrottleKey", out var v) ? v?.ToString() ?? "" : "";

    // ══════════════════════════════════════════════════════════════
    // Constructor validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new KeyedThrottleProcessor(null!, KeyFromHeader, 5);
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public void Constructor_NullKeyExtractor_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new KeyedThrottleProcessor(next, null!, 5);
        act.Should().Throw<ArgumentNullException>().WithParameterName("keyExtractor");
    }

    [Fact]
    public void Constructor_ZeroMax_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new KeyedThrottleProcessor(next, KeyFromHeader, 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxPerPeriod");
    }

    [Fact]
    public void Constructor_NegativeMax_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new KeyedThrottleProcessor(next, KeyFromHeader, -1);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxPerPeriod");
    }

    // ══════════════════════════════════════════════════════════════
    // Processor behavior
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SingleMessage_PassesThrough()
    {
        var processed = false;
        var next = new DelegateProcessor(_ => processed = true);
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 10);

        await throttle.Process(CreateExchange("hello", "A"));

        processed.Should().BeTrue();
    }

    [Fact]
    public async Task SameKey_UnderLimit_AllPass()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 5, TimeSpan.FromSeconds(2));

        for (int i = 0; i < 5; i++)
            await throttle.Process(CreateExchange(i, "A"));

        count.Should().Be(5);
    }

    [Fact]
    public async Task DifferentKeys_IndependentLimits()
    {
        var countA = 0;
        var countB = 0;
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["ThrottleKey"]?.ToString();
            if (key == "A") Interlocked.Increment(ref countA);
            else Interlocked.Increment(ref countB);
        });
        // 2 per period per key, long period so no slot release
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 2, TimeSpan.FromSeconds(10));

        // Key A: 2 should pass immediately
        await throttle.Process(CreateExchange(1, "A"));
        await throttle.Process(CreateExchange(2, "A"));

        // Key B: 2 should also pass immediately (independent bucket)
        await throttle.Process(CreateExchange(3, "B"));
        await throttle.Process(CreateExchange(4, "B"));

        countA.Should().Be(2);
        countB.Should().Be(2);
    }

    [Fact]
    public async Task SameKey_ExceedLimit_Throttles()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 2, TimeSpan.FromMilliseconds(500));

        var sw = Stopwatch.StartNew();
        // Send 2 — fills the bucket
        await throttle.Process(CreateExchange(1, "X"));
        await throttle.Process(CreateExchange(2, "X"));
        var fast = sw.ElapsedMilliseconds;

        // 3rd must wait for slot release (~500ms)
        await throttle.Process(CreateExchange(3, "X"));
        sw.Stop();

        count.Should().Be(3);
        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
    }

    [Fact]
    public async Task ExceedOnOneKey_OtherKeyStillFast()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 1, TimeSpan.FromSeconds(10));

        // Exhaust key A
        await throttle.Process(CreateExchange(1, "A"));

        // Key B should still pass immediately
        var sw = Stopwatch.StartNew();
        await throttle.Process(CreateExchange(2, "B"));
        sw.Stop();

        count.Should().Be(2);
        sw.ElapsedMilliseconds.Should().BeLessThan(200);
    }

    [Fact]
    public async Task PreservesExchangeBody()
    {
        object? captured = null;
        var next = new DelegateProcessor(ex => captured = ex.In.Body);
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 10);

        await throttle.Process(CreateExchange("payload", "K"));

        captured.Should().Be("payload");
    }

    [Fact]
    public async Task NullKey_TreatedAsEmptyString()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, _ => null!, 10);

        await throttle.Process(CreateExchange("a"));
        await throttle.Process(CreateExchange("b"));

        count.Should().Be(2);
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceled()
    {
        var next = new DelegateProcessor(_ => { });
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 1, TimeSpan.FromSeconds(10));

        // Exhaust the single slot for key "Z"
        await throttle.Process(CreateExchange(1, "Z"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => throttle.Process(CreateExchange(2, "Z"), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConcurrentAccess_ThreadSafe()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 50, TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(0, 50)
            .Select(i => throttle.Process(CreateExchange(i, "K")))
            .ToArray();

        await Task.WhenAll(tasks);
        count.Should().Be(50);
    }

    [Fact]
    public async Task ConcurrentAccess_MultipleKeys_ThreadSafe()
    {
        var count = 0;
        var next = new DelegateProcessor(_ => Interlocked.Increment(ref count));
        using var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 20, TimeSpan.FromSeconds(5));

        var tasks = Enumerable.Range(0, 60)
            .Select(i => throttle.Process(CreateExchange(i, $"Key{i % 3}")))
            .ToArray();

        await Task.WhenAll(tasks);
        count.Should().Be(60);
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var next = new DelegateProcessor(_ => { });
        var throttle = new KeyedThrottleProcessor(next, KeyFromHeader, 10);
        throttle.Dispose();
        var act = () => throttle.Dispose();
        act.Should().NotThrow();
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — step recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Throttle_Keyed_RecordsStep()
    {
        var def = new RouteDefinition();
        Func<IExchange, string> key = e => "k";
        def.From("direct:in").Throttle(key, 10, TimeSpan.FromSeconds(2));

        def.Steps.Should().HaveCount(2);
        var step = def.Steps[1].Should().BeOfType<KeyedThrottleStep>().Subject;
        step.KeyExtractor.Should().BeSameAs(key);
        step.MaxPerPeriod.Should().Be(10);
        step.Period.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void DSL_Throttle_Keyed_DefaultPeriod()
    {
        var def = new RouteDefinition();
        Func<IExchange, string> key = e => "k";
        def.From("direct:in").Throttle(key, 5);

        var step = def.Steps[1].Should().BeOfType<KeyedThrottleStep>().Subject;
        step.Period.Should().BeNull();
    }

    [Fact]
    public void DSL_Throttle_Keyed_NullKeyExtractor_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:in");
        var act = () => def.Throttle(null!, 5);
        act.Should().Throw<ArgumentNullException>().WithParameterName("keyExtractor");
    }

    [Fact]
    public void DSL_Throttle_Keyed_ZeroMax_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:in");
        var act = () => def.Throttle(e => "k", 0);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("maxPerPeriod");
    }

    [Fact]
    public void DSL_Throttle_Keyed_Chaining()
    {
        var def = new RouteDefinition();
        var result = def.From("direct:in")
            .Throttle(e => "k", 10)
            .To("direct:out");

        result.Should().BeSameAs(def);
        def.Steps.Should().HaveCount(3);
    }
}
