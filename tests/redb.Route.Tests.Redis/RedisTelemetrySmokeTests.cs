using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Redis;
using redb.Route.Telemetry;
using Xunit.Abstractions;

namespace redb.Route.Tests.Redis;

/// <summary>
/// Smoke test for the P1 transport span opened by <see cref="RedisProducer"/>.
/// Requires Redis docker container on localhost:6379.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RedisTelemetrySmokeTests
{
    private readonly ITestOutputHelper _output;
    public RedisTelemetrySmokeTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RedisProducer_EmitsTransportSpanWithDbSystemTag()
    {
        var component = new RedisComponent();
        var uri = EndpointUriParser.Parse(
            $"redis:SET:smoke-{Guid.NewGuid():N}?connectionString=localhost:6379");
        var endpoint = (RedisEndpoint)component.CreateEndpoint(uri);
        var producer = (RedisProducer)endpoint.CreateProducer();
        await producer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        try
        {
            await producer.Process(new Exchange(new Message("v")));
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
        activity.GetTagItem("db.system").Should().Be("redis");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
