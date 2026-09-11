using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>The caching scope over the in-process cache.</summary>
public class CacheScopeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Hit_SkipsInnerSteps_Miss_RunsThem()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://c")
                .Cache("customer-${header.id}", TimeSpan.FromMinutes(5))
                    .Process(e => { Interlocked.Increment(ref calls); e.In.Body = $"loaded {e.In.Headers["id"]}"; })
                .EndCache()
                .To("mock://c"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://c", "q", "id", 1);
        await ctx.SendBodyAndHeader("direct://c", "q", "id", 1);
        await ctx.SendBodyAndHeader("direct://c", "q", "id", 2);

        calls.Should().Be(2, "id 1 was a miss then a hit, id 2 a miss");
        var mock = ctx.Mock("mock://c");
        await mock.ExpectBodies("loaded 1", "loaded 1", "loaded 2").AssertIsSatisfiedAsync(Wait);
        mock.ReceivedExchanges.Select(e => e.In.Headers["cache.hit"]).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task Ttl_Expires()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://ttl")
                .Cache("k", TimeSpan.FromMilliseconds(200)).Process(_ => Interlocked.Increment(ref calls)).EndCache());
        await ctx.Start();

        await ctx.SendBody("direct://ttl", "x");
        await ctx.SendBody("direct://ttl", "x");
        await Task.Delay(400);
        await ctx.SendBody("direct://ttl", "x");

        calls.Should().Be(2);
    }

    [Fact]
    public async Task CacheHeaders_RestoresHeaders_DefaultDoesNot()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://h").Cache("k").CacheHeaders().SetHeader("computed", "yes").SetBody("v").EndCache().To("mock://h");
            b.From("direct://nh").Cache("k2").SetHeader("computed", "yes").SetBody("v").EndCache().To("mock://nh");
        });
        await ctx.Start();

        await ctx.SendBody("direct://h", "a");
        await ctx.SendBody("direct://h", "a");
        await ctx.SendBody("direct://nh", "a");
        await ctx.SendBody("direct://nh", "a");

        ctx.Mock("mock://h").ReceivedExchanges[1].In.Headers.Should().ContainKey("computed");
        ctx.Mock("mock://nh").ReceivedExchanges[1].In.Headers.Should().NotContainKey("computed");
        ctx.Mock("mock://nh").ReceivedExchanges[1].In.Body.Should().Be("v");
    }

    [Fact]
    public async Task KeyFromBody_HashesTheBody()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kb").Cache("ignored").KeyFromBody().Process(_ => Interlocked.Increment(ref calls)).EndCache());
        await ctx.Start();

        await ctx.SendBody("direct://kb", "same");
        await ctx.SendBody("direct://kb", "same");
        await ctx.SendBody("direct://kb", "other");

        calls.Should().Be(2);
    }

    [Fact]
    public async Task Regions_Isolate_Keys()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://r1").Cache("k").Region("one").Process(_ => Interlocked.Increment(ref calls)).EndCache();
            b.From("direct://r2").Cache("k").Region("two").Process(_ => Interlocked.Increment(ref calls)).EndCache();
        });
        await ctx.Start();

        await ctx.SendBody("direct://r1", "x");
        await ctx.SendBody("direct://r2", "x");

        calls.Should().Be(2, "same key, different regions");
    }

    [Fact]
    public async Task FailedInner_IsNotCached()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://f")
                .Cache("k").Process(_ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("down"); }).EndCache());
        await ctx.Start();

        for (var i = 0; i < 2; i++)
        {
            var act = () => ctx.SendBody("direct://f", "x");
            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        calls.Should().Be(2);
    }

    [Fact]
    public async Task MaxEntries_Option_BoundsTheCache()
    {
        var calls = 0;
        await using var ctx = new RouteContext();
        ctx.UseCache(o => o.MaxEntries = 1);
        ctx.AddRoutes(b => b.From("direct://max").Cache("${header.id}").Process(_ => Interlocked.Increment(ref calls)).EndCache());
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://max", "x", "id", 1);
        await ctx.SendBodyAndHeader("direct://max", "x", "id", 2);   // over the limit: MemoryCache rejects the newcomer (or compacts)
        await ctx.SendBodyAndHeader("direct://max", "x", "id", 1);
        await ctx.SendBodyAndHeader("direct://max", "x", "id", 2);

        // Unbounded, the second round would be two hits (calls == 2). With one slot at least one of the
        // two keys is a miss again — whether MemoryCache rejected the newcomer (3) or evicted the older (4).
        calls.Should().BeInRange(3, 4);
    }
}
