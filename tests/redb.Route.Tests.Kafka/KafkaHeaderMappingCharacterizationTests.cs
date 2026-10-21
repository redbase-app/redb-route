using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Kafka;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// What crosses a Kafka record today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer sends every exchange header but its own <c>redbKafka.*</c>, as text; the consumer takes every record
/// header back, as text. A control header of another transport (<c>redbHttp.Method</c>) and a credential cross too.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaHeaderMappingCharacterizationTests
{
    private const string Brokers = "localhost:29092";

    [Fact]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheRecord()
    {
        var topic = $"hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new KafkaComponent());
        ctx.AddRoutes(r => r
            .From($"kafka://{topic}?brokers={Brokers}&groupId=grp-{Guid.NewGuid():N}&autoOffsetReset=Earliest")
            .Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[KafkaHeaders.Prefix + "Custom"] = "own";
        var producer = ctx.GetEndpoint($"kafka://{topic}?brokers={Brokers}").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers;

        headers["X-Corr"].Should().Be("c1");
        headers["X-Int"].Should().Be("42", "a value crosses as its text");
        headers["X-Null"].Should().Be("", "a null value crosses as an empty value, it is not skipped");
        headers["Authorization"].Should().Be("Bearer secret", "nothing filters a credential");
        headers["redbHttp.Method"].Should().Be("DELETE", "a control header of another transport crosses (finding 3.1)");
        headers.Should().NotContainKey(KafkaHeaders.Prefix + "Custom", "the producer keeps its own redbKafka.* back");
        headers[KafkaHeaders.Topic].Should().Be(topic, "the consumer adds its own metadata");
    }
}
