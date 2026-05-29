using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Processors;

namespace redb.Route.Tests.Definitions;

/// <summary>
/// Tests for W5 F6 — SplitDefinition and MulticastDefinition.
/// </summary>
public class SplitMulticastDefinitionTests : IAsyncDisposable
{
    private readonly RouteContext _context = new();

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private static Exchange MakeExchange(object? body = null)
        => new Exchange(new Message { Body = body });

    // ── SplitDefinition structure ─────────────────────────────────────────────

    [Fact]
    public void Split_ReturnsASplitDefinition()
    {
        var route = new RouteDefinition();
        route.Split(_ => []).Should().BeOfType<SplitDefinition>();
    }

    [Fact]
    public void Split_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.Split(_ => []);
        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<SplitDefinition>();
    }

    [Fact]
    public void Split_SetsParent()
    {
        var route = new RouteDefinition();
        var split = route.Split(_ => []);
        split.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void SplitDefinition_EndSplit_ReturnsParent()
    {
        var route = new RouteDefinition();
        var back = route.Split(_ => []).EndSplit();
        back.Should().BeSameAs(route);
    }

    [Fact]
    public void SplitDefinition_EndSplit_WithoutParent_Throws()
    {
        var split = new SplitDefinition(_ => []);
        split.Invoking(s => s.EndSplit()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SplitDefinition_CreateProcessor_ReturnsSplitterProcessor()
    {
        var split = new SplitDefinition(_ => []);
        split.Process(_ => { });
        split.CreateProcessor(_context).Should().BeOfType<SplitterProcessor>();
    }

    // ── SplitDefinition execution ─────────────────────────────────────────────

    [Fact]
    public async Task Split_ProcessesEachPart()
    {
        var log = new List<object?>();
        var route = new RouteDefinition()
            .Split(e => (e.In.Body as IEnumerable<object?>)!)
                .Process(e => { log.Add(e.In.Body); })
            .EndSplit();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        var ex = MakeExchange(new object?[] { "a", "b", "c" });
        await processor.Process(ex);
        log.Should().Equal("a", "b", "c");
    }

    [Fact]
    public async Task Split_EmptyList_ProcessesNothing()
    {
        int count = 0;
        var route = new RouteDefinition()
            .Split(_ => Array.Empty<object?>())
                .Process(_ => { count++; })
            .EndSplit();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        count.Should().Be(0);
    }

    [Fact]
    public async Task StepsAfterEndSplit_ExecuteOnce()
    {
        int afterCount = 0;
        var route = new RouteDefinition()
            .Split(e => (e.In.Body as IEnumerable<object?>)!)
                .Process(_ => { })
            .EndSplit()
            .Process(_ => { afterCount++; });

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange(new object?[] { "x", "y" }));
        afterCount.Should().Be(1);
    }

    // ── MulticastDefinition structure ─────────────────────────────────────────

    [Fact]
    public void Multicast_ReturnsMulticastDefinition()
    {
        var route = new RouteDefinition();
        route.Multicast().Should().BeOfType<MulticastDefinition>();
    }

    [Fact]
    public void Multicast_IsAddedToRouteOutputs()
    {
        var route = new RouteDefinition();
        route.Multicast();
        route.Outputs.Should().HaveCount(1);
        route.Outputs[0].Should().BeOfType<MulticastDefinition>();
    }

    [Fact]
    public void Multicast_SetsParent()
    {
        var route = new RouteDefinition();
        var mc = route.Multicast();
        mc.Parent.Should().BeSameAs(route);
    }

    [Fact]
    public void MulticastDefinition_EndMulticast_ReturnsParent()
    {
        var route = new RouteDefinition();
        var back = route.Multicast().EndMulticast();
        back.Should().BeSameAs(route);
    }

    [Fact]
    public void MulticastDefinition_CreateProcessor_ReturnsMulticastProcessor()
    {
        var mc = new MulticastDefinition();
        mc.Process(_ => { });
        mc.CreateProcessor(_context).Should().BeOfType<MulticastProcessor>();
    }

    // ── MulticastDefinition execution ─────────────────────────────────────────

    [Fact]
    public async Task Multicast_Sequential_SendsToAllTargets()
    {
        var log = new List<string>();
        var route = new RouteDefinition()
            .Multicast().Sequential()
                .Process(_ => { log.Add("target1"); })
                .Process(_ => { log.Add("target2"); })
            .EndMulticast();

        var processor = ((RouteDefinition)route).CreateProcessor(_context);
        await processor.Process(MakeExchange());
        log.Should().Equal("target1", "target2");
    }
}
