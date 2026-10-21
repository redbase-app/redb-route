using System.Text;
using redb.Route.Abstractions;
using redb.Route.As2;
using redb.Route.Core;
using As2Dsl = redb.Route.As2.Fluent.As2;

namespace redb.Route.Tests.As2;

/// <summary>
/// What crosses an AS2 message today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer sends every exchange header but its own <c>redbAs2.*</c>, <c>As2Headers.NonBridgedHeaders</c> (the
/// MIME and AS2 fields it sets itself) and null values as HTTP headers; the consumer takes every request header but
/// pseudo-headers, the wrapper and hop-by-hop fields and the credentials that authenticated the hop.
/// </summary>
public sealed class As2HeaderMappingCharacterizationTests
{
    [Fact]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheMessage()
    {
        var port = As2TestKit.FreePort();
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var context = new RouteContext();
        context.AddComponent(new As2Component());
        context.AddToRegistry("me", As2TestKit.Receiver());
        context.AddToRegistry("them", As2TestKit.Sender());
        context.AddRoutes(r => r
            .From(As2Dsl.Receive("/inbound").Host("127.0.0.1").Port(port).ConnectionFactory("me"))
            .Process(e => received.TrySetResult(e)));
        await context.Start();

        var producer = context.GetEndpoint(As2Dsl.Send($"http://127.0.0.1:{port}/inbound").ConnectionFactory("them"))
            .CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("ISA*00*...EDI~") { ContentType = "application/edi-x12" });
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[As2Headers.Prefix + "Custom"] = "own";
        await producer.Process(exchange);
        await producer.Stop();

        var arrived = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var headers = arrived.In.Headers;

        Encoding.UTF8.GetString((byte[])arrived.In.Body!).Should().Be("ISA*00*...EDI~");
        headers["X-Corr"].Should().Be("c1");
        headers["X-Int"].Should().Be("42", "a value crosses as its text");
        headers.Should().NotContainKey("X-Null", "a null value is skipped");
        headers.Should().NotContainKey("Authorization",
            "the producer sends it, but the consumer drops the credentials that authenticated the hop");
        headers["redbHttp.Method"].Should().Be("DELETE", "a control header of another transport crosses (finding 3.1)");
        headers.Should().NotContainKey(As2Headers.Prefix + "Custom", "the producer keeps its own redbAs2.* back");
    }
}
