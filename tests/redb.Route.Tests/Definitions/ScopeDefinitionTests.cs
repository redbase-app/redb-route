using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F4 — FilterDefinition and IdempotentConsumerDefinition.
/// </summary>
public class ScopeDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── FilterDefinition ─────────────────────────────────────────────────────

    [Fact]
    public void Filter_ReturnsFilterDefinition()
    {
        var route = new RouteDefinition();
        var filter = route.Filter(_ => true);
        filter.Should().BeOfType<FilterDefinition>();
    }

    [Fact]
    public void Filter_SetsParentOnFilterDefinition()
    {
        var route = new RouteDefinition();
        var filter = route.Filter(_ => true);
        filter.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void Filter_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.Filter(_ => true);
        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<FilterDefinition>();
    }

    [Fact]
    public void FilterDefinition_EndFilter_ReturnsParentRoute()
    {
        var route = new RouteDefinition();
        var filter = route.Filter(_ => true);
        var back = filter.EndFilter();
        back.Should().BeSameAs(route);
    }

    [Fact]
    public void FilterDefinition_EndFilter_WithoutParent_Throws()
    {
        var filter = new FilterDefinition(_ => true);
        var act = () => filter.EndFilter();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FilterDefinition_LeafMethods_ReturnSelf()
    {
        var filter = new FilterDefinition(_ => true);
        filter.Process(_ => { }).Should().BeSameAs(filter);
        filter.To("direct:x").Should().BeSameAs(filter);
        filter.SetBody("v").Should().BeSameAs(filter);
        filter.Stop().Should().BeSameAs(filter);
    }

    [Fact]
    public async Task FilterDefinition_PredicateTrue_PassesExchange()
    {
        bool processed = false;
        var route = new RouteDefinition()
            .Filter(_ => true)
                .Process(_ => { processed = true; })
            .EndFilter();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        processed.Should().BeTrue();
    }

    [Fact]
    public async Task FilterDefinition_PredicateFalse_SkipsExchange()
    {
        bool processed = false;
        var route = new RouteDefinition()
            .Filter(_ => false)
                .Process(_ => { processed = true; })
            .EndFilter();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        processed.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_StepsAfterEndFilter_ExecuteRegardlessOfPredicate()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Filter(_ => false)
                .Process(_ => { log.Add("inside"); })
            .EndFilter()
            .Process(_ => { log.Add("after"); });

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        log.Should().Equal("after");
    }

    [Fact]
    public void FilterDefinition_CreateProcessor_ReturnsFilterProcessor()
    {
        var filter = new FilterDefinition(_ => true);
        filter.Process(_ => { });
        filter.CreateProcessor(_context).Should().BeOfType<FilterProcessor>();
    }

    // ── IdempotentConsumerDefinition ──────────────────────────────────────────

    [Fact]
    public void IdempotentConsumer_ReturnsIdempotentConsumerDefinition()
    {
        var repo = new InMemoryIdempotentRepository();
        var route = new RouteDefinition();
        var ic = route.IdempotentConsumer(repo, e => e.In.GetHeader<string>("id") ?? string.Empty);
        ic.Should().BeOfType<IdempotentConsumerDefinition>();
    }

    [Fact]
    public void IdempotentConsumer_SetsParent()
    {
        var repo = new InMemoryIdempotentRepository();
        var route = new RouteDefinition();
        var ic = route.IdempotentConsumer(repo, e => e.In.GetHeader<string>("id") ?? string.Empty);
        ic.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void IdempotentConsumerDefinition_EndIdempotentConsumer_ReturnsParent()
    {
        var repo = new InMemoryIdempotentRepository();
        var route = new RouteDefinition();
        var ic = route.IdempotentConsumer(repo, e => e.In.GetHeader<string>("id") ?? string.Empty);
        ic.EndIdempotentConsumer().Should().BeSameAs(route);
    }

    [Fact]
    public async Task IdempotentConsumer_DeduplicatesDuplicateMessages()
    {
        var repo = new InMemoryIdempotentRepository();
        int processCount = 0;
        var route = new RouteDefinition()
            .IdempotentConsumer(repo, e => e.In.GetHeader<string>("id") ?? string.Empty)
                .Process(_ => { processCount++; })
            .EndIdempotentConsumer();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);

        var ex1 = MakeExchange();
        ex1.In.setHeader("id", "msg-1");
        await processor.Process(ex1);

        var ex2 = MakeExchange();
        ex2.In.setHeader("id", "msg-1");
        await processor.Process(ex2);

        processCount.Should().Be(1);
    }

    [Fact]
    public async Task IdempotentConsumer_ProcessesUniqueMessages()
    {
        var repo = new InMemoryIdempotentRepository();
        int processCount = 0;
        var route = new RouteDefinition()
            .IdempotentConsumer(repo, e => e.In.GetHeader<string>("id") ?? string.Empty)
                .Process(_ => { processCount++; })
            .EndIdempotentConsumer();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);

        for (int i = 1; i <= 3; i++)
        {
            var ex = MakeExchange();
            ex.In.setHeader("id", $"msg-{i}");
            await processor.Process(ex);
        }

        processCount.Should().Be(3);
    }
}
