using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>
/// The <c>cache:</c> component keeps working after <c>Stop()</c> and <c>Start()</c> of the same route context, and the
/// entries put before the restart are still there: the store belongs to the context, not to a run of its routes.
/// </summary>
public class CacheRestartTests
{
    [Fact]
    public async Task Get_and_put_work_after_the_context_is_stopped_and_started_again()
    {
        var ctx = new RouteContext();
        ctx.UseCache();
        await using var _ = ctx.AddRoutes(b =>
        {
            b.From("direct://get").To("cache:customers?action=get&key=${header.id}").To("mock://get");
            b.From("direct://put").To("cache:customers?action=put&key=${header.id}&ttl=5m");
        });

        await ctx.Start();
        await ctx.SendBodyAndHeader("direct://put", "cached-7", "id", 7);

        await ctx.Stop();
        await ctx.Start();
        await ctx.SendBodyAndHeader("direct://get", "original", "id", 7);
        await ctx.SendBodyAndHeader("direct://put", "cached-8", "id", 8);
        await ctx.SendBodyAndHeader("direct://get", "original", "id", 8);

        var got = ctx.Mock("mock://get").ReceivedExchanges;
        got.Should().HaveCount(2);
        got[0].In.Body.Should().Be("cached-7", "запись, положенная до рестарта, видна после него");
        got[1].In.Body.Should().Be("cached-8", "cache после рестарта и кладёт, и отдаёт");
    }
}
