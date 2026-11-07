using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.MqttNet;
using redb.Route.MqttNet.Connection;

namespace redb.Route.Tests.MqttNet;

/// <summary>
/// What crosses an MQTT message today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md). MQTT does not carry exchange headers: only the user properties the
/// route put into the one <c>redbMqtt.userProperties</c> dictionary are sent, so it is a transport of explicit consent
/// and gets no strategy.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MqttHeaderMappingCharacterizationTests
{
    private const string Broker = "server=localhost&port=11883";

    [Fact]
    public async Task ProducerToConsumer_OnlyTheUserPropertiesDictionaryCrosses()
    {
        var topic = $"test/hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new MqttComponent { ClientFactory = new DefaultMqttClientFactory() });
        ctx.AddRoutes(r => r.From($"mqtt:{topic}?mode=Subscribe&{Broker}").Process(e => received.TrySetResult(e)));
        await ctx.Start();

        var exchange = new Exchange(new Message("body"));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[MqttHeaders.UserProperties] = new Dictionary<string, string> { ["X-Explicit"] = "e1" };
        var producer = ctx.GetEndpoint($"mqtt:{topic}?mode=Publish&{Broker}").CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(15))).In.Headers;

        headers.Should().NotContainKey("X-Corr", "an exchange header is not sent");
        headers.Should().NotContainKey("Authorization");
        headers.Should().NotContainKey("redbHttp.Method");
        // The client speaks MQTT 5: user properties exist, and only the dictionary's entries (and the trace context)
        // come back, in the same one header.
        var userProperties = headers[MqttHeaders.UserProperties].Should().BeOfType<Dictionary<string, string>>().Subject;
        userProperties["X-Explicit"].Should().Be("e1");
        userProperties.Should().NotContainKey("X-Corr");
        userProperties.Should().NotContainKey("Authorization");
    }
}
