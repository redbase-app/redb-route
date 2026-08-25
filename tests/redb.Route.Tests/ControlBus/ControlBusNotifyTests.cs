using System.Collections.Concurrent;
using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.ControlBus;
using redb.Route.Core;

namespace redb.Route.Tests.ControlBus;

/// <summary>
/// Tests for the <c>controlbus:notify</c> event consumer (redb extension beyond Camel): route/context
/// lifecycle events surfaced as exchanges into a route, with optional event/route filters.
/// </summary>
public class ControlBusNotifyTests
{
    private static async Task<bool> Eventually(Func<bool> condition, int seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    [Fact]
    public async Task Notify_EmitsRouteStoppedEvent()
    {
        var events = new ConcurrentBag<(string ev, string? routeId)>();

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://worker").RouteId("worker").Process(_ => { });
            r.From("controlbus:notify").RouteId("notify")
                .Process(e => events.Add((
                    e.In.GetHeader<string>(ControlBusHeaders.Event)!,
                    e.In.GetHeader<string>(ControlBusHeaders.RouteId))));
        });

        await ctx.Start();
        await ctx.StopRoute("worker");

        (await Eventually(() => events.Any(x => x.ev == "RouteStopped" && x.routeId == "worker")))
            .Should().BeTrue("stopping 'worker' should surface a RouteStopped event on controlbus:notify");
    }

    [Fact]
    public async Task Notify_EventsFilter_OnlyMatchingEvents()
    {
        var events = new ConcurrentBag<string>();

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://w").RouteId("w").Process(_ => { });
            r.From("controlbus:notify?events=RouteStarted").RouteId("n")
                .Process(e => events.Add(e.In.GetHeader<string>(ControlBusHeaders.Event)!));
        });

        await ctx.Start();
        await ctx.StopRoute("w");                 // RouteStopped — must be filtered out
        await ctx.StartRoute("w");                // RouteStarted — must be captured

        (await Eventually(() => events.Contains("RouteStarted"))).Should().BeTrue();
        events.Should().NotContain("RouteStopped");
    }

    [Fact]
    public async Task Notify_RouteFilter_OnlyThatRoute()
    {
        var routeIds = new ConcurrentBag<string?>();

        await using var ctx = new RouteContext();
        ctx.AddRoutes(r =>
        {
            r.From("direct://a").RouteId("a").Process(_ => { });
            r.From("direct://b").RouteId("b").Process(_ => { });
            r.From("controlbus:notify?routeId=a").RouteId("n2")
                .Process(e => routeIds.Add(e.In.GetHeader<string>(ControlBusHeaders.RouteId)));
        });

        await ctx.Start();
        await ctx.StopRoute("a");
        await ctx.StopRoute("b");

        (await Eventually(() => routeIds.Any(x => x == "a"))).Should().BeTrue();
        routeIds.Should().NotContain("b");
    }

    [Fact]
    public async Task Notify_IsConsumerOnly_ProducerThrows()
    {
        await using var ctx = new RouteContext();
        var endpoint = ctx.GetEndpoint("controlbus:notify");
        var act = () => endpoint.CreateProducer();
        act.Should().Throw<NotSupportedException>();
        await Task.CompletedTask;
    }
}
