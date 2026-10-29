using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests;

/// <summary>
/// A request the transport refuses as the caller's fault (malformed, unauthenticated, not what the agreement allows) is
/// answered by the protocol before any exchange exists. It is counted in <c>Refused</c>, with the reason of the last
/// one, and kept out of <c>Errors</c>, the last error and health: a partner sending what the endpoint cannot accept is
/// not a failure of the route, and a storm of such requests must not turn a healthy endpoint red.
/// </summary>
public sealed class RefusalStatisticsTests
{
    private static async Task<(RouteContext Context, IEndpointStatistics Stats)> Endpoint()
    {
        var context = new RouteContext();
        context.AddRoutes(r => r.From("direct:in").Process(_ => { }));
        await context.Start();
        return (context, (IEndpointStatistics)context.GetEndpoint("direct:in"));
    }

    [Fact]
    public async Task A_refusal_is_counted_with_its_reason_and_is_not_an_error()
    {
        var (context, stats) = await Endpoint();
        await using var _ = context;

        stats.RecordRefused("EBMS:0101 for m1: the signature does not verify.");
        stats.RecordRefused("EBMS:0103 for m2: the message is not signed.");

        stats.Refused.Should().Be(2);
        stats.LastRefusalMessage.Should().Be("EBMS:0103 for m2: the message is not signed.");
        stats.LastRefusalTime.Should().NotBeNull();
        stats.Errors.Should().Be(0);
        stats.LastErrorTime.Should().BeNull();
        stats.MessagesIn.Should().Be(0, "no exchange was created");
        stats.HealthStatus.Should().Be(EndpointHealthStatus.Healthy);
    }

    [Fact]
    public async Task Reset_clears_the_refusals()
    {
        var (context, stats) = await Endpoint();
        await using var _ = context;
        stats.RecordRefused("bad");

        stats.ResetStatistics();

        stats.Refused.Should().Be(0);
        stats.LastRefusalMessage.Should().BeEmpty();
        stats.LastRefusalTime.Should().BeNull();
    }

    [Fact]
    public async Task Refusals_are_readable_from_a_route()
    {
        var (context, stats) = await Endpoint();
        await using var _ = context;
        stats.RecordRefused("bad request");

        ExpressionResolver.ReadStatistic(stats, "refused").Should().Be(1L);
        ExpressionResolver.ReadStatistic(stats, "lastRefusal").Should().Be("bad request");
        ExpressionResolver.IsKnownStatistic("Refused").Should().BeTrue();
    }
}
