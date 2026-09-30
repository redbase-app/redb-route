using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>Object bodies through the distributed store: the type name travels with the entry.</summary>
public class DistributedBodyTypeTests
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
    }

    private static RouteContext Context()
    {
        var ctx = new RouteContext();
        ctx.UseCache(o => o.DefaultProvider = CacheProvider.Distributed);
        ctx.AddService(typeof(IDistributedCache), new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        return ctx;
    }

    [Fact]
    public async Task AnonymousBody_RoundTrips_LikeAnyTypeOfTheWritersAssembly()
    {
        // Safety net: an anonymous type is a type of the writer's assembly like any other, and comes back
        // as itself wherever that assembly is loaded.
        var calls = 0;
        await using var ctx = Context().AddRoutes(b => b
            .From("direct://anon")
                .Cache("k").Process(e => { Interlocked.Increment(ref calls); e.In.Body = new { Id = 7, Customer = "acme" }; }).EndCache()
                .To("mock://anon"));
        await ctx.Start();

        await ctx.SendBody("direct://anon", "in");
        await ctx.SendBody("direct://anon", "in");

        calls.Should().Be(1);
        var hit = ctx.Mock("mock://anon").ReceivedExchanges[1].In;
        hit.Headers["cache.hit"].Should().Be(true);
        hit.Body.Should().BeEquivalentTo(new { Id = 7, Customer = "acme" });
        hit.Body!.GetType().Should().Be(new { Id = 0, Customer = "" }.GetType());
    }

    [Fact]
    public async Task ManyHits_OnOnePocoType_ComeBackTyped()
    {
        // Safety net for the resolved-type and serialize-method caches: the second and later reads take the cached path.
        var calls = 0;
        await using var ctx = Context().AddRoutes(b => b
            .From("direct://typed")
                .Cache("${header.id}").Process(e => { Interlocked.Increment(ref calls); e.In.Body = new Order { Id = (int)e.In.Headers["id"]!, Customer = "acme" }; }).EndCache()
                .To("mock://typed"));
        await ctx.Start();

        for (var round = 0; round < 3; round++)
            for (var id = 1; id <= 3; id++)
                await ctx.SendBodyAndHeader("direct://typed", "in", "id", id);

        calls.Should().Be(3, "three keys, each computed once");
        ctx.Mock("mock://typed").ReceivedExchanges.Select(e => e.In.Body).Should().AllBeOfType<Order>();
        ctx.Mock("mock://typed").ReceivedExchanges.Select(e => ((Order)e.In.Body!).Id).Should().Equal(1, 2, 3, 1, 2, 3, 1, 2, 3);
    }
}
