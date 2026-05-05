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
}
