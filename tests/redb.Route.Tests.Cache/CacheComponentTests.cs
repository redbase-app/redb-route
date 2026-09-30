using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary>The <c>cache:</c> component: explicit get / put / remove / clear.</summary>
public class CacheComponentTests
{
    private static RouteContext Context()
    {
        var ctx = new RouteContext();
        ctx.UseCache();
        return ctx;
    }

    [Fact]
    public async Task Get_Miss_Then_Put_Then_Hit_AcrossRoutes()
    {
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://get").To("cache:customers?action=get&key=${header.id}").To("mock://get");
            b.From("direct://put").To("cache:customers?action=put&key=${header.id}&ttl=5m");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://get", "original", "id", 7);
        await ctx.SendBodyAndHeader("direct://put", "cached-7", "id", 7);
        await ctx.SendBodyAndHeader("direct://get", "original", "id", 7);

        var got = ctx.Mock("mock://get").ReceivedExchanges;
        got[0].In.Headers["cache.hit"].Should().Be(false);
        got[0].In.Body.Should().Be("original");
        got[1].In.Headers["cache.hit"].Should().Be(true);
        got[1].In.Body.Should().Be("cached-7");
    }

    [Fact]
    public async Task Remove_And_Clear()
    {
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://put").To("cache:region?action=put&key=${header.id}");
            b.From("direct://get").To("cache:region?action=get&key=${header.id}").To("mock://get");
            b.From("direct://remove").To("cache:region?action=remove&key=${header.id}");
            b.From("direct://clear").To("cache:region?action=clear");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://put", "a", "id", "a");
        await ctx.SendBodyAndHeader("direct://put", "b", "id", "b");
        await ctx.SendBodyAndHeader("direct://remove", "x", "id", "a");
        await ctx.SendBodyAndHeader("direct://get", "x", "id", "a");
        await ctx.SendBodyAndHeader("direct://get", "x", "id", "b");
        await ctx.SendBody("direct://clear", "x");
        await ctx.SendBodyAndHeader("direct://get", "x", "id", "b");

        ctx.Mock("mock://get").ReceivedExchanges.Select(e => e.In.Headers["cache.hit"]).Should().Equal(false, true, false);
    }

    [Fact]
    public async Task Clear_RemovesAKeyThatWasPutTwice()
    {
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://put2").To("cache:overwrite?action=put&key=k");
            b.From("direct://get2").To("cache:overwrite?action=get&key=k").To("mock://get2");
            b.From("direct://clear2").To("cache:overwrite?action=clear");
        });
        await ctx.Start();

        await ctx.SendBody("direct://put2", "first");
        await ctx.SendBody("direct://put2", "second");
        // MemoryCache runs the replaced entry's eviction callback on the thread pool; let it fire before clear.
        await Task.Delay(200);
        await ctx.SendBody("direct://clear2", "x");
        await ctx.SendBody("direct://get2", "x");

        ctx.Mock("mock://get2").ReceivedExchanges[0].In.Headers["cache.hit"].Should().Be(false, "clear must remove an overwritten key too");
    }

    [Fact]
    public async Task InvalidOptions_FailLoudly_WhenTheEndpointIsCreated()
    {
        // A static To's endpoint is created when its route starts, so the option validation stops the start, with the
        // reason in the message.
        await StartOf("cache:r?action=put").Should().ThrowAsync<ArgumentException>().WithMessage("*needs key*");
        await StartOf("cache:r?action=flush&key=k").Should().ThrowAsync<ArgumentException>().WithMessage("*unknown action*");
        await StartOf("cache:r?action=put&key=k&ttl=soon").Should().ThrowAsync<FormatException>();
    }

    /// <summary>Starting a context with one route that sends to <paramref name="target"/>.</summary>
    private static Func<Task> StartOf(string target) => async () =>
    {
        await using var ctx = Context().AddRoutes(b => b.From("direct://start-of").To(target));
        await ctx.Start();
    };

    [Fact]
    public async Task Put_StoresOnlyTheNamedHeaders()
    {
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://put-named").To("cache:named?action=put&key=k&headers=X-Rate,X-Missing");
            b.From("direct://get-named").To("cache:named?action=get&key=k").To("mock://get-named");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://put-named", "v", new Dictionary<string, object?>
        {
            ["X-Rate"] = 5, ["Other"] = "first", ["Authorization"] = "Bearer one",
        });
        await ctx.SendBodyAndHeaders("direct://get-named", "x", new Dictionary<string, object?>
        {
            ["Other"] = "second", ["Authorization"] = "Bearer two",
        });

        var got = ctx.Mock("mock://get-named").ReceivedExchanges[0].In;
        got.Headers["cache.hit"].Should().Be(true);
        got.Body.Should().Be("v");
        got.Headers["X-Rate"].Should().Be(5);
        got.Headers["Other"].Should().Be("second", "a header the put did not name is not stored");
        got.Headers["Authorization"].Should().Be("Bearer two");
        got.Headers.Should().NotContainKey("X-Missing");
    }

    [Fact]
    public async Task HeaderOptions_AreRefused_WhenTheyCannotBeHonoured()
    {
        await StartOf("cache:r?action=put&key=k&cacheHeaders=true").Should()
            .ThrowAsync<ArgumentException>("a put cannot tell the value's headers from the request's").WithMessage("*headers=*");
        await StartOf("cache:r?action=put&key=k&headers=X-Rate,Authorization").Should()
            .ThrowAsync<ArgumentException>().WithMessage("*Authorization*credential*");
        await StartOf("cache:r?action=get&key=k&headers=X-Rate").Should()
            .ThrowAsync<ArgumentException>().WithMessage("*headers=*put*");
    }

    [Theory]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("5m", 300_000)]
    [InlineData("2h", 7_200_000)]
    [InlineData("1d", 86_400_000)]
    [InlineData("00:00:10", 10_000)]
    public void Durations(string text, double milliseconds)
        => CacheDuration.Parse(text)!.Value.TotalMilliseconds.Should().Be(milliseconds);

    [Fact]
    public async Task ScopeAndComponent_ShareTheStore()
    {
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://scope").Cache("${header.id}").Region("shared").SetBody("from-scope").EndCache();
            b.From("direct://get").To("cache:shared?action=get&key=${header.id}").To("mock://get");
        });
        await ctx.Start();

        await ctx.SendBodyAndHeader("direct://scope", "x", "id", 1);
        await ctx.SendBodyAndHeader("direct://get", "x", "id", 1);

        var got = ctx.Mock("mock://get").ReceivedExchanges[0];
        got.In.Headers["cache.hit"].Should().Be(true);
        got.In.Body.Should().Be("from-scope");
    }
}
