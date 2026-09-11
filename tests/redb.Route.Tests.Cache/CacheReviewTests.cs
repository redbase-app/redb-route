using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>Code review 2026-09-01: В13 (a host cache with SizeLimit), duration parsing, Out on a hit, and what a hit hands back (В12).</summary>
public class CacheReviewTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task HostMemoryCacheWithSizeLimit_IsUsable()
    {
        var calls = 0;
        await using var ctx = new RouteContext();
        ctx.AddService(typeof(IMemoryCache), new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 }));
        ctx.AddRoutes(b => b.From("direct://sized").Cache("k").Process(_ => Interlocked.Increment(ref calls)).EndCache());
        await ctx.Start();

        await ctx.SendBody("direct://sized", "x");
        await ctx.SendBody("direct://sized", "x");

        calls.Should().Be(1, "the second send is a hit; a sized host cache must not reject the entry on every miss");
    }

    [Theory]
    [InlineData("5")]
    [InlineData("30")]
    public void Duration_BareNumber_IsRejected(string text)
    {
        var act = () => CacheDuration.Parse(text);
        act.Should().Throw<FormatException>("a number without a unit is ambiguous: TimeSpan would read it as days");
    }

    [Theory]
    [InlineData("0s")]
    [InlineData("-5m")]
    [InlineData("00:00:00")]
    public void Duration_NonPositive_IsRejected(string text)
    {
        var act = () => CacheDuration.Parse(text);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public async Task Hit_RestoresOut_WhenTheMissProducedOut()
    {
        // The scope is the route's last step, so its Out reaches the caller the way a reply does (the
        // pipeline merges Out into In only between steps); caller-owned exchanges make Out observable.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://out")
                .Cache("k-out").Process(e => e.Out = new Message("out-body")).EndCache());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var miss = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://out", miss);
        var hit = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://out", hit);

        miss.Out?.Body.Should().Be("out-body");
        hit.Out.Should().NotBeNull("a hit must leave the exchange the way the miss did: a consumer that replies only when Out is present must see one");
        hit.Out!.Body.Should().Be("out-body");
    }

    [Fact]
    public async Task StreamBody_IsBufferedOnTheMiss_AndReplayableOnHits()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stream")
                .Cache("k-stream").Process(e => e.In.Body = new MemoryStream("payload"u8.ToArray())).EndCache()
                .To("mock://stream"));
        await ctx.Start();

        await ctx.SendBody("direct://stream", "in");
        await ctx.SendBody("direct://stream", "in");
        await ctx.Mock("mock://stream").ExpectMessageCount(2).AssertIsSatisfiedAsync(Wait);

        var bodies = ctx.Mock("mock://stream").ReceivedExchanges.Select(e => e.In.Body).ToList();
        bodies.Should().AllBeOfType<byte[]>("a stream can be read once; the cache keeps bytes and hands bytes back");
        bodies.Select(body => Encoding.UTF8.GetString((byte[])body!)).Should().Equal("payload", "payload");
    }

    [Fact]
    public async Task JsonNodeBody_IsCopiedIntoAndOutOfTheCache()
    {
        var seen = new List<int>();
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://node")
                .Cache("k-node").Process(e => e.In.Body = JsonNode.Parse("""{"n":1}""")).EndCache()
                .Process(e => seen.Add(((JsonObject)e.In.Body!)["n"]!.GetValue<int>()))
                .Process(e => ((JsonObject)e.In.Body!)["n"] = 2));   // a later step mutates what it got
        await ctx.Start();

        await ctx.SendBody("direct://node", "in");
        await ctx.SendBody("direct://node", "in");

        seen.Should().Equal(1, 1);   // neither the miss's later mutation nor a hit's reaches the cached copy
    }

    // Fourth batch: region keys, and a stampede on one key.

    [Fact]
    public async Task Regions_DoNotCollideOnTheSeparator_AndClearStaysInItsRegion()
    {
        await using var ctx = new RouteContext();
        ctx.UseCache();
        ctx.AddRoutes(b =>
        {
            b.From("direct://reg-a").Cache("b:c").Region("a").SetBody("from-a").EndCache().To("mock://reg-a");
            b.From("direct://reg-ab").Cache("c").Region("a:b").SetBody("from-ab").EndCache().To("mock://reg-ab");
            b.From("direct://reg-clear-a").To("cache:a?action=clear");
        });
        await ctx.Start();

        await ctx.SendBody("direct://reg-a", "x");
        await ctx.SendBody("direct://reg-ab", "x");       // region "a:b" key "c" must not be region "a" key "b:c"
        await ctx.SendBody("direct://reg-clear-a", "x");
        await ctx.SendBody("direct://reg-ab", "x");       // clearing "a" must not reach "a:b"

        var received = ctx.Mock("mock://reg-ab").ReceivedExchanges;
        received.Select(e => e.In.Body).Should().Equal("from-ab", "from-ab");
        received[1].In.Headers["cache.hit"].Should().Be(true);
    }

    [Fact]
    public async Task ConcurrentMissesOnOneKey_RunTheInnerStepsOnce()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://stampede")
                .Cache("hot", TimeSpan.FromMinutes(1))
                    .Process(async (e, ct) =>
                    {
                        Interlocked.Increment(ref calls);
                        await Task.Delay(300, ct);
                        e.In.Body = "computed";
                    })
                .EndCache()
                .To("mock://stampede"));
        await ctx.Start();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ctx.SendBody("direct://stampede", "in")));

        calls.Should().Be(1, "the first miss computes; the other nineteen wait for its result");
        var bodies = ctx.Mock("mock://stampede").ReceivedExchanges.Select(e => e.In.Body).ToList();
        bodies.Should().HaveCount(20).And.AllBeEquivalentTo("computed");
    }
}
