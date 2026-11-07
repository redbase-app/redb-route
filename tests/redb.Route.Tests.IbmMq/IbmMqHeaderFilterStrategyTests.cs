using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.IbmMq;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on IBM MQ endpoints, on the poll and the XMS (<c>receiveMode=listener</c>) paths:
/// on a producer it decides which exchange headers go into the header catalogue and which catalogued headers of a
/// reply come back, on a consumer which catalogued headers come in and which headers go out with the reply. The
/// redbIbmMq.* metadata is set whatever the strategy says.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqHeaderFilterStrategyTests
{
    private const string Connection =
        "host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";

    private static RouteContext Context()
    {
        var ctx = new RouteContext();
        ctx.AddComponent(new IbmMqComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        return ctx;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> Deliver(string producerOptions, string consumerOptions)
    {
        const string queue = "DEV.QUEUE.2";
        var tag = $"hdr-strategy-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = Context();
        // The dev queue is shared: only this test's message is taken.
        ctx.AddRoutes(r => r.From($"wmq:{queue}?{Connection}{consumerOptions}").Process(e =>
        {
            var body = e.In.Body is byte[] bytes ? Encoding.UTF8.GetString(bytes) : e.In.Body?.ToString();
            if (body == tag)
                received.TrySetResult(e);
        }));
        await ctx.Start();

        var exchange = new Exchange(new Message(tag));
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        var producer = ctx.GetEndpoint($"wmq:{queue}?{Connection}{producerOptions}").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> Reply(string producerOptions, string consumerOptions)
    {
        const string queue = "DEV.QUEUE.3";
        await using var ctx = Context();
        ctx.AddRoutes(r => r.From($"wmq:{queue}?{Connection}{consumerOptions}").Process(e =>
        {
            e.Out = new Message("reply");
            e.Out.Headers["Correlation-ID"] = "c-42";
            e.Out.Headers["X-Reply"] = "r1";
            e.Out.Headers["Authorization"] = "Bearer secret";
        }));
        await ctx.Start();

        var producer = ctx.GetEndpoint($"wmq:{queue}?{Connection}&replyTo=true&timeout=15{producerOptions}").CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("request"));
        await producer.Process(exchange);
        await producer.Stop();

        exchange.HasOut.Should().BeTrue("the request-reply producer must receive the reply");
        return new Dictionary<string, object?>(exchange.Out!.Headers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&receiveMode=listener")]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader(string receiveMode)
    {
        var headers = await Deliver("&headerFilterStrategy=#correlationOnly", receiveMode);

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Corr");
        headers.Should().NotContainKey("Authorization");
        headers.Should().ContainKey(IbmMqHeaders.MsgId, "the consumer's own metadata is set whatever the strategy says");
    }

    [Theory]
    [InlineData("")]
    [InlineData("&receiveMode=listener")]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn(string receiveMode)
    {
        var headers = await Deliver("", "&headerFilterStrategy=#noSecrets" + receiveMode);

        headers["X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey("Authorization", "the consumer's strategy drops it on the way in");
    }

    [Fact]
    public async Task OnAReplyingConsumer_TheStrategyDecidesWhatTheReplyCarries()
    {
        var headers = await Reply("", "&headerFilterStrategy=#correlationOnly");

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Reply");
        headers.Should().NotContainKey("Authorization");
    }

    [Theory]
    [InlineData("")]
    [InlineData("&receiveMode=listener")]
    public async Task OnARequestReplyProducer_TheStrategyDecidesWhatComesBackWithTheReply(string receiveMode)
    {
        var headers = await Reply("&headerFilterStrategy=#noSecrets" + receiveMode, "");

        headers["X-Reply"].Should().Be("r1");
        headers.Should().NotContainKey("Authorization", "the producer's strategy drops it on the way back in");
    }
}
