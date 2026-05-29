using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F5 — ChoiceDefinition / WhenDefinition / OtherwiseDefinition.
/// </summary>
public class ChoiceDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── Structure ─────────────────────────────────────────────────────────────

    [Fact]
    public void Choice_ReturnsChoiceDefinition()
    {
        var route = new RouteDefinition();
        route.Choice().Should().BeOfType<ChoiceDefinition>();
    }

    [Fact]
    public void Choice_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.Choice();
        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<ChoiceDefinition>();
    }

    [Fact]
    public void Choice_SetsParent()
    {
        var route = new RouteDefinition();
        var choice = route.Choice();
        choice.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void When_ReturnsWhenDefinition()
    {
        var choice = new RouteDefinition().Choice();
        choice.When(_ => true).Should().BeOfType<WhenDefinition>();
    }

    [Fact]
    public void Otherwise_ReturnsOtherwiseDefinition()
    {
        var choice = new RouteDefinition().Choice();
        choice.Otherwise().Should().BeOfType<OtherwiseDefinition>();
    }

    [Fact]
    public void Otherwise_CalledTwice_Throws()
    {
        var choice = new RouteDefinition().Choice();
        choice.Otherwise();
        choice.Invoking(c => c.Otherwise()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void EndChoice_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var back = route.Choice().EndChoice();
        back.Should().BeSameAs(route);
    }

    [Fact]
    public void WhenDefinition_EndWhen_ReturnsChoiceDefinition()
    {
        var choice = new RouteDefinition().Choice();
        var when = choice.When(_ => true);
        when.EndWhen().Should().BeSameAs(choice);
    }

    [Fact]
    public void WhenDefinition_EndChoice_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var when = route.Choice().When(_ => true);
        when.EndChoice().Should().BeSameAs(route);
    }

    [Fact]
    public void WhenDefinition_When_AddsBranchToChoice()
    {
        var route = new RouteDefinition();
        var choice = route.Choice();
        var when1 = choice.When(_ => true);
        var when2 = when1.When(_ => false);
        when2.Should().BeOfType<WhenDefinition>();
        when2.Should().NotBeSameAs(when1);
    }

    [Fact]
    public void OtherwiseDefinition_EndOtherwise_ReturnsChoice()
    {
        var choice = new RouteDefinition().Choice();
        var otherwise = choice.Otherwise();
        otherwise.EndOtherwise().Should().BeSameAs(choice);
    }

    [Fact]
    public void OtherwiseDefinition_EndChoice_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var otherwise = route.Choice().Otherwise();
        otherwise.EndChoice().Should().BeSameAs(route);
    }

    // ── CreateProcessor ───────────────────────────────────────────────────────

    [Fact]
    public void ChoiceDefinition_CreateProcessor_ReturnsChoiceProcessor()
    {
        var choice = new RouteDefinition().Choice();
        choice.When(_ => true).Process(_ => { });
        choice.CreateProcessor(_context).Should().BeOfType<ChoiceProcessor>();
    }

    // ── Execution: routing ────────────────────────────────────────────────────

    [Fact]
    public async Task Choice_FirstMatchingWhen_Executes()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Choice()
                .When(e => e.In.Body is "A")
                    .Process(_ => { log.Add("branch-A"); })
                .When(e => e.In.Body is "B")
                    .Process(_ => { log.Add("branch-B"); })
            .EndChoice();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);

        await processor.Process(MakeExchange("A"));
        await processor.Process(MakeExchange("B"));

        log.Should().Equal("branch-A", "branch-B");
    }

    [Fact]
    public async Task Choice_NoMatch_ExecutesOtherwise()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Choice()
                .When(e => e.In.Body is "A")
                    .Process(_ => { log.Add("branch-A"); })
                .Otherwise()
                    .Process(_ => { log.Add("otherwise"); })
            .EndChoice();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange("Z"));
        log.Should().Equal("otherwise");
    }

    [Fact]
    public async Task Choice_NoMatch_NoOtherwise_DoesNothing()
    {
        bool reached = false;
        var route = new RouteDefinition()
            .Choice()
                .When(e => e.In.Body is "A")
                    .Process(_ => { reached = true; })
            .EndChoice();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange("Z"));
        reached.Should().BeFalse();
    }

    [Fact]
    public async Task Choice_OnlyFirstMatchingWhen_Executes()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Choice()
                .When(_ => true)
                    .Process(_ => { log.Add("first"); })
                .When(_ => true)
                    .Process(_ => { log.Add("second"); })
            .EndChoice();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        log.Should().Equal("first");
    }

    [Fact]
    public async Task StepsAfterEndChoice_ExecuteRegardlessOfBranch()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Choice()
                .When(e => e.In.Body is "A")
                    .Process(_ => { log.Add("inside"); })
            .EndChoice()
            .Process(_ => { log.Add("after"); });

        var processor = ((RouteDefinition)route).CreateProcessor(_context);

        await processor.Process(MakeExchange("Z"));
        log.Should().Equal("after");
    }

    [Fact]
    public async Task When_MultipleSteps_AllExecute()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Choice()
                .When(_ => true)
                    .Process(_ => { log.Add("step1"); })
                    .Process(_ => { log.Add("step2"); })
            .EndChoice();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        log.Should().Equal("step1", "step2");
    }
}
