using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.S3;
using redb.Route.Telemetry;
using Xunit.Abstractions;

namespace redb.Route.Tests.S3;

/// <summary>
/// Smoke test for the P1 transport span opened by <see cref="S3Producer"/>.
/// Requires MinIO docker container on localhost:9000.
/// </summary>
[Trait("Category", "Integration")]
public sealed class S3TelemetrySmokeTests
{
    private readonly ITestOutputHelper _output;
    public S3TelemetrySmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task S3Producer_EmitsTransportSpanWithS3Tags()
    {
        const string Bucket = "integration-tests";
        var qs = "serviceUrl=http://localhost:9000&accessKey=minioadmin&secretKey=minioadmin" +
                 "&region=us-east-1&forcePathStyle=true";
        var uri = EndpointUriParser.Parse($"s3://{Bucket}?{qs}");
        var endpoint = (S3Endpoint)new S3Component().CreateEndpoint(uri);
        var producer = endpoint.CreateProducer();
        await producer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var ex = new Exchange(new Message("hello"));
        ex.In.Headers[S3Headers.Key] = $"smoke-{Guid.NewGuid():N}.txt";
        try
        {
            await producer.Process(ex);
        }
        catch (Exception ioex)
        {
            _output.WriteLine($"S3 Put failed (acceptable for smoke if bucket missing): {ioex.GetType().Name}: {ioex.Message}");
        }
        finally
        {
            await producer.Stop();
        }

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty("S3Producer.Process must open a transport span even on failure");
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("redb.system").Should().Be("s3");
        activity.GetTagItem("messaging.destination.name").Should().Be(Bucket);
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
