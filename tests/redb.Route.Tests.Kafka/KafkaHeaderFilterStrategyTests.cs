using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on Kafka endpoints: on a producer it decides which exchange headers go into the
/// record, on a consumer which record headers come into the exchange; the record's content type and the consumer's
/// redbKafka.* metadata are set whatever the strategy says.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaHeaderFilterStrategyTests
{
    private const string Brokers = "localhost:29092";

    private static async Task<IReadOnlyDictionary<string, object?>> RoundTrip(string producerOptions, string consumerOptions)
    {
        var topic = $"hdr-strategy-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        ctx.AddRoutes(r => r
            .From($"kafka://{topic}?brokers={Brokers}&groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest{consumerOptions}")
            .Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body") { ContentType = "application/json" });
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        var producer = ctx.GetEndpoint($"kafka://{topic}?brokers={Brokers}{producerOptions}").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var arrived = await received.Task.WaitAsync(TimeSpan.FromSeconds(30));
        arrived.In.ContentType.Should().Be("application/json", "the content type crosses whatever the strategy says");
        return new Dictionary<string, object?>(arrived.In.Headers);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader()
    {
        var headers = await RoundTrip("&headerFilterStrategy=#correlationOnly", "");

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Corr");
        headers.Should().NotContainKey("Authorization");
        headers.Should().ContainKey(KafkaHeaders.Topic, "the consumer's own metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn()
    {
        var headers = await RoundTrip("", "&headerFilterStrategy=#noSecrets");

        headers["X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey("Authorization", "the consumer's strategy drops it on the way in");
    }
}
