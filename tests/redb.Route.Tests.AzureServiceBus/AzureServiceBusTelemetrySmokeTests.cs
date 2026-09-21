using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Tests.Shared;
using Xunit.Abstractions;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// Smoke test for the P1 transport span opened by <see cref="AzureServiceBusProducer"/>.
/// Requires Azure Service Bus emulator on localhost:5300 with queue.1.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusTelemetrySmokeTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private const string Queue = "queue.1";

    private readonly ITestOutputHelper _output;
    public AzureServiceBusTelemetrySmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task AzureServiceBusProducer_EmitsTransportSpanWithMessagingTags()
    {
        var uri = EndpointUriParser.Parse($"asb://{Queue}?connectionString={ConnectionString}");
        var endpoint = (AzureServiceBusEndpoint)new AzureServiceBusComponent().CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();
        await producer.Start();

        using var capture = new SpanCapture();   // this test's spans only

        try
        {
            await producer.Process(new Exchange(new Message($"smoke-{Guid.NewGuid():N}")));
        }
        catch (Exception ex)
        {
            _output.WriteLine($"ASB Send failed (acceptable for smoke): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await producer.Stop();
            await endpoint.Stop();
        }

        var activities = capture.Spans;
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("messaging.system").Should().Be("azureservicebus");
        activity.GetTagItem("messaging.operation").Should().Be("send");
        activity.GetTagItem("messaging.destination.name").Should().Be(Queue);
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
