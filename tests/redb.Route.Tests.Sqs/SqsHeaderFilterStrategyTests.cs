using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Sqs;
using Message = redb.Route.Core.Message;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on SQS endpoints: on a producer it decides which plain exchange headers go out as
/// message attributes, on a consumer which attributes come in (asked with the attribute's own name). A
/// <c>redbSqs.attr.X</c> header and the consumer's redbSqs.* metadata stay outside the strategy.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqsHeaderFilterStrategyTests
{
    private const string Attr = SqsHeaders.MessageAttributePrefix;

    private static string Uri(string queue, string options) => SqsDsl.Queue(queue).ServiceUrl("http://localhost:4566")
        .Region("us-east-1").Credentials("test", "test").AutoCreateQueue().Build() + options;

    private static async Task<IReadOnlyDictionary<string, object?>> RoundTrip(string producerOptions, string consumerOptions)
    {
        var queue = $"hdr-strategy-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new SqsComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        ctx.AddRoutes(r => r.From(Uri(queue, consumerOptions)).Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers[Attr + "Named"] = "by-prefix";
        var producer = ctx.GetEndpoint(Uri(queue, producerOptions)).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader_AndTheExplicitAttribute()
    {
        var headers = await RoundTrip("&headerFilterStrategy=#correlationOnly", "");

        headers[Attr + "Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey(Attr + "X-Corr");
        headers.Should().NotContainKey(Attr + "Authorization");
        headers[Attr + "Named"].Should().Be("by-prefix", "a redbSqs.attr.X header is an explicit request");
        headers.Should().ContainKey(SqsHeaders.MessageId, "the consumer's own metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn_ByTheAttributeName()
    {
        var headers = await RoundTrip("", "&headerFilterStrategy=#noSecrets");

        headers[Attr + "X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey(Attr + "Authorization", "the consumer's strategy drops it on the way in");
    }
}
