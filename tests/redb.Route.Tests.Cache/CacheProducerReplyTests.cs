using redb.Route.Cache;
using redb.Route.Core;

namespace redb.Route.Tests.Cache;

/// <summary>The <c>cache:</c> component reading an entry the scope stored as a reply (<c>Out</c>).</summary>
public class CacheProducerReplyTests
{
    [Fact]
    public async Task Get_RestoresAReplyWhereTheScopeLeftIt_InOut()
    {
        await using var ctx = new RouteContext();
        ctx.UseCache();
        ctx.AddRoutes(b =>
        {
            b.From("direct://reply-scope").Cache("k").Region("replies").Process(e => e.Out = new Message("reply")).EndCache();
            b.From("direct://reply-get").To("cache:replies?action=get&key=k");
        });
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();

        var miss = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://reply-scope", miss);
        var read = Exchange.Create(new Message("in"), null);
        await template.SendAsync("direct://reply-get", read);

        read.In.Headers["cache.hit"].Should().Be(true);
        read.Out.Should().NotBeNull("the scope cached a reply; the component must hand it back as a reply, not as the request body");
        read.Out!.Body.Should().Be("reply");
        read.In.Body.Should().Be("in");
    }
}
