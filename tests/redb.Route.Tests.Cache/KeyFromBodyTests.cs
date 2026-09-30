using redb.Route.Cache;
using redb.Route.Core;
using redb.Route.TestKit;

namespace redb.Route.Tests.Cache;

/// <summary><c>KeyFromBody()</c>: the key is a hash of the body, whatever the body is.</summary>
public class KeyFromBodyTests
{
    public sealed class Order
    {
        public int Id { get; set; }
        public string Customer { get; set; } = "";
    }

    public sealed class Node
    {
        public Node? Next { get; set; }
    }

    [Fact]
    public async Task StreamBody_IsBufferedAndHashed()
    {
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kb-stream").Cache("ignored").KeyFromBody().Process(_ => Interlocked.Increment(ref calls)).EndCache().To("mock://kb-stream"));
        await ctx.Start();

        await ctx.SendBody("direct://kb-stream", new MemoryStream("payload"u8.ToArray()));
        await ctx.SendBody("direct://kb-stream", new MemoryStream("payload"u8.ToArray()));
        await ctx.SendBody("direct://kb-stream", new MemoryStream("other"u8.ToArray()));

        calls.Should().Be(2, "two equal payloads share one key");
        ctx.Mock("mock://kb-stream").ReceivedExchanges.Select(e => e.In.Body)
            .Should().AllBeOfType<byte[]>("a stream is read once to hash it; the steps continue with its bytes");
    }

    [Fact]
    public async Task PocoBody_IsHashedThroughTheContextDataFormat()
    {
        // Safety net: a POCO hashed the same way the distributed store serializes it.
        var calls = 0;
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kb-poco").Cache("ignored").KeyFromBody().Process(_ => Interlocked.Increment(ref calls)).EndCache());
        await ctx.Start();

        await ctx.SendBody("direct://kb-poco", new Order { Id = 1, Customer = "acme" });
        await ctx.SendBody("direct://kb-poco", new Order { Id = 1, Customer = "acme" });
        await ctx.SendBody("direct://kb-poco", new Order { Id = 2, Customer = "acme" });

        calls.Should().Be(2);
    }

    [Fact]
    public async Task UnserializableBody_FailsNamingTheKeyMode()
    {
        await using var ctx = new RouteContext().AddRoutes(b => b
            .From("direct://kb-cycle").Cache("ignored").KeyFromBody().SetBody("v").EndCache());
        await ctx.Start();
        var node = new Node();
        node.Next = node;

        var act = () => ctx.SendBody("direct://kb-cycle", node);

        var failure = await act.Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("KeyFromBody").And.Contain(nameof(Node));
    }
}
