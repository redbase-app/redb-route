using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Elasticsearch;
using redb.Route.Telemetry;
using redb.Route.Tests.Shared;
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

        using var capture = new SpanCapture();   // this test's spans only

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
            await DeleteIndex(index);
        }

        var activities = capture.Spans;
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("db.system").Should().Be("elasticsearch");
        activity.GetTagItem("messaging.destination.name").Should().Be(index);
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }

    /// <summary>
    /// Deletes the test's index by name (Elasticsearch 8 refuses a wildcard delete); a failed index attempt leaves none.
    /// </summary>
    private static async Task DeleteIndex(string index)
    {
        var client = new Elastic.Clients.Elasticsearch.ElasticsearchClient(
            new Elastic.Clients.Elasticsearch.ElasticsearchClientSettings(new Uri("http://localhost:9200")));
        var deleted = await client.Indices.DeleteAsync(index, d => d.IgnoreUnavailable(true));
        if (!deleted.IsValidResponse)
            throw new InvalidOperationException($"The smoke test's index was not deleted: {deleted.DebugInformation}");
    }
}
