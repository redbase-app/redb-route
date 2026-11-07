using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>
/// Which headers a hit replays with <c>CacheHeaders()</c>: what the miss produced, never what the first
/// caller brought, never a credential. Review 2026-09-29, C1 and C6.
/// </summary>
public class CacheHeadersPolicyTests
{
    [Fact]
    public async Task Hit_KeepsTheCurrentCallersHeaders_AndReplaysWhatTheMissProduced()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://creds")
                .Cache("k").CacheHeaders().SetHeader("computed", "yes").SetBody("v").EndCache()
                .To("mock://creds"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://creds", "a", new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer one", ["Cookie"] = "session=one", ["redbHttp.RemoteAddress"] = "10.0.0.1",
        });
        await ctx.SendBodyAndHeaders("direct://creds", "a", new Dictionary<string, object?>
        {
            ["Authorization"] = "Bearer two", ["Cookie"] = "session=two", ["redbHttp.RemoteAddress"] = "10.0.0.2",
        });

        var hit = ctx.Mock("mock://creds").ReceivedExchanges[1].In;
        hit.Headers["cache.hit"].Should().Be(true);
        hit.Headers["computed"].Should().Be("yes");
        hit.Headers["Authorization"].Should().Be("Bearer two", "a hit must not hand the first caller's credential to the current one");
        hit.Headers["Cookie"].Should().Be("session=two");
        hit.Headers["redbHttp.RemoteAddress"].Should().Be("10.0.0.2", "request metadata belongs to the current exchange");
    }

    [Fact]
    public async Task Hit_RemovesWhatTheMissRemoved()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://removed")
                .Cache("k").CacheHeaders().Process(e => e.In.Headers.Remove("temp")).SetBody("v").EndCache()
                .To("mock://removed"));
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://removed", "a", "temp", "one");
        await ctx.SendBodyAndHeader("direct://removed", "a", "temp", "two");

        var got = ctx.Mock("mock://removed").ReceivedExchanges;
        got[0].In.Headers.Should().NotContainKey("temp");
        got[1].In.Headers["cache.hit"].Should().Be(true);
        got[1].In.Headers.Should().NotContainKey("temp", "a hit must leave the headers the way the miss did");
    }

    [Fact]
    public async Task Hit_DoesNotReplayACredentialTheMissProduced()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://service-token")
                .Cache("k").CacheHeaders().SetHeader("Authorization", "Bearer service").SetHeader("X-Trace", "t").SetBody("v").EndCache()
                .To("mock://service-token"));
        await ctx.Start();

        await ctx.SendBody("direct://service-token", "a");
        await ctx.SendBody("direct://service-token", "a");

        var got = ctx.Mock("mock://service-token").ReceivedExchanges;
        got[0].In.Headers["Authorization"].Should().Be("Bearer service", "the miss itself keeps what its steps set");
        got[1].In.Headers["cache.hit"].Should().Be(true);
        got[1].In.Headers["X-Trace"].Should().Be("t");
        got[1].In.Headers.Should().NotContainKey("Authorization", "a credential is never stored, whoever produced it");
    }

    [Fact]
    public async Task Hit_RestoresInHeaders_WhenTheMissRepliedInOut()
    {
        // C6: with the reply in Out, what the steps did to In must come back on a hit as well.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://out-headers")
                .Cache("k").CacheHeaders().SetHeader("x", "1").Process(e => e.Out = new Message("reply")).EndCache());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var miss = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://out-headers", miss);
        var hit = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://out-headers", hit);

        hit.In.Headers["cache.hit"].Should().Be(true);
        hit.In.Headers["x"].Should().Be("1", "the miss set it on In");
        hit.Out.Should().NotBeNull();
        hit.Out!.Body.Should().Be("reply");
    }

    [Fact]
    public async Task Hit_DoesNotReplayRequestHeadersAReplyCopied()
    {
        // A producer that builds its reply from the request's headers: the copies are the request's, not the reply's own.
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://copied")
                .Cache("k").CacheHeaders().Process(e =>
                {
                    var reply = new Message("reply");
                    foreach (var (name, value) in e.In.Headers)
                        reply.Headers[name] = value;
                    reply.Headers["X-Reply"] = "yes";
                    e.Out = reply;
                }).EndCache());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var miss = Exchange.Create(new Message("in") { Headers = { ["X-Tenant"] = "a" } }, null);
        await template.SendAsync("direct://copied", miss);
        var hit = Exchange.Create(new Message("in") { Headers = { ["X-Tenant"] = "b" } }, null);
        await template.SendAsync("direct://copied", hit);

        hit.Out.Should().NotBeNull();
        hit.Out!.Headers["X-Reply"].Should().Be("yes");
        hit.Out.Headers.Should().NotContainKey("X-Tenant", "the first caller's request header must not travel in another caller's reply");
        hit.In.Headers["X-Tenant"].Should().Be("b");
    }

    [Fact]
    public async Task Distributed_CarriesTheProducedAndRemovedHeaders_NotTheCallersOwn()
    {
        await using var ctx = new RouteContext();
        ctx.UseCache(o => o.DefaultProvider = CacheProvider.Distributed);
        ctx.AddService(typeof(IDistributedCache), new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        ctx.AddRoutes(b => b
            .From("direct://dist-headers")
                .Cache("k").CacheHeaders().SetHeader("computed", "yes").Process(e => e.In.Headers.Remove("temp")).SetBody("v").EndCache()
                .To("mock://dist-headers"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://dist-headers", "a", new Dictionary<string, object?> { ["Authorization"] = "Bearer one", ["temp"] = "one" });
        await ctx.SendBodyAndHeaders("direct://dist-headers", "a", new Dictionary<string, object?> { ["Authorization"] = "Bearer two", ["temp"] = "two" });

        var hit = ctx.Mock("mock://dist-headers").ReceivedExchanges[1].In;
        hit.Headers["cache.hit"].Should().Be(true);
        hit.Headers["computed"].Should().Be("yes");
        hit.Headers["Authorization"].Should().Be("Bearer two");
        hit.Headers.Should().NotContainKey("temp");
    }
}
