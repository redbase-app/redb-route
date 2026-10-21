using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Amqp;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// What crosses an AMQP 1.0 message today, both ways of a request-reply: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer and the replying consumer send every exchange header but their own <c>redbAmqp.*</c>, the AMQP standard
/// property names and null values; the consumer takes every application property back.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpHeaderMappingCharacterizationTests
{
    private static string Uri(string queue, string? extra = null)
        => $"amqp://{queue}?host=localhost&port=5673&user=admin&password=admin{extra}";

    private static Exchange Sample()
    {
        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers["MessageId"] = "m-1";
        exchange.In.Headers[AmqpHeaders.Prefix + "Custom"] = "own";
        return exchange;
    }

    [Fact]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheMessage()
    {
        var queue = $"hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AmqpComponent());
        ctx.AddRoutes(r => r.From(Uri(queue)).Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var producer = ctx.GetEndpoint(Uri(queue)).CreateProducer();
        await producer.Start();
        await producer.Process(Sample());
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers;

        headers["X-Corr"].Should().Be("c1");
        headers["X-Int"].Should().Be(42, "a number crosses as an AMQP number");
        headers.Should().NotContainKey("X-Null", "a null value is skipped");
        headers["Authorization"].Should().Be("Bearer secret", "nothing filters a credential");
        headers["redbHttp.Method"].Should().Be("DELETE", "a control header of another transport crosses (finding 3.1)");
        headers.Should().NotContainKey(AmqpHeaders.Prefix + "Custom", "the producer keeps its own redbRmq.* back");
        headers["MessageId"].Should().Be("m-1",
            "a header named after a message property is sent as that property, and comes back under its name");
    }

    [Fact]
    public async Task RpcReply_TheHeadersThatCrossTheReply()
    {
        var queue = $"hdr-char-rpc-{Guid.NewGuid():N}";
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AmqpComponent());
        ctx.AddRoutes(r => r.From(Uri(queue)).Process(e =>
        {
            e.In.Headers.Clear();
            e.In.Headers["X-Reply"] = "r1";
            e.In.Headers["X-Int"] = 7;
            e.In.Headers["X-Null"] = null;
            e.In.Headers["redbHttp.Method"] = "PUT";
            e.In.Headers[AmqpHeaders.Prefix + "Custom"] = "own";
            e.In.Body = "reply";
        }));
        await ctx.Start();

        var producer = ctx.GetEndpoint(Uri(queue, "&replyTo=true&timeout=15")).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("request"));
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (exchange.HasOut ? exchange.Out! : exchange.In).Headers;
        headers["X-Reply"].Should().Be("r1");
        headers["X-Int"].Should().Be(7, "a reply keeps a number an AMQP number (RabbitMQ sends it as text)");
        headers.Should().NotContainKey("X-Null");
        headers["redbHttp.Method"].Should().Be("PUT", "a control header crosses the reply too");
        headers.Should().NotContainKey(AmqpHeaders.Prefix + "Custom");
    }
}
