using System.Diagnostics;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Keyed Throttle using full route compilation and pipeline execution.
/// </summary>
[Trait("Category", "Integration")]
public class KeyedThrottleIntegrationTests
{
    private readonly RouteContext _context = new();

    private static IExchange CreateExchange(object? body = null, string? key = null)
    {
        var ex = Exchange.Create(new Message(body), null);
        if (key != null) ex.In.Headers["ThrottleKey"] = key;
        return ex;
    }

    private static Func<IExchange, string> KeyFromHeader =>
        e => e.In.Headers.TryGetValue("ThrottleKey", out var v) ? v?.ToString() ?? "" : "";

    // ══════════════════════════════════════════════════════════════
    // Basic compiled pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_SingleKey_PassesUnderLimit()
    {
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 5, TimeSpan.FromSeconds(5));
        def.Transform(e => $"ok:{e.In.Body}");

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var ex = CreateExchange("hello", "A");
        await pipeline.Process(ex);

        ex.In.Body.Should().Be("ok:hello");
    }

    [Fact]
    public async Task CompiledPipeline_DifferentKeys_IndependentRates()
    {
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 2, TimeSpan.FromSeconds(10));
        def.Transform(e => $"ok:{e.In.Body}");

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        // 2 messages per key should pass immediately
        var a1 = CreateExchange("a1", "A");
        var a2 = CreateExchange("a2", "A");
        var b1 = CreateExchange("b1", "B");
        var b2 = CreateExchange("b2", "B");

        await pipeline.Process(a1);
        await pipeline.Process(b1);
        await pipeline.Process(a2);
        await pipeline.Process(b2);

        a1.In.Body.Should().Be("ok:a1");
        a2.In.Body.Should().Be("ok:a2");
        b1.In.Body.Should().Be("ok:b1");
        b2.In.Body.Should().Be("ok:b2");
    }

    [Fact]
    public async Task CompiledPipeline_SameKey_ExceedLimit_IsThrottled()
    {
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 2, TimeSpan.FromMilliseconds(500));
        def.Process(e => { }); // no-op tail

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        // Fill the bucket
        await pipeline.Process(CreateExchange(1, "X"));
        await pipeline.Process(CreateExchange(2, "X"));

        // 3rd message must wait ~500ms
        var sw = Stopwatch.StartNew();
        await pipeline.Process(CreateExchange(3, "X"));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(400);
    }

    // ══════════════════════════════════════════════════════════════
    // Preserves exchange properties
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_PreservesHeadersAndProperties()
    {
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 10);
        def.Process(e => { }); // no-op

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var ex = CreateExchange("body", "K");
        ex.In.Headers["Custom"] = "value";
        ex.Properties["Prop1"] = 42;

        await pipeline.Process(ex);

        ex.In.Headers["Custom"].Should().Be("value");
        ex.In.Headers["ThrottleKey"].Should().Be("K");
        ex.Properties["Prop1"].Should().Be(42);
        ex.In.Body.Should().Be("body");
    }

    // ══════════════════════════════════════════════════════════════
    // Chaining with transform
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_ChainingWithTransform()
    {
        var def = new RouteDefinition();
        def.From("direct://kt")
           .SetHeader("Step", "before")
           .Throttle(KeyFromHeader, 10)
           .Transform(e => $"throttled:{e.In.Body}");

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var ex = CreateExchange("data", "K");
        await pipeline.Process(ex);

        // SetHeader runs before throttle (non-wrapping), transform is in the tail
        ex.In.Headers["Step"].Should().Be("before");
        ex.In.Body.Should().Be("throttled:data");
    }

    // ══════════════════════════════════════════════════════════════
    // Default period (1 second)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_DefaultPeriod_Works()
    {
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 100); // no period = 1 second default
        def.Process(e => { });

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        // 100 messages under limit should pass fast
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
            await pipeline.Process(CreateExchange(i, "K"));
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    // ══════════════════════════════════════════════════════════════
    // Concurrent keys
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_ConcurrentKeys_AllProcessed()
    {
        var count = 0;
        var def = new RouteDefinition();
        def.From("direct://kt");
        def.Throttle(KeyFromHeader, 20, TimeSpan.FromSeconds(5));
        def.Process(e => Interlocked.Increment(ref count));

        var compiler = new RouteCompiler(_context, null);
        var pipeline = compiler.Compile(def);

        var tasks = Enumerable.Range(0, 40)
            .Select(i => pipeline.Process(CreateExchange(i, $"Key{i % 4}")))
            .ToArray();

        await Task.WhenAll(tasks);
        count.Should().Be(40);
    }
}
