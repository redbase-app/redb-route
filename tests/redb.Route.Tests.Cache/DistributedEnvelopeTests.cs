using System.Text;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>What the distributed store makes of a value under its key that it did not write.</summary>
public class DistributedEnvelopeTests
{
    private static IDistributedCache Memory() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static RouteContext Context(IDistributedCache cache)
    {
        var ctx = new RouteContext();
        ctx.UseCache(o => o.DefaultProvider = CacheProvider.Distributed);
        ctx.AddService(typeof(IDistributedCache), cache);
        return ctx;
    }

    [Fact]
    public async Task ForeignJson_UnderOurKey_IsAnError_NotAHitWithAnEmptyBody()
    {
        var cache = Memory();
        await cache.SetAsync(CacheKeys.For("default", "k"), Encoding.UTF8.GetBytes("""{"id":1}"""));
        var calls = 0;
        await using var ctx = Context(cache).AddRoutes(b => b
            .From("direct://foreign").Cache("k").Process(e => { calls++; e.In.Body = "computed"; }).EndCache().To("mock://foreign"));
        await ctx.Start();

        var act = () => ctx.SendBody("direct://foreign", "in");

        var failure = await act.Should().ThrowAsync<InvalidOperationException>("another writer's value under our key is a shared key space, not a hit and not a miss");
        failure.Which.Message.Should().Contain(CacheKeys.For("default", "k")).And.Contain("cache entry");
        calls.Should().Be(0, "no silent recompute over somebody else's value");
        ctx.Mock("mock://foreign").ReceivedExchanges.Should().BeEmpty("no hit with a null body either");
    }

    [Fact]
    public async Task NonJsonBytes_UnderOurKey_IsAnError_NamingTheKey()
    {
        var cache = Memory();
        await cache.SetAsync(CacheKeys.For("default", "k"), new byte[] { 0x01, 0x02, 0x03 });
        await using var ctx = Context(cache).AddRoutes(b => b.From("direct://raw").Cache("k").SetBody("computed").EndCache());
        await ctx.Start();

        var act = () => ctx.SendBody("direct://raw", "in");

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain(CacheKeys.For("default", "k"));
    }

    [Fact]
    public async Task NullBody_IsACachedValue_NotAMiss()
    {
        // Safety net for the envelope marker: a null body is a legitimate cached value and must stay a hit.
        var calls = 0;
        await using var ctx = Context(Memory()).AddRoutes(b => b
            .From("direct://null").Cache("k").Process(e => { calls++; e.In.Body = null; }).EndCache().To("mock://null"));
        await ctx.Start();

        await ctx.SendBody("direct://null", "in");
        await ctx.SendBody("direct://null", "in");

        calls.Should().Be(1);
        var hit = ctx.Mock("mock://null").ReceivedExchanges[1].In;
        hit.Headers["cache.hit"].Should().Be(true);
        hit.Body.Should().BeNull();
    }
}
