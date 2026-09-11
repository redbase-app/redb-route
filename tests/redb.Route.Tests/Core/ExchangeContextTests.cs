using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// <see cref="IExchange.Context"/> — Apache Camel's <c>exchange.getContext()</c>, the seam that
/// lets a processor reach measurements and services without capturing the context into a lambda
/// (METRICS_IN_ROUTE_PLAN, П1). Stamped by the route wrapper on entry, restored on exit, so
/// "current" always means the route actually executing — inside a <c>direct:</c> sub-route the
/// inner route's context, and the caller's own again after the sub-route returns.
/// </summary>
public sealed class ExchangeContextTests
{
    [Fact]
    public async Task An_exchange_inside_a_route_knows_its_context()
    {
        IRouteContext? seen = null;

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:ctx").Process(e => seen = e.Context));
        await context.Start();

        var producer = context.GetEndpoint("direct:ctx").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));

        seen.Should().BeSameAs(context,
            "this is what replaces capturing the context into every lambda");
    }

    [Fact]
    public void A_hand_made_exchange_has_no_context()
    {
        new Exchange(new Message("m")).Context.Should().BeNull(
            "it was never handed to a route, and pretending otherwise would lie about lifetime");
    }

    [Fact]
    public async Task The_context_lets_a_step_read_endpoint_statistics_without_capture()
    {
        long? errors = null;

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:ctx-stats").Process(e =>
            errors = ((IEndpointStatistics)e.Context!.GetEndpoint("direct:ctx-stats")).Errors));
        await context.Start();

        var producer = context.GetEndpoint("direct:ctx-stats").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));

        errors.Should().Be(0);
    }

    [Fact]
    public async Task Copies_inherit_the_context()
    {
        await using var context = new RouteContext();
        IExchange? inRoute = null;
        context.AddRoutes(r => r.From("direct:ctx-copy").Process(e => inRoute = e.Clone()));
        await context.Start();

        var producer = context.GetEndpoint("direct:ctx-copy").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));

        inRoute!.Context.Should().BeSameAs(context);
        inRoute.CreateChild(new Message("c")).Context.Should().BeSameAs(context);
        inRoute.Snapshot().Context.Should().BeSameAs(context);
    }

    [Fact]
    public async Task Parallel_branches_see_the_context()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<IRouteContext?>();

        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:ctx-multi")
            .Multicast()
                .To("direct:ctx-branch")
            .EndMulticast());
        context.AddRoutes(r => r.From("direct:ctx-branch").Process(e => seen.Add(e.Context)));
        await context.Start();

        var producer = context.GetEndpoint("direct:ctx-multi").CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("m")));

        seen.Should().NotBeEmpty().And.OnlyContain(c => ReferenceEquals(c, context));
    }

    [Fact]
    public async Task After_the_route_returns_the_callers_view_is_restored()
    {
        // The stamp is scoped, not sticky: an exchange created by hand, pushed through a route,
        // comes back with the context it had before — null. Camel's "current context" semantics,
        // taken literally: outside any route there is no current route.
        await using var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:ctx-restore").Process(_ => { }));
        await context.Start();

        var producer = context.GetEndpoint("direct:ctx-restore").CreateProducer();
        await producer.Start();

        var exchange = new Exchange(new Message("m"));
        await producer.Process(exchange);

        exchange.Context.Should().BeNull("the wrapper restores what the caller had on entry");
    }
}
