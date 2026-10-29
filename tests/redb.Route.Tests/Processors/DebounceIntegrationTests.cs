using System.Collections.Concurrent;
using AwesomeAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Debounce using full route compilation and pipeline execution.
/// </summary>
[Trait("Category", "Integration")]
public class DebounceIntegrationTests
{
    private readonly RouteContext _context = new();

    private static IExchange CreateExchange(object? body = null, string? key = null)
    {
        var ex = Exchange.Create(new Message(body), null);
        if (key != null) ex.In.Headers["DebounceKey"] = key;
        return ex;
    }

    private static Func<IExchange, string> KeyFromHeader =>
        e => e.In.Headers.TryGetValue("DebounceKey", out var v) ? v?.ToString() ?? "" : "";

    // ══════════════════════════════════════════════════════════════
    // Compiled pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CompiledPipeline_SingleMessage_ForwardedAfterQuiet()
    {
        var forwarded = new ConcurrentBag<object?>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        def.Debounce(KeyFromHeader, TimeSpan.FromMilliseconds(100))
            .Process(e => forwarded.Add(e.In.Body))
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("hello", "A"));

        forwarded.Should().BeEmpty();
        await Task.Delay(350);

        forwarded.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public async Task CompiledPipeline_RapidFire_OnlyLastForwarded()
    {
        var forwarded = new ConcurrentBag<object?>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        // The window is wide against the 30 ms gaps: with 150 ms a slow machine stretched a gap past the window and
        // forwarded v1 as well.
        def.Debounce(KeyFromHeader, TimeSpan.FromSeconds(1))
            .Process(e => forwarded.Add(e.In.Body))
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("v1", "A"));
        await Task.Delay(30);
        await pipeline.Process(CreateExchange("v2", "A"));
        await Task.Delay(30);
        await pipeline.Process(CreateExchange("v3", "A"));

        await Task.Delay(2_000);

        forwarded.Should().ContainSingle().Which.Should().Be("v3");
    }

    [Fact]
    public async Task CompiledPipeline_DifferentKeys_Independent()
    {
        var forwarded = new ConcurrentBag<string>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        def.Debounce(KeyFromHeader, TimeSpan.FromMilliseconds(100))
            .Process(e =>
            {
                var k = e.In.Headers["DebounceKey"]?.ToString() ?? "";
                forwarded.Add($"{k}:{e.In.Body}");
            })
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("a1", "A"));
        await pipeline.Process(CreateExchange("b1", "B"));

        await Task.Delay(350);

        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("A:a1");
        forwarded.Should().Contain("B:b1");
    }

    [Fact]
    public async Task CompiledPipeline_DebounceWrapsDownstreamSteps()
    {
        var forwarded = new ConcurrentBag<object?>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        def.Debounce(KeyFromHeader, TimeSpan.FromMilliseconds(100))
            .Transform(e => $"transformed:{e.In.Body}")
            .Process(e => forwarded.Add(e.In.Body))
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("raw", "X"));
        await Task.Delay(350);

        forwarded.Should().ContainSingle().Which.Should().Be("transformed:raw");
    }

    [Fact]
    public async Task CompiledPipeline_DebounceAfterOtherSteps()
    {
        var forwarded = new ConcurrentBag<object?>();
        var preProcessed = new ConcurrentBag<object?>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        def.Process(e => preProcessed.Add(e.In.Body));
        // Wide against the 30 ms gap, as in RapidFire: a slow machine stretched the gap past 100 ms.
        def.Debounce(KeyFromHeader, TimeSpan.FromSeconds(1))
            .Process(e => forwarded.Add(e.In.Body))
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("msg1", "A"));
        await Task.Delay(30);
        await pipeline.Process(CreateExchange("msg2", "A"));

        // Pre-debounce processor runs for every message
        preProcessed.Should().HaveCount(2);

        await Task.Delay(2_000);

        // Post-debounce only gets the last
        forwarded.Should().ContainSingle().Which.Should().Be("msg2");
    }

    [Fact]
    public async Task CompiledPipeline_MultipleKeysBurstThenQuiet()
    {
        var forwarded = new ConcurrentBag<string>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        // Wide against the burst: five calls in a row may take longer than 150 ms on a slow machine.
        def.Debounce(KeyFromHeader, TimeSpan.FromSeconds(1))
            .Process(e =>
            {
                var k = e.In.Headers["DebounceKey"]?.ToString() ?? "";
                forwarded.Add($"{k}:{e.In.Body}");
            })
            .End();

        var pipeline = def.CreateProcessor(_context);

        // Burst on A
        await pipeline.Process(CreateExchange("a1", "A"));
        await pipeline.Process(CreateExchange("a2", "A"));
        await pipeline.Process(CreateExchange("a3", "A"));

        // Burst on B
        await pipeline.Process(CreateExchange("b1", "B"));
        await pipeline.Process(CreateExchange("b2", "B"));

        await Task.Delay(2_000);

        forwarded.Should().HaveCount(2);
        forwarded.Should().Contain("A:a3");
        forwarded.Should().Contain("B:b2");
    }

    [Fact]
    public async Task CompiledPipeline_HeaderModifiedByDebounce()
    {
        var forwarded = new ConcurrentBag<object?>();
        var def = new RouteDefinition();
        def.From("direct://debounce");
        def.Debounce(KeyFromHeader, TimeSpan.FromMilliseconds(100))
            .SetHeader("Debounced", "true")
            .Process(e => forwarded.Add(e.In.Headers["Debounced"]))
            .End();

        var pipeline = def.CreateProcessor(_context);

        await pipeline.Process(CreateExchange("test", "A"));
        await Task.Delay(350);

        forwarded.Should().ContainSingle().Which.Should().Be("true");
    }
}
