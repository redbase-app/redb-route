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
    public async Task InvalidOptions_FailLoudly_WhenTheEndpointIsCreated()
    {
        // To(...) creates its endpoint on the first message (the producer is lazy for every component),
        // so the option validation surfaces there, with the reason in the message.
        await using var ctx = Context().AddRoutes(b =>
        {
            b.From("direct://x").To("cache:r?action=put");
            b.From("direct://y").To("cache:r?action=flush&key=k");
            b.From("direct://z").To("cache:r?action=put&key=k&ttl=soon");
        });
        await ctx.Start();

        var noKey = () => ctx.SendBody("direct://x", "v");
        await noKey.Should().ThrowAsync<ArgumentException>().WithMessage("*needs key*");

        var badAction = () => ctx.SendBody("direct://y", "v");
        await badAction.Should().ThrowAsync<ArgumentException>().WithMessage("*unknown action*");

        var badTtl = () => ctx.SendBody("direct://z", "v");
        await badTtl.Should().ThrowAsync<FormatException>();
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
