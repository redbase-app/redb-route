using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for DebounceProcessor, DSL, and step creation.
/// </summary>
public class DebounceTests
{
    private static IExchange CreateExchange(object? body = null, string? key = null)
    {
        var ex = Exchange.Create(new Message(body), null);
        if (key != null) ex.In.Headers["DebounceKey"] = key;
        return ex;
    }

    private static Func<IExchange, string> KeyFromHeader =>
        e => e.In.Headers.TryGetValue("DebounceKey", out var v) ? v?.ToString() ?? "" : "";

    // ══════════════════════════════════════════════════════════════
    // Constructor validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new DebounceProcessor(null!, KeyFromHeader, TimeSpan.FromMilliseconds(100));
        act.Should().Throw<ArgumentNullException>().WithParameterName("next");
    }

    [Fact]
    public void Constructor_NullKeyExtractor_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new DebounceProcessor(next, null!, TimeSpan.FromMilliseconds(100));
        act.Should().Throw<ArgumentNullException>().WithParameterName("keyExtractor");
    }

    [Fact]
    public void Constructor_ZeroQuietPeriod_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new DebounceProcessor(next, KeyFromHeader, TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    [Fact]
    public void Constructor_NegativeQuietPeriod_Throws()
    {
        var next = new DelegateProcessor(_ => { });
        var act = () => new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    // ══════════════════════════════════════════════════════════════
    // Processor behavior
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SingleMessage_ForwardedAfterQuietPeriod()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(100));

        await debounce.Process(CreateExchange("hello", "A"));

        // Should not be forwarded immediately
        forwarded.Should().BeEmpty();

        // Wait for quiet period + margin
        await Task.Delay(350);

        forwarded.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task RapidMessages_SameKey_OnlyLastForwarded()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(150));

        // Send 3 messages in quick succession for the same key
        await debounce.Process(CreateExchange("first", "A"));
        await Task.Delay(30);
        await debounce.Process(CreateExchange("second", "A"));
        await Task.Delay(30);
        await debounce.Process(CreateExchange("third", "A"));

        // Wait for quiet period + margin
        await Task.Delay(400);

        // Only the last message should have been forwarded
        forwarded.Should().ContainSingle().Which.Should().Be("third");
    }

    [Fact]
    public async Task DifferentKeys_Independent()
    {
        var forwarded = new ConcurrentBag<string>();
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["DebounceKey"]?.ToString() ?? "";
            forwarded.Add($"{key}:{ex.In.Body}");
        });
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(100));

        await debounce.Process(CreateExchange("a1", "A"));
        await debounce.Process(CreateExchange("b1", "B"));

        await Task.Delay(350);

        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("A:a1");
        forwarded.Should().Contain("B:b1");
    }

    [Fact]
    public async Task DifferentKeys_OnlyLatestPerKey()
    {
        var forwarded = new ConcurrentBag<string>();
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["DebounceKey"]?.ToString() ?? "";
            forwarded.Add($"{key}:{ex.In.Body}");
        });
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(150));

        // Rapid updates on both keys
        await debounce.Process(CreateExchange("a1", "A"));
        await debounce.Process(CreateExchange("b1", "B"));
        await Task.Delay(30);
        await debounce.Process(CreateExchange("a2", "A"));
        await debounce.Process(CreateExchange("b2", "B"));
        await Task.Delay(30);
        await debounce.Process(CreateExchange("a3", "A"));

        await Task.Delay(300);

        // A should forward "a3", B should forward "b2"
        forwarded.Should().Contain("A:a3");
        forwarded.Should().Contain("B:b2");
        forwarded.Where(f => f.StartsWith("A:")).Should().ContainSingle();
        forwarded.Where(f => f.StartsWith("B:")).Should().ContainSingle();
    }

    [Fact]
    public async Task Process_ReturnsImmediately()
    {
        var next = new DelegateProcessor(async (_, ct) =>
        {
            await Task.Delay(500, ct); // Slow downstream
        });
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(50));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await debounce.Process(CreateExchange("fast", "A"));
        sw.Stop();

        // Process should return immediately, not wait for quiet period or downstream
        sw.ElapsedMilliseconds.Should().BeLessThan(50);
    }

    [Fact]
    public async Task TimerReset_ExtendsQuietPeriod()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(200));

        await debounce.Process(CreateExchange("msg1", "A"));
        await Task.Delay(100); // 100ms in, timer still has 100ms left
        await debounce.Process(CreateExchange("msg2", "A")); // Resets timer to 200ms

        // At 200ms total — original timer would have fired, but it was reset
        await Task.Delay(120);
        forwarded.Should().BeEmpty("timer was reset by second message");

        // After full new quiet period passes (~300ms from msg2)
        await Task.Delay(250);
        forwarded.Should().ContainSingle().Which.Should().Be("msg2");
    }

    [Fact]
    public async Task Dispose_CancelsTimers()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(200));

        await debounce.Process(CreateExchange("hello", "A"));
        debounce.Dispose();

        // Timer was cancelled, should not forward
        await Task.Delay(400);
        forwarded.Should().BeEmpty();
    }

    [Fact]
    public async Task DisposedProcessor_ThrowsOnProcess()
    {
        var next = new DelegateProcessor(_ => { });
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(100));
        debounce.Dispose();

        var act = () => debounce.Process(CreateExchange("nope", "A"));
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task FlushAsync_ForwardsAllPending()
    {
        var forwarded = new ConcurrentBag<string>();
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["DebounceKey"]?.ToString() ?? "";
            forwarded.Add($"{key}:{ex.In.Body}");
        });
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromSeconds(10));

        await debounce.Process(CreateExchange("a-val", "A"));
        await debounce.Process(CreateExchange("b-val", "B"));

        // Flush without waiting for timers
        await debounce.FlushAsync();

        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("A:a-val");
        forwarded.Should().Contain("B:b-val");

        debounce.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_FlushesAndCleans()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromSeconds(10));

        await debounce.Process(CreateExchange("flushed", "A"));

        await debounce.DisposeAsync();

        forwarded.Should().ContainSingle().Which.Should().Be("flushed");
    }

    [Fact]
    public async Task NullKey_UsesEmptyString()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        Func<IExchange, string> nullKey = _ => null!;
        using var debounce = new DebounceProcessor(next, nullKey, TimeSpan.FromMilliseconds(100));

        await debounce.Process(CreateExchange("val1"));
        await Task.Delay(30);
        await debounce.Process(CreateExchange("val2"));

        await Task.Delay(350);

        // Both go to empty-string key, only last forwarded
        forwarded.Should().ContainSingle().Which.Should().Be("val2");
    }

    [Fact]
    public async Task SequentialBursts_SameKey_ForwardsEachBurst()
    {
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(100));

        // First burst
        await debounce.Process(CreateExchange("burst1-a", "K"));
        await Task.Delay(20);
        await debounce.Process(CreateExchange("burst1-b", "K"));

        // Wait for quiet period to expire
        await Task.Delay(350);
        forwarded.Should().ContainSingle().Which.Should().Be("burst1-b");

        // Second burst — same key
        await debounce.Process(CreateExchange("burst2-a", "K"));
        await Task.Delay(20);
        await debounce.Process(CreateExchange("burst2-b", "K"));

        await Task.Delay(350);
        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("burst2-b");
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — step recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Debounce_RecordsStep()
    {
        var def = new RouteDefinition();
        Func<IExchange, string> key = e => "k";
        def.From("direct:in").Debounce(key, TimeSpan.FromMilliseconds(500));

        def.Steps.Should().HaveCount(2);
        var step = def.Steps[1].Should().BeOfType<DebounceStep>().Subject;
        step.KeyExtractor.Should().BeSameAs(key);
        step.QuietPeriod.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void DSL_Debounce_NullKeyExtractor_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:in");
        var act = () => def.Debounce(null!, TimeSpan.FromMilliseconds(100));
        act.Should().Throw<ArgumentNullException>().WithParameterName("keyExtractor");
    }

    [Fact]
    public void DSL_Debounce_ZeroQuietPeriod_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:in");
        var act = () => def.Debounce(e => "k", TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    [Fact]
    public void DSL_Debounce_NegativeQuietPeriod_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct:in");
        var act = () => def.Debounce(e => "k", TimeSpan.FromMilliseconds(-50));
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    [Fact]
    public void DSL_Debounce_Chaining()
    {
        var def = new RouteDefinition();
        var result = def.From("direct:in")
            .Debounce(e => "k", TimeSpan.FromMilliseconds(200))
            .To("direct:out");

        result.Should().BeSameAs(def);
        def.Steps.Should().HaveCount(3);
    }
}
