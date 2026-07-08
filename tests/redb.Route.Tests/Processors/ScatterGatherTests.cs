using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>
/// Unit tests for Scatter-Gather DSL, step creation, and builder validation.
/// Full pipeline tests are in <see cref="ScatterGatherIntegrationTests"/>.
/// </summary>
public class ScatterGatherTests
{
    // ══════════════════════════════════════════════════════════════
    // DSL — simple overload
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Simple_AddsStep()
    {
        var def = new RouteDefinition();

        Func<IExchange, IExchange, IExchange> agg = (a, b) => a;
        def.ScatterGather(agg, "http://a:8080", "http://b:8080");

        var sgDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<ScatterGatherDefinition>().Subject;
        sgDef.StaticRecipients.Should().BeEquivalentTo(["http://a:8080", "http://b:8080"]);
        sgDef.DynamicRecipients.Should().BeNull();
        sgDef.AggregationStrategy.Should().BeSameAs(agg);
        sgDef.ParallelProcessing.Should().BeTrue();
        sgDef.Timeout.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void DSL_Simple_NullStrategy_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.ScatterGather(null!, "http://a:8080");
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void DSL_Simple_NoRecipients_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.ScatterGather((a, b) => a);
        act.Should().Throw<ArgumentException>();
    }

    // ══════════════════════════════════════════════════════════════
    // DSL — builder overload
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void DSL_Builder_FullConfig_AddsStep()
    {
        var def = new RouteDefinition();

        Func<IExchange, IExchange, IExchange> agg = (a, b) => a;
        def.ScatterGather(sg => sg
            .Recipients("http://a:8080", "http://b:8080", "http://c:8080")
            .AggregationStrategy(agg)
            .Timeout(TimeSpan.FromSeconds(5))
            .ParallelProcessing(false)
            .MaxDegreeOfParallelism(2)
            .StopOnException());

        var sgDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<ScatterGatherDefinition>().Subject;
        sgDef.StaticRecipients.Should().HaveCount(3);
        sgDef.DynamicRecipients.Should().BeNull();
        sgDef.AggregationStrategy.Should().BeSameAs(agg);
        sgDef.ParallelProcessing.Should().BeFalse();
        sgDef.MaxDegreeOfParallelism.Should().Be(2);
        sgDef.StopOnException.Should().BeTrue();
        sgDef.Timeout.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DSL_Builder_DynamicRecipients_AddsStep()
    {
        var def = new RouteDefinition();

        Func<IExchange, IEnumerable<string>> factory = e =>
            e.In.GetHeader<string>("targets")!.Split(',');

        def.ScatterGather(sg => sg
            .Recipients(factory)
            .AggregationStrategy((a, b) => a));

        var sgDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<ScatterGatherDefinition>().Subject;
        sgDef.StaticRecipients.Should().BeNull();
        sgDef.DynamicRecipients.Should().BeSameAs(factory);
    }

    [Fact]
    public void DSL_Builder_NoStrategy_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.ScatterGather(sg => sg
            .Recipients("http://a:8080"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AggregationStrategy*");
    }

    [Fact]
    public void DSL_Builder_NoRecipients_Throws()
    {
        var def = new RouteDefinition();

        var act = () => def.ScatterGather(sg => sg
            .AggregationStrategy((a, b) => a));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Recipients*");
    }

    [Fact]
    public void DSL_Builder_NegativeTimeout_Throws()
    {
        var act = () =>
        {
            var def = new RouteDefinition();
            def.ScatterGather(sg => sg
                .Recipients("http://a:8080")
                .AggregationStrategy((a, b) => a)
                .Timeout(TimeSpan.FromSeconds(-1)));
        };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DSL_Builder_NegativeMaxDop_Throws()
    {
        var act = () =>
        {
            var def = new RouteDefinition();
            def.ScatterGather(sg => sg
                .Recipients("http://a:8080")
                .AggregationStrategy((a, b) => a)
                .MaxDegreeOfParallelism(-1));
        };

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Builder — Recipients override
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Builder_StaticRecipients_OverridesDynamic()
    {
        var def = new RouteDefinition();

        def.ScatterGather(sg => sg
            .Recipients(e => ["http://dynamic:8080"])
            .Recipients("http://static:8080")               // override: static wins
            .AggregationStrategy((a, b) => a));

        var sgDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<ScatterGatherDefinition>().Subject;
        sgDef.StaticRecipients.Should().NotBeNull();
        sgDef.DynamicRecipients.Should().BeNull();
    }

    [Fact]
    public void Builder_DynamicRecipients_OverridesStatic()
    {
        var def = new RouteDefinition();

        Func<IExchange, IEnumerable<string>> factory = e => ["http://dynamic:8080"];
        def.ScatterGather(sg => sg
            .Recipients("http://static:8080")
            .Recipients(factory)                             // override: dynamic wins
            .AggregationStrategy((a, b) => a));

        var sgDef = def.Outputs.Should().ContainSingle().Which.Should().BeOfType<ScatterGatherDefinition>().Subject;
        sgDef.StaticRecipients.Should().BeNull();
        sgDef.DynamicRecipients.Should().BeSameAs(factory);
    }

    // ══════════════════════════════════════════════════════════════
    // Constructor validation
    // ══════════════════════════════════════════════════════════════

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new ScatterGatherProcessor(
            null!, ["http://a:8080"], (a, b) => a);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRecipients_Throws()
    {
        var context = new RouteContext();
        var act = () => new ScatterGatherProcessor(
            context, (IReadOnlyList<string>)null!, (a, b) => a);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_EmptyRecipients_Throws()
    {
        var context = new RouteContext();
        var act = () => new ScatterGatherProcessor(
            context, Array.Empty<string>(), (a, b) => a);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullAggregation_Throws()
    {
        var context = new RouteContext();
        var act = () => new ScatterGatherProcessor(
            context, ["http://a:8080"], null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullDynamicFactory_Throws()
    {
        var context = new RouteContext();
        var act = () => new ScatterGatherProcessor(
            context, (Func<IExchange, IEnumerable<string>>)null!, (a, b) => a);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NegativeTimeout_Throws()
    {
        var context = new RouteContext();
        var act = () => new ScatterGatherProcessor(
            context, ["http://a:8080"], (a, b) => a,
            timeout: TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ══════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════

    private static IExchange CreateExchange(object? body = null) =>
        new Exchange(new Message { Body = body ?? "test" });
}
