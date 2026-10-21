using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.RabbitMQ;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on RabbitMQ endpoints: on a producer it decides which exchange headers go out with
/// the request and which reply headers come back, on a consumer which delivered headers come in and which go out with
/// the RPC reply. Basic property names and the consumer's redbRmq.* metadata stay outside the strategy.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RabbitMQHeaderFilterStrategyTests
{
    private static string Uri(string queue, string? extra = null)
        => $"rabbitmq://{queue}?host=localhost&port=5672&username=admin&password=admin&declare=true&autoDelete=true{extra}";

    private static RouteContext Context()
    {
        var ctx = new RouteContext();
        ctx.AddComponent(new RabbitMQComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        return ctx;
    }

    private static async Task<IReadOnlyDictionary<string, object?>> Deliver(string producerOptions, string consumerOptions)
    {
        var queue = $"hdr-strategy-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = Context();
        ctx.AddRoutes(r => r.From(Uri(queue, consumerOptions)).Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["MessageId"] = "m-1";
        var producer = ctx.GetEndpoint(Uri(queue, producerOptions)).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers);
    }

    private static async Task<IReadOnlyDictionary<string, object?>> Reply(string producerOptions, string consumerOptions)
    {
        var queue = $"hdr-strategy-rpc-{Guid.NewGuid():N}";
        await using var ctx = Context();
        ctx.AddRoutes(r => r.From(Uri(queue, consumerOptions)).Process(e =>
        {
            e.In.Headers.Clear();
            e.In.Headers["Correlation-ID"] = "c-42";
            e.In.Headers["X-Reply"] = "r1";
            e.In.Headers["Authorization"] = "Bearer secret";
            e.In.Body = "reply";
        }));
        await ctx.Start();

        var producer = ctx.GetEndpoint(Uri(queue, "&replyTo=true&timeout=20000" + producerOptions)).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("request"));
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((exchange.HasOut ? exchange.Out! : exchange.In).Headers);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader()
    {
        var headers = await Deliver("&headerFilterStrategy=#correlationOnly", "");

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Corr");
        headers.Should().NotContainKey("Authorization");
        headers["MessageId"].Should().Be("m-1", "a basic property name goes into the property whatever the strategy says");
        headers.Should().ContainKey(RmqHeaders.RoutingKey, "the consumer's own metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn()
    {
        var headers = await Deliver("", "&headerFilterStrategy=#noSecrets");

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

    [Fact]
    public async Task OnARequestReplyProducer_TheStrategyDecidesWhatComesBackWithTheReply()
    {
        var headers = await Reply("&headerFilterStrategy=#noSecrets", "");

        headers["X-Reply"].Should().Be("r1");
        headers.Should().NotContainKey("Authorization", "the producer's strategy drops it on the way back in");
    }
}
