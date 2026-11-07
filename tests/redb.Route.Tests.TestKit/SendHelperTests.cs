using redb.Route.Components;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.TestKit;

public class SendHelperTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task SendBody_DeliversTheBody()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://s-in").To("mock://s-out"));
        await ctx.Start();

        await ctx.SendBody("direct://s-in", "payload");

        await ctx.Mock("mock://s-out").ExpectBodies("payload").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task SendBodyAndHeaders_DeliversHeaders()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://sh-in").To("mock://sh-out"));
        await ctx.Start();

        await ctx.SendBodyAndHeaders("direct://sh-in", "payload", new Dictionary<string, object?> { ["a"] = 1, ["b"] = "two" });

        await ctx.Mock("mock://sh-out").ExpectHeader("a", 1).ExpectHeader("b", "two").AssertIsSatisfiedAsync(Wait);
    }

    [Fact]
    public async Task RequestBody_ReturnsTheReply_ConvertedToT()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
        {
            b.From("direct://ping").SetBody("pong");
            b.From("direct://answer").SetBody("42");
        });
        await ctx.Start();

        (await ctx.RequestBody<string>("direct://ping", "ping")).Should().Be("pong");
        (await ctx.RequestBody<int>("direct://answer", "?")).Should().Be(42);
    }

    [Fact]
    public async Task RequestBody_NullableTarget_ConvertsTheReply()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b.From("direct://answer-n").SetBody("42"));
        await ctx.Start();

        (await ctx.RequestBody<int?>("direct://answer-n", "?")).Should().Be(42);
    }

    private sealed class ExchangeBoundStream : redb.Route.Abstractions.IExchangeBoundBody;

    [Fact]
    public async Task RequestBody_ExchangeBoundReply_IsRefused()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
            b.From("direct://bound").SetBody(_ => new ExchangeBoundStream()));
        await ctx.Start();

        var act = () => ctx.RequestBody<ExchangeBoundStream>("direct://bound", "?");

        await act.Should().ThrowAsync<InvalidOperationException>(
                "the helper ends the exchange before it returns, and the body could no longer be read")
            .WithMessage("*RequestAsync*");
    }

    [Fact]
    public async Task RequestBodyAndHeaders_SeesHeaders()
    {
        await using var ctx = new RouteContext().AddRoutes(b =>
            b.From("direct://echo-h").SetBody(e => $"hello {e.In.Headers["name"]}"));
        await ctx.Start();

        var reply = await ctx.RequestBodyAndHeaders<string>("direct://echo-h", null,
            new Dictionary<string, object?> { ["name"] = "kit" });

        reply.Should().Be("hello kit");
    }

    [Fact]
    public async Task Mock_ResolvesOriginalUri_AndMockUri_ToTheSameEndpoint()
    {
        await using var ctx = new RouteContext();

        var byOriginal = ctx.Mock("kafka://x?acks=1");
        var byMockName = ctx.Mock("mock://kafka:x");

        byOriginal.Should().BeSameAs(byMockName).And.BeOfType<MockEndpoint>();
    }

    [Theory]
    [InlineData("kafka://orders-vip?acks=all", "mock://kafka:orders-vip")]
    [InlineData("sql:INSERT INTO audit", "mock://sql:INSERT INTO audit")]
    [InlineData("mock://already", "mock://already")]
    [InlineData("http://api.internal/v1/send?method=POST", "mock://http:api.internal/v1/send")]
    public void MockUri_For(string endpoint, string expected)
        => MockUri.For(endpoint).Should().Be(expected);
}
