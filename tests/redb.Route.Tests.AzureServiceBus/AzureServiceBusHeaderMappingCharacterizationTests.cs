using System.Text;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// What crosses an Azure Service Bus message today, producer to consumer: pinned before the header filter strategy
/// (docs/V4/headers/01-HEADER-FILTER-STRATEGY.md) takes the rule over, so the transport's default keeps it exactly.
/// The producer sends every exchange header but its own <c>redbAsb.*</c> and null values as application properties,
/// with their own types; the consumer takes every application property back.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusHeaderMappingCharacterizationTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Uri = $"asb://queue.1?connectionString={ConnectionString}";

    [Fact]
    public async Task ProducerToConsumer_TheHeadersThatCrossTheMessage()
    {
        var tag = $"hdr-char-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AzureServiceBusComponent());
        // The emulator's queue is shared by the tests of this collection: only this test's message is taken.
        ctx.AddRoutes(r => r.From(Uri + "&ackMode=auto").Process(e =>
        {
            if (e.In.Body is byte[] body && Encoding.UTF8.GetString(body) == tag)
                received.TrySetResult(e);
        }));
        await ctx.Start();

        var exchange = new Exchange(new Message(tag));
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["X-Int"] = 42;
        exchange.In.Headers["X-Null"] = null;
        exchange.In.Headers["Authorization"] = "Bearer secret";
        exchange.In.Headers["redbHttp.Method"] = "DELETE";
        exchange.In.Headers[AzureServiceBusHeaders.Prefix + "Custom"] = "own";
        var producer = ctx.GetEndpoint(Uri).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        var headers = (await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers;

        headers["X-Corr"].Should().Be("c1");
        headers["X-Int"].Should().Be(42, "an application property keeps its type");
        headers.Should().NotContainKey("X-Null", "a null value is skipped");
        headers["Authorization"].Should().Be("Bearer secret", "nothing filters a credential");
        headers["redbHttp.Method"].Should().Be("DELETE", "a control header of another transport crosses (finding 3.1)");
        headers.Should().NotContainKey(AzureServiceBusHeaders.Prefix + "Custom", "the producer keeps its own redbAsb.* back");
    }
}
