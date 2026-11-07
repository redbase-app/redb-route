using System.Text;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// <c>headerFilterStrategy=#name</c> on Azure Service Bus endpoints: on a producer it decides which exchange headers go
/// out as application properties, on a consumer which application properties come in. The consumer's redbAsb.*
/// metadata is set whatever the strategy says.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusHeaderFilterStrategyTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const string Uri = $"asb://queue.1?connectionString={ConnectionString}";

    private static async Task<IReadOnlyDictionary<string, object?>> RoundTrip(string producerOptions, string consumerOptions)
    {
        var tag = $"hdr-strategy-{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IExchange>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AzureServiceBusComponent());
        ctx.AddToRegistry("correlationOnly", new DefaultHeaderFilterStrategy { OutFilter = ["Correlation-ID"], FilterOnMatch = false });
        ctx.AddToRegistry("noSecrets", new DefaultHeaderFilterStrategy { InFilter = ["Authorization"] });
        // The emulator's queue is shared by the tests of this collection: only this test's message is taken.
        ctx.AddRoutes(r => r.From(Uri + "&ackMode=auto" + consumerOptions).Process(e =>
        {
            if (e.In.Body is byte[] body && Encoding.UTF8.GetString(body) == tag)
                received.TrySetResult(e);
        }));
        await ctx.Start();

        var exchange = new Exchange(new Message(tag));
        exchange.In.Headers["Correlation-ID"] = "c-42";
        exchange.In.Headers["X-Corr"] = "c1";
        exchange.In.Headers["Authorization"] = "Bearer secret";
        var producer = ctx.GetEndpoint(Uri + producerOptions).CreateProducer();
        await producer.Start();
        await producer.Process(exchange);
        await producer.Stop();

        return new Dictionary<string, object?>((await received.Task.WaitAsync(TimeSpan.FromSeconds(30))).In.Headers);
    }

    [Fact]
    public async Task OnAProducer_AWhitelistSendsOnlyTheNamedHeader()
    {
        var headers = await RoundTrip("&headerFilterStrategy=#correlationOnly", "");

        headers["Correlation-ID"].Should().Be("c-42");
        headers.Should().NotContainKey("X-Corr");
        headers.Should().NotContainKey("Authorization");
        headers.Should().ContainKey(AzureServiceBusHeaders.SequenceNumber,
            "the consumer's own metadata is set whatever the strategy says");
    }

    [Fact]
    public async Task OnAConsumer_TheStrategyDecidesWhatComesIn()
    {
        var headers = await RoundTrip("", "&headerFilterStrategy=#noSecrets");

        headers["X-Corr"].Should().Be("c1");
        headers.Should().NotContainKey("Authorization", "the consumer's strategy drops it on the way in");
    }
}
