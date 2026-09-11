using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>The distributed provider: the reference in-memory implementation and a real Redis (tests/docker: localhost:6379).</summary>
public class DistributedCacheTests
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
    }

    private static RouteContext Context(IDistributedCache cache)
    {
        var ctx = new RouteContext();
        ctx.UseCache(o => o.DefaultProvider = CacheProvider.Distributed);
        ctx.AddService(typeof(IDistributedCache), cache);
        return ctx;
    }

    private static IDistributedCache Memory() => new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    private static IDistributedCache Redis() => new RedisCache(Options.Create(new RedisCacheOptions
    {
        Configuration = "localhost:6379,abortConnect=false",
        InstanceName = $"redb-route-cache-tests-{Guid.NewGuid():N}:",
    }));

    [Fact]
    public async Task Poco_KeepsItsType_ThroughTheDistributedCache()
    {
        var calls = 0;
        await using var ctx = Context(Memory()).AddRoutes(b => b
            .From("direct://d")
                .Cache("${header.id}", TimeSpan.FromMinutes(1))
                    .Process(e => { Interlocked.Increment(ref calls); e.In.Body = new Order { Id = 7, Customer = "acme" }; })
                .EndCache()
                .To("mock://d"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://d", "x", "id", 7);
        await ctx.SendBodyAndHeader("direct://d", "x", "id", 7);

        calls.Should().Be(1);
        var hit = ctx.Mock("mock://d").ReceivedExchanges[1].In;
        hit.Headers["cache.hit"].Should().Be(true);
        hit.Body.Should().BeOfType<Order>().Which.Customer.Should().Be("acme");
        hit.ContentType.Should().Be("application/json");
    }

    [Fact]
    public async Task String_Bytes_AndHeaders_RoundTrip()
    {
        await using var ctx = Context(Memory()).AddRoutes(b =>
        {
            b.From("direct://put").To("cache:r?action=put&key=${header.id}&cacheHeaders=true");
            b.From("direct://get").To("cache:r?action=get&key=${header.id}").To("mock://get");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://put", "text", new Dictionary<string, object?> { ["id"] = "s", ["n"] = 5, ["flag"] = true });
        await ctx.SendBodyAndHeader("direct://put", new byte[] { 1, 2, 3 }, "id", "b");
        await ctx.SendBodyAndHeader("direct://get", "x", "id", "s");
        await ctx.SendBodyAndHeader("direct://get", "x", "id", "b");

        var got = ctx.Mock("mock://get").ReceivedExchanges;
        got[0].In.Body.Should().Be("text");
        got[0].In.Headers["n"].Should().Be(5L, "JSON numbers come back as Int64");
        got[0].In.Headers["flag"].Should().Be(true);
        got[1].In.Body.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Fact]
    public async Task MissingDistributedCache_FailsStart_WithGuidance()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://x").Cache("k").Distributed().SetBody("v").EndCache());

        var act = () => ctx.Start();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IDistributedCache*");
    }

    [Fact]
    public async Task Redis_RealContainer_RoundTrip()
    {
        var calls = 0;
        await using var ctx = Context(Redis()).AddRoutes(b => b
            .From("direct://redis")
                .Cache("${header.id}", TimeSpan.FromSeconds(30))
                    .Process(e => { Interlocked.Increment(ref calls); e.In.Body = new Order { Id = 1, Customer = "redis" }; })
                .EndCache()
                .To("mock://redis"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://redis", "x", "id", 1);
        await ctx.SendBodyAndHeader("direct://redis", "x", "id", 1);

        calls.Should().Be(1, "the second message is served by Redis");
        ctx.Mock("mock://redis").ReceivedExchanges[1].In.Body.Should().BeOfType<Order>().Which.Customer.Should().Be("redis");
    }
}
