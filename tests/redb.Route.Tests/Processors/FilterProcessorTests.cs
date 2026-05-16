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
}
