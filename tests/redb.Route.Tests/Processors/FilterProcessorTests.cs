using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="FilterProcessor"/>.</summary>
public class FilterProcessorTests
{
    /// <summary>When predicate is true, next processor executes.</summary>
    [Fact]
    public async Task Process_PredicateTrue_ExecutesNext()
    {
        var executed = false;
        var next = new DelegateProcessor(_ => executed = true);
        var filter = new FilterProcessor(_ => true, next);

        await filter.Process(new Exchange(new Message("data")));

        executed.Should().BeTrue();
    }

    /// <summary>When predicate is false, next processor is skipped.</summary>
    [Fact]
    public async Task Process_PredicateFalse_SkipsNext()
    {
        var executed = false;
        var next = new DelegateProcessor(_ => executed = true);
        var filter = new FilterProcessor(_ => false, next);

        await filter.Process(new Exchange(new Message("data")));

        executed.Should().BeFalse();
    }

    /// <summary>Predicate receives the current exchange.</summary>
    [Fact]
    public async Task Process_PredicateReceivesExchange()
    {
        var filter = new FilterProcessor(
            ex => ex.In.Body is string s && s.StartsWith("ok"),
            new DelegateProcessor(ex => ex.In.Body = "passed"));

        var exchange = new Exchange(new Message("ok-data"));
        await filter.Process(exchange);

        exchange.In.Body.Should().Be("passed");
    }

    /// <summary>Predicate can inspect headers.</summary>
    [Fact]
    public async Task Process_FilterByHeader()
    {
        var executed = false;
        var filter = new FilterProcessor(
            ex => ex.In.GetHeader<string>("type") == "important",
            new DelegateProcessor(_ => executed = true));

        var msg = new Message("body");
        msg.Headers["type"] = "important";
        await filter.Process(new Exchange(msg));

        executed.Should().BeTrue();
    }

    /// <summary>Null predicate throws.</summary>
    [Fact]
    public void Constructor_NullPredicate_Throws()
    {
        var act = () => new FilterProcessor(null!, new DelegateProcessor(_ => { }));
        act.Should().Throw<ArgumentNullException>();
    }

    /// <summary>Null next processor throws.</summary>
    [Fact]
    public void Constructor_NullNext_Throws()
    {
        var act = () => new FilterProcessor(_ => true, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ────────────────── DSL compilation (Phase 0 / T0.1 fixators) ──────────────────
    // See redb.Route/docs/EIP_SCOPE_FIX_PLAN.md § 1.1.
    // These tests validate end-to-end behaviour of the .Filter(...) DSL when compiled
    // by OldRouteCompiler and executed through PipelineProcessor. They should both PASS
    // once Phase 1 ships (CompileFilter wired through FilterProcessor + scope form).

    /// <summary>T0.1a — Filter with passing predicate must allow downstream steps to run.</summary>
    [Fact]
    public async Task RouteDsl_Filter_PassThrough_NextStepRuns()
    {
        await using var context = new RouteContext();
        var received = new List<object?>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-pass")
                .Filter(_ => true)
                .Process(e => received.Add(e.In.Body));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-pass").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("payload")));

        received.Should().ContainSingle().Which.Should().Be("payload");
    }

    /// <summary>
    /// Scope-form Filter(p, body): body runs only when predicate is true,
    /// subsequent steps run for ALL exchanges regardless of predicate.
    /// </summary>
    [Fact]
    public async Task RouteDsl_FilterScope_BodyConditional_TailUnconditional()
    {
        await using var context = new RouteContext();
        var inBody = new List<int>();
        var afterBody = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://flt-scope")
                .Filter(e => (int)e.In.Body! % 2 == 0, b => b
                    .Process(e => inBody.Add((int)e.In.Body!)))
                .Process(e => afterBody.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://flt-scope").CreateProducer();
        await producer.Start();
        foreach (var i in new[] { 1, 2, 3, 4 })
            await producer.Process(new Exchange(new Message(i)));

        inBody.Should().Equal(2, 4);
        afterBody.Should().Equal(1, 2, 3, 4);
    }
}
