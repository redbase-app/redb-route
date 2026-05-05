using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Processors;

/// <summary>Tests for <see cref="ChoiceProcessor"/> and <see cref="WhenClause"/>.</summary>
public class ChoiceProcessorTests
{
    /// <summary>First matching when-clause executes.</summary>
    [Fact]
    public async Task Process_FirstMatchingWhen_Executes()
    {
        var result = "";
        var choice = new ChoiceProcessor()
            .When(ex => ex.In.Body is "A", new DelegateProcessor(_ => result = "branch-A"))
            .When(ex => ex.In.Body is "B", new DelegateProcessor(_ => result = "branch-B"));

        await choice.Process(new Exchange(new Message("A")));

        result.Should().Be("branch-A");
    }

    /// <summary>Second match is used when first doesn't match.</summary>
    [Fact]
    public async Task Process_SecondMatch_WhenFirstFails()
    {
        var result = "";
        var choice = new ChoiceProcessor()
            .When(ex => ex.In.Body is "A", new DelegateProcessor(_ => result = "branch-A"))
            .When(ex => ex.In.Body is "B", new DelegateProcessor(_ => result = "branch-B"));

        await choice.Process(new Exchange(new Message("B")));

        result.Should().Be("branch-B");
    }

    /// <summary>Only first matching executes (short-circuit).</summary>
    [Fact]
    public async Task Process_OnlyFirstMatch_ShortCircuits()
    {
        var count = 0;
        var choice = new ChoiceProcessor()
            .When(_ => true, new DelegateProcessor(_ => count++))
            .When(_ => true, new DelegateProcessor(_ => count++));

        await choice.Process(new Exchange());

        count.Should().Be(1);
    }

    /// <summary>Otherwise executes when no when-clause matches.</summary>
    [Fact]
    public async Task Process_NoMatch_Otherwise_Executes()
    {
        var result = "";
        var choice = new ChoiceProcessor()
            .When(ex => ex.In.Body is "A", new DelegateProcessor(_ => result = "A"))
            .SetOtherwise(new DelegateProcessor(_ => result = "otherwise"));

        await choice.Process(new Exchange(new Message("X")));

        result.Should().Be("otherwise");
    }

    /// <summary>No match and no otherwise — nothing happens.</summary>
    [Fact]
    public async Task Process_NoMatch_NoOtherwise_DoesNothing()
    {
        var executed = false;
        var choice = new ChoiceProcessor()
            .When(ex => ex.In.Body is "A", new DelegateProcessor(_ => executed = true));

        await choice.Process(new Exchange(new Message("X")));

        executed.Should().BeFalse();
    }

    /// <summary>WhenClauses property returns added clauses.</summary>
    [Fact]
    public void WhenClauses_ReturnsAll()
    {
        var choice = new ChoiceProcessor()
            .When(_ => true, new DelegateProcessor(_ => { }))
            .When(_ => false, new DelegateProcessor(_ => { }));

        choice.WhenClauses.Should().HaveCount(2);
    }

    /// <summary>WhenClause validates arguments.</summary>
    [Fact]
    public void WhenClause_NullPredicate_Throws()
    {
        var act = () => new WhenClause(null!, new DelegateProcessor(_ => { }));
        act.Should().Throw<ArgumentNullException>();
    }
}
