using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="AggregatorProcessor"/>.</summary>
public class AggregatorProcessorTests
{
    /// <summary>Aggregates exchanges by correlation key and fires on completion.</summary>
    [Fact]
    public async Task Process_AggregatesByKey_FiresOnCompletion()
    {
        IExchange? result = null;
        var target = new DelegateProcessor(ex => result = ex);

        var aggregator = new AggregatorProcessor(
            correlationKey: ex => ex.In.GetHeader<string>("orderId")!,
            aggregationStrategy: (old, @new) =>
            {
                var count = old.GetProperty<int>("count") + 1;
                old.Properties["count"] = count;
                return old;
            },
            completionPredicate: ex => ex.GetProperty<int>("count") >= 2,
            target: target);

        var ex1 = new Exchange(new Message("item-1"));
        ex1.In.Headers["orderId"] = "order-123";
        ex1.Properties["count"] = 1;
        await aggregator.Process(ex1);
        result.Should().BeNull(); // Not complete yet

        var ex2 = new Exchange(new Message("item-2"));
        ex2.In.Headers["orderId"] = "order-123";
        await aggregator.Process(ex2);
        result.Should().NotBeNull(); // Now complete
    }

    /// <summary>Different correlation keys aggregate independently.</summary>
    [Fact]
    public async Task Process_DifferentKeys_IndependentAggregation()
    {
        var results = new List<string>();
        var target = new DelegateProcessor(ex => results.Add(ex.In.GetHeader<string>("key")!));

        var aggregator = new AggregatorProcessor(
            correlationKey: ex => ex.In.GetHeader<string>("key")!,
            aggregationStrategy: (old, _) => old,
            completionPredicate: _ => true, // Complete immediately
            target: target);

        var ex1 = new Exchange(new Message("a"));
        ex1.In.Headers["key"] = "A";
        await aggregator.Process(ex1);

        var ex2 = new Exchange(new Message("b"));
        ex2.In.Headers["key"] = "B";
        await aggregator.Process(ex2);

        results.Should().Equal("A", "B");
    }

    /// <summary>PendingGroupCount tracks active groups.</summary>
    [Fact]
    public async Task PendingGroupCount_TracksActiveGroups()
    {
        var target = new DelegateProcessor(_ => { });
        var aggregator = new AggregatorProcessor(
            correlationKey: ex => ex.In.GetHeader<string>("key")!,
            aggregationStrategy: (old, @new) => old,
            completionPredicate: ex => ex.GetProperty<int>("done") == 1,
            target: target);

        var ex1 = new Exchange(new Message("a"));
        ex1.In.Headers["key"] = "group-1";
        await aggregator.Process(ex1);

        aggregator.PendingGroupCount.Should().Be(1);

        var ex2 = new Exchange(new Message("b"));
        ex2.In.Headers["key"] = "group-2";
        await aggregator.Process(ex2);

        aggregator.PendingGroupCount.Should().Be(2);
    }

    /// <summary>Single exchange completing immediately fires target once.</summary>
    [Fact]
    public async Task Process_ImmediateCompletion_FiresTarget()
    {
        var fired = 0;
        var target = new DelegateProcessor(_ => fired++);

        var aggregator = new AggregatorProcessor(
            correlationKey: _ => "single",
            aggregationStrategy: (old, _) => old,
            completionPredicate: _ => true,
            target: target);

        await aggregator.Process(new Exchange(new Message("data")));

        fired.Should().Be(1);
        aggregator.PendingGroupCount.Should().Be(0);
    }

    // ────────────────── DSL compilation (Phase 0 / T0.3 fixators) ──────────────────
    // See redb.Route/docs/EIP_SCOPE_FIX_PLAN.md § 1.3.

    /// <summary>
    /// T0.3a — Symptom A: completed aggregate must reach the next step in the route.
    /// Today CompileAggregate wires a no-op DelegateProcessor as the AggregatorProcessor
    /// target, so completed aggregates silently disappear. Will be fixed in Phase 2.
    /// </summary>
    [Fact]
    public async Task RouteDsl_Aggregate_CompletedExchange_FlowsToNextStep()
    {
        await using var context = new RouteContext();
        var downstream = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://agg-complete")
                .Aggregate(
                    correlationKey: _ => "k",
                    aggregationStrategy: (old, neu) =>
                    {
                        var sum = (int)(old.In.Body ?? 0) + (int)(neu.In.Body ?? 0);
                        old.In.Body = sum;
                        return old;
                    },
                    completionPredicate: ex => (int)(ex.In.Body ?? 0) >= 3)
                .Process(e => downstream.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://agg-complete").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(1)));
        await producer.Process(new Exchange(new Message(2)));

        downstream.Should().ContainSingle().Which.Should().Be(3);
    }

    /// <summary>
    /// T0.3b — Symptom B: pre-completion input exchanges must NOT leak past the
    /// aggregate step into downstream processors. Today the aggregator consumes the
    /// exchange into its group but PipelineProcessor still routes the original input
    /// further. Will be fixed in Phase 2 (tail-consuming wiring + exchange.Stop()).
    /// </summary>
    [Fact]
    public async Task RouteDsl_Aggregate_PreCompletionExchange_DoesNotLeakToTail()
    {
        await using var context = new RouteContext();
        var downstream = new List<int>();

        context.AddRoutes(r =>
        {
            r.From("direct://agg-leak")
                .Aggregate(
                    correlationKey: _ => "k",
                    aggregationStrategy: (old, neu) =>
                    {
                        var sum = (int)(old.In.Body ?? 0) + (int)(neu.In.Body ?? 0);
                        old.In.Body = sum;
                        return old;
                    },
                    completionPredicate: ex => (int)(ex.In.Body ?? 0) >= 100)
                .Process(e => downstream.Add((int)e.In.Body!));
        });
        await context.Start();

        var producer = context.GetEndpoint("direct://agg-leak").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message(1)));
        await producer.Process(new Exchange(new Message(2)));

        // Group is not complete (sum=3 < 100), so nothing should flow downstream.
        downstream.Should().BeEmpty();
    }
}
