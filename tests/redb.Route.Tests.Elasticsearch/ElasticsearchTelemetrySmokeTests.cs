using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Elasticsearch;
using redb.Route.Telemetry;
using Xunit.Abstractions;

namespace redb.Route.Tests.Elasticsearch;

/// <summary>
/// Smoke test for the P1 transport span opened by <see cref="ElasticsearchProducer"/>.
/// Requires Elasticsearch docker container on localhost:9200.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ElasticsearchTelemetrySmokeTests
{
    private readonly ITestOutputHelper _output;
    public ElasticsearchTelemetrySmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ElasticsearchProducer_EmitsTransportSpanWithDbSystemTag()
    {
        var index = $"smoke-{Guid.NewGuid():N}";
        var uri = EndpointUriParser.Parse($"elasticsearch://Index:{index}?nodes=http://localhost:9200");
        var endpoint = (ElasticsearchEndpoint)new ElasticsearchComponent().CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var ex = new Exchange(new Message("""{"title":"smoke"}"""));
        try
        {
            await producer.Process(ex);
        }
        catch (Exception ioex)
        {
            _output.WriteLine($"ES Index failed (acceptable for smoke): {ioex.GetType().Name}: {ioex.Message}");
        }
        finally
        {
            await producer.Stop();
        }

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("db.system").Should().Be("elasticsearch");
        activity.GetTagItem("messaging.destination.name").Should().Be(index);
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
