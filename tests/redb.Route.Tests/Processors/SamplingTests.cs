using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Sampling processor, DSL, and step creation.
/// </summary>
public class SamplingTests
{
    private static IExchange CreateExchange(object? body = null)
        => Exchange.Create(new Message(body), null);

    // ══════════════════════════════════════════════════════════════
    // Constructor validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void CountBased_ZeroFrequency_Throws()
    {
        var act = () => new SamplingProcessor(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void CountBased_NegativeFrequency_Throws()
    {
        var act = () => new SamplingProcessor(-5);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TimeBased_ZeroPeriod_Throws()
    {
        var act = () => new SamplingProcessor(TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TimeBased_NegativePeriod_Throws()
    {
        var act = () => new SamplingProcessor(TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Count-based: frequency=1 (passes all)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_FrequencyOne_PassesAll()
    {
        var processor = new SamplingProcessor(1);

        for (int i = 0; i < 10; i++)
        {
            var exchange = CreateExchange($"msg-{i}");
            await processor.Process(exchange);
            exchange.IsStopped.Should().BeFalse($"message {i} should pass with frequency=1");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // Count-based: frequency=3 (every 3rd)
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_FrequencyThree_PassesEveryThird()
    {
        var processor = new SamplingProcessor(3);
        var results = new bool[9];

        for (int i = 0; i < 9; i++)
        {
            var exchange = CreateExchange(i);
            await processor.Process(exchange);
            results[i] = !exchange.IsStopped;
        }

        // Messages 1,4,7 (0-indexed: 0,3,6) should pass
        results.Should().Equal(true, false, false, true, false, false, true, false, false);
    }

    // ══════════════════════════════════════════════════════════════
    // Count-based: first message always passes
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task CountBased_FirstMessageAlwaysPasses()
    {
        var processor = new SamplingProcessor(100);

        var exchange = CreateExchange("first");
        await processor.Process(exchange);

        exchange.IsStopped.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    // Time-based: first message always passes
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TimeBased_FirstMessageAlwaysPasses()
    {
        var processor = new SamplingProcessor(TimeSpan.FromHours(1));

        var exchange = CreateExchange("first");
        await processor.Process(exchange);

        exchange.IsStopped.Should().BeFalse();
    }

    // ══════════════════════════════════════════════════════════════
    // Time-based: rapid messages are dropped
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public async Task TimeBased_RapidMessages_DroppedAfterFirst()
    {
        var processor = new SamplingProcessor(TimeSpan.FromMinutes(10));

        var first = CreateExchange("first");
        await processor.Process(first);
        first.IsStopped.Should().BeFalse("first message passes");

        // Immediately send more — they should all be dropped
        for (int i = 0; i < 5; i++)
        {
            var exchange = CreateExchange($"rapid-{i}");
            await processor.Process(exchange);
            exchange.IsStopped.Should().BeTrue($"rapid message {i} should be dropped");
        }
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — step recording
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_SampleCount_AddsSampleCountStep()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        def.Sample(5);

        def.Steps.Should().ContainSingle(s => s is SampleCountStep);
        var step = def.Steps.OfType<SampleCountStep>().Single();
        step.MessageFrequency.Should().Be(5);
    }

    [Fact]
    public void DSL_SamplePeriod_AddsSamplePeriodStep()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        def.Sample(TimeSpan.FromSeconds(30));

        def.Steps.Should().ContainSingle(s => s is SamplePeriodStep);
        var step = def.Steps.OfType<SamplePeriodStep>().Single();
        step.Period.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DSL_SampleCount_ZeroFrequency_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var act = () => def.Sample(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DSL_SamplePeriod_ZeroPeriod_Throws()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var act = () => def.Sample(TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DSL_SampleCount_Chaining()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var result = def.Sample(10);

        result.Should().BeSameAs(def);
    }

    [Fact]
    public void DSL_SamplePeriod_Chaining()
    {
        var def = new RouteDefinition();
        def.From("direct://test");

        var result = def.Sample(TimeSpan.FromSeconds(1));

        result.Should().BeSameAs(def);
    }
}
