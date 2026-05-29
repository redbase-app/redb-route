using System;
using System.Threading.Tasks;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Integration tests for Sampling using full route compilation and pipeline execution.
/// </summary>
[Trait("Category", "Integration")]
public class SamplingIntegrationTests
{
    private readonly RouteContext _context = new();

    private static IExchange CreateExchange(object? body = null)
        => Exchange.Create(new Message(body), null);

    // ══════════════════════════════════════════════════════════════
    // Count-based: compiled pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_CompiledPipeline_SamplesEveryNth()
    {
        var def = new RouteDefinition();
        def.From("direct://sample");
        def.Sample(3);
        def.Transform(e => $"sampled:{e.In.Body}");

        var pipeline = def.CreateProcessor(_context);

        // Message 1 (passes) — gets transformed
        var e1 = CreateExchange("A");
        await pipeline.Process(e1);
        e1.In.Body.Should().Be("sampled:A");

        // Message 2 (dropped) — body unchanged
        var e2 = CreateExchange("B");
        await pipeline.Process(e2);
        e2.In.Body.Should().Be("B");

        // Message 3 (dropped) — body unchanged
        var e3 = CreateExchange("C");
        await pipeline.Process(e3);
        e3.In.Body.Should().Be("C");

        // Message 4 (passes) — gets transformed
        var e4 = CreateExchange("D");
        await pipeline.Process(e4);
        e4.In.Body.Should().Be("sampled:D");
    }

    // ══════════════════════════════════════════════════════════════
    // Count-based: frequency=1 passes all
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_FrequencyOne_AllPass()
    {
        var def = new RouteDefinition();
        def.From("direct://sample");
        def.Sample(1);
        def.SetHeader("Sampled", "true");

        var pipeline = def.CreateProcessor(_context);

        for (int i = 0; i < 5; i++)
        {
            var exchange = CreateExchange(i);
            await pipeline.Process(exchange);
            exchange.In.Headers.Should().ContainKey("Sampled", $"message {i} should pass with frequency=1");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Time-based: compiled pipeline
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TimeBased_CompiledPipeline_FirstPasses_RestDropped()
    {
        var def = new RouteDefinition();
        def.From("direct://sample-time");
        def.Sample(TimeSpan.FromMinutes(10)); // Very long period
        def.Transform(e => $"sampled:{e.In.Body}");

        var pipeline = def.CreateProcessor(_context);

        // First message passes
        var e1 = CreateExchange("first");
        await pipeline.Process(e1);
        e1.In.Body.Should().Be("sampled:first");

        // Immediate follow-ups are dropped
        for (int i = 0; i < 3; i++)
        {
            var exchange = CreateExchange($"drop-{i}");
            await pipeline.Process(exchange);
            exchange.In.Body.Should().Be($"drop-{i}", "transform should not apply to dropped messages");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Count-based: chaining with subsequent steps
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_ChainsWithFilter()
    {
        var def = new RouteDefinition();
        def.From("direct://sample");
        def.Sample(2); // Every 2nd = 1, 3, 5, ...
        def.Transform(e => $"ok:{e.In.Body}");

        var pipeline = def.CreateProcessor(_context);

        var results = new string?[4];
        for (int i = 0; i < 4; i++)
        {
            var exchange = CreateExchange(i);
            await pipeline.Process(exchange);
            results[i] = exchange.In.Body?.ToString();
        }

        // 1st passes (transformed), 2nd dropped, 3rd passes (transformed), 4th dropped
        results.Should().Equal("ok:0", "1", "ok:2", "3");
    }

    // ══════════════════════════════════════════════════════════════
    // Properties preserved
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_PreservesProperties()
    {
        var def = new RouteDefinition();
        def.From("direct://sample");
        def.Sample(1);

        var pipeline = def.CreateProcessor(_context);

        var exchange = CreateExchange("data");
        exchange.Properties["traceId"] = "abc-123";
        await pipeline.Process(exchange);

        exchange.Properties["traceId"].Should().Be("abc-123");
    }

    // ══════════════════════════════════════════════════════════════
    // Headers preserved on dropped messages
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_DroppedMessage_HeadersPreserved()
    {
        var def = new RouteDefinition();
        def.From("direct://sample");
        def.Sample(100); // Only first passes
        def.SetHeader("Applied", "yes");

        var pipeline = def.CreateProcessor(_context);

        // First passes — header set
        var e1 = CreateExchange("first");
        await pipeline.Process(e1);
        e1.In.Headers.Should().ContainKey("Applied");

        // Second dropped — header NOT set
        var e2 = CreateExchange("second");
        e2.In.Headers["Original"] = "keep";
        await pipeline.Process(e2);
        e2.In.Headers.Should().NotContainKey("Applied");
        e2.In.Headers["Original"].Should().Be("keep");
    }
}
