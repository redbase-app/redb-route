using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// An exchange is in flight until its last entry into a route has finished. A copy keeps the exchange id, so an exchange
/// entering a route again (a route calling itself through <c>direct:</c>, parallel branches through the same sub-route)
/// registers the same id more than once; the repository counts the entries, and the first to finish no longer takes
/// the exchange off while the others still run. It used to: the dashboard showed nothing in flight under load.
/// </summary>
public class InflightReentryTests
{
    private static InflightExchange Entry(string id)
        => new(id, "route-1", DateTime.UtcNow, Environment.CurrentManagedThreadId, "direct:in");

    [Fact]
    public void An_exchange_registered_twice_stays_in_flight_until_both_entries_finish()
    {
        var repository = new DefaultInflightRepository();
        repository.Register(Entry("ex-1"));
        repository.Register(Entry("ex-1"));

        repository.Unregister("ex-1");
        repository.Count.Should().Be(1, "the other entry is still running");
        repository.Browse().Should().ContainSingle();

        repository.Unregister("ex-1");
        repository.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_route_re_entered_by_the_same_exchange_shows_it_in_flight_until_the_outer_run_ends()
    {
        await using var context = new RouteContext($"inflight-reentry-{Guid.NewGuid():N}");
        context.AddComponent(new DirectComponent());
        int? inFlightAfterInnerRun = null;
        context.AddRoutes(r =>
            r.From("direct://inflight-reentry").RouteId("inflight-reentry")
                .Choice()
                    .When(e => e.In.GetHeader<string>("depth") == "outer")
                        .SetHeader("depth", "inner")
                        .To("direct://inflight-reentry")
                        .Process(_ => inFlightAfterInnerRun = context.InflightRepository.CountByRoute("inflight-reentry"))
                .End());
        await context.Start();
        var template = new ProducerTemplate(context);
        template.Start();

        var message = new Message("x");
        message.Headers["depth"] = "outer";
        await template.SendAsync("direct://inflight-reentry", message);
        await template.DisposeAsync();

        inFlightAfterInnerRun.Should().Be(1, "the outer run of the exchange is still in flight");
    }
}
