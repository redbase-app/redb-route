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
            b.From("direct://put").To("cache:r?action=put&key=${header.id}&headers=n,flag");
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
    public async Task Clear_RemovesASlidingKeyKeptAliveByReads()
    {
        // The store keeps its own list of written keys (IDistributedCache cannot enumerate) and sweeps the
        // expired ones every 256 writes. A sliding entry that is read all the time outlives its write plus
        // the window, so the list must follow the reads, or clear skips a live key.
        var store = new DistributedCacheStore(Memory(), null);
        var key = CacheKeys.For("r", "warm");
        // A one-second window read every 100 ms: the margin has to survive a machine busy with other builds.
        await store.SetAsync(key, new CacheEntry { Body = "v" }, ttl: null, sliding: TimeSpan.FromSeconds(1), CancellationToken.None);

        var until = DateTime.UtcNow.AddMilliseconds(1500);
        while (DateTime.UtcNow < until)
        {
            (await store.GetAsync(key, CancellationToken.None)).Should().NotBeNull("reads renew a sliding entry");
            await Task.Delay(100);
        }
        for (var i = 0; i < 256; i++)
            await store.SetAsync(CacheKeys.For("r", $"other-{i}"), new CacheEntry { Body = "x" }, TimeSpan.FromMinutes(1), null, CancellationToken.None);

        await store.ClearAsync("r", CancellationToken.None);

        (await store.GetAsync(key, CancellationToken.None)).Should().BeNull("clear must remove a key that is still alive");
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
