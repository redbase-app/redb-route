using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// What crosses an IBM MQ message today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer packs every exchange header but its own <c>redbIbmMq.*</c> and null values into one JSON-encoded
/// message property (MQ property names forbid hyphens), every value as text; the consumer unpacks every one of them,
/// on the poll path and on the event-driven XMS path (<c>receiveMode=listener</c>, its own mapper) alike.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqHeaderMappingCharacterizationTests
{
    private const string Queue = "DEV.QUEUE.2";
    private const string Connection =
        "host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";

    [Theory]
    [InlineData("")]
    [InlineData("&receiveMode=listener")]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheMessage(string receiveMode)
    {
        var tag = $"hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new IbmMqComponent());
        // The dev queue is shared: only this test's message is taken.
        ctx.AddRoutes(r => r.From($"wmq:{Queue}?{Connection}{receiveMode}").Process(e =>
        {
            var body = e.In.Body is byte[] bytes ? Encoding.UTF8.GetString(bytes) : e.In.Body?.ToString();
            if (body == tag)
                received.TrySetResult(e);
        }));
        await ctx.Start();

        var exchange = new Exchange(new Message(tag));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[IbmMqHeaders.Prefix + "Custom"] = "own";
        var producer = ctx.GetEndpoint($"wmq:{Queue}?{Connection}").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers;

        headers["X-Corr"].Should().Be("c1");
        headers["X-Int"].Should().Be("42", "every value crosses as text in the JSON property");
        headers.Should().NotContainKey("X-Null", "a null value is skipped");
        headers["Authorization"].Should().Be("Bearer secret", "nothing filters a credential");
        headers["redbHttp.Method"].Should().Be("DELETE", "a control header of another transport crosses (finding 3.1)");
        headers.Should().NotContainKey(IbmMqHeaders.Prefix + "Custom", "the producer keeps its own redbIbmMq.* back");
    }
}
