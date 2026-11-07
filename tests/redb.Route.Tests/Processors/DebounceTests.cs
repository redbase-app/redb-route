using System.Collections.Concurrent;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using Microsoft.Extensions.Time.Testing;
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
    // Processor behavior. The quiet period runs on a FakeTimeProvider (findings register row 21): time moves only
    // when a test advances it, so no assertion depends on how loaded the machine is. A forward runs as a
    // continuation on the thread pool after its timer fires, so Forwarded waits for it to be observed.
    // ══════════════════════════════════════════════════════════════

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(100);

    private static async Task Forwarded<T>(ConcurrentBag<T> forwarded, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (forwarded.Count < count && DateTime.UtcNow < deadline)
            await Task.Delay(5);
    }

    [Fact]
    public async Task SingleMessage_ForwardedAfterQuietPeriod()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("hello", "A"));
        time.Advance(Quiet - TimeSpan.FromMilliseconds(1));
        forwarded.Should().BeEmpty("the quiet period has not passed");

        time.Advance(TimeSpan.FromMilliseconds(1));
        await Forwarded(forwarded, 1);
        forwarded.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task RapidMessages_SameKey_OnlyLastForwarded()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("first", "A"));
        time.Advance(TimeSpan.FromMilliseconds(30));
        await debounce.Process(CreateExchange("second", "A"));
        time.Advance(TimeSpan.FromMilliseconds(30));
        await debounce.Process(CreateExchange("third", "A"));
        time.Advance(Quiet);

        await Forwarded(forwarded, 1);
        forwarded.Should().ContainSingle().Which.Should().Be("third");
    }

    [Fact]
    public async Task DifferentKeys_Independent()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<string>();
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["DebounceKey"]?.ToString() ?? "";
            forwarded.Add($"{key}:{ex.In.Body}");
        });
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("a1", "A"));
        await debounce.Process(CreateExchange("b1", "B"));
        time.Advance(Quiet);

        await Forwarded(forwarded, 2);
        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("A:a1");
        forwarded.Should().Contain("B:b1");
    }

    [Fact]
    public async Task DifferentKeys_OnlyLatestPerKey()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<string>();
        var next = new DelegateProcessor(ex =>
        {
            var key = ex.In.Headers["DebounceKey"]?.ToString() ?? "";
            forwarded.Add($"{key}:{ex.In.Body}");
        });
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("a1", "A"));
        await debounce.Process(CreateExchange("b1", "B"));
        time.Advance(TimeSpan.FromMilliseconds(30));
        await debounce.Process(CreateExchange("a2", "A"));
        await debounce.Process(CreateExchange("b2", "B"));
        time.Advance(TimeSpan.FromMilliseconds(30));
        await debounce.Process(CreateExchange("a3", "A"));
        time.Advance(Quiet);

        await Forwarded(forwarded, 2);
        forwarded.Should().Contain("A:a3");
        forwarded.Should().Contain("B:b2");
        forwarded.Where(f => f.StartsWith("A:")).Should().ContainSingle();
        forwarded.Where(f => f.StartsWith("B:")).Should().ContainSingle();
    }

    [Fact]
    public void Process_ReturnsImmediately()
    {
        var next = new DelegateProcessor(async (_, ct) => await Task.Delay(500, ct)); // slow downstream
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: new FakeTimeProvider());

        // Neither the quiet period nor the downstream call is awaited by Process.
        debounce.Process(CreateExchange("fast", "A")).IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task TimerReset_ExtendsQuietPeriod()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(200), timeProvider: time);

        await debounce.Process(CreateExchange("msg1", "A"));
        time.Advance(TimeSpan.FromMilliseconds(100));
        await debounce.Process(CreateExchange("msg2", "A")); // resets the timer to 200 ms

        time.Advance(TimeSpan.FromMilliseconds(120)); // 200 ms after msg1 have passed
        forwarded.Should().BeEmpty("the timer was reset by the second message");

        time.Advance(TimeSpan.FromMilliseconds(80)); // 200 ms after msg2
        await Forwarded(forwarded, 1);
        forwarded.Should().ContainSingle().Which.Should().Be("msg2");
    }

    [Fact]
    public async Task Dispose_CancelsTimers()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromMilliseconds(200), timeProvider: time);

        await debounce.Process(CreateExchange("hello", "A"));
        debounce.Dispose();
        time.Advance(TimeSpan.FromMilliseconds(400));
        await Task.Yield();

        forwarded.Should().BeEmpty("the timer was cancelled");
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
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromSeconds(10), timeProvider: new FakeTimeProvider());

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
        var debounce = new DebounceProcessor(next, KeyFromHeader, TimeSpan.FromSeconds(10), timeProvider: new FakeTimeProvider());

        await debounce.Process(CreateExchange("flushed", "A"));

        await debounce.DisposeAsync();

        forwarded.Should().ContainSingle().Which.Should().Be("flushed");
    }

    [Fact]
    public async Task NullKey_UsesEmptyString()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        Func<IExchange, string> nullKey = _ => null!;
        using var debounce = new DebounceProcessor(next, nullKey, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("val1"));
        await debounce.Process(CreateExchange("val2"));
        time.Advance(Quiet);
        await Forwarded(forwarded, 1);
        time.Advance(Quiet); // nothing else is pending
        await Task.Yield();

        // Both go to empty-string key, only last forwarded
        forwarded.Should().ContainSingle().Which.Should().Be("val2");
    }

    [Fact]
    public async Task SequentialBursts_SameKey_ForwardsEachBurst()
    {
        var time = new FakeTimeProvider();
        var forwarded = new ConcurrentBag<object?>();
        var next = new DelegateProcessor(ex => forwarded.Add(ex.In.Body));
        using var debounce = new DebounceProcessor(next, KeyFromHeader, Quiet, timeProvider: time);

        await debounce.Process(CreateExchange("burst1-a", "K"));
        time.Advance(TimeSpan.FromMilliseconds(20));
        await debounce.Process(CreateExchange("burst1-b", "K"));
        time.Advance(Quiet);
        await Forwarded(forwarded, 1);
        forwarded.Should().ContainSingle().Which.Should().Be("burst1-b");

        await debounce.Process(CreateExchange("burst2-a", "K"));
        time.Advance(TimeSpan.FromMilliseconds(20));
        await debounce.Process(CreateExchange("burst2-b", "K"));
        time.Advance(Quiet);
        await Forwarded(forwarded, 2);
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
        def.Debounce(key, TimeSpan.FromMilliseconds(500));

        var node = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<DebounceDefinition>().Subject;
        node.KeyExtractor.Should().BeSameAs(key);
        node.QuietPeriod.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void DSL_Debounce_NullKeyExtractor_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.Debounce(null!, TimeSpan.FromMilliseconds(100));
        act.Should().Throw<ArgumentNullException>().WithParameterName("keyExtractor");
    }

    [Fact]
    public void DSL_Debounce_ZeroQuietPeriod_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.Debounce(e => "k", TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    [Fact]
    public void DSL_Debounce_NegativeQuietPeriod_Throws()
    {
        var def = new RouteDefinition();
        var act = () => def.Debounce(e => "k", TimeSpan.FromMilliseconds(-50));
        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("quietPeriod");
    }

    [Fact]
    public void DSL_Debounce_Chaining()
    {
        var def = new RouteDefinition();
        var scope = def.Debounce(e => "k", TimeSpan.FromMilliseconds(200));
        scope.To("direct:out");
        var result = scope.EndDebounce();

        result.Should().BeSameAs(def);
        def.Outputs.Should().ContainSingle().Which.Should().BeOfType<DebounceDefinition>()
            .Which.Outputs.Should().ContainSingle().Which.Should().BeOfType<ToDefinition>();
    }
}
