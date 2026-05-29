using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.File;
using redb.Route.Telemetry;

namespace redb.Route.Tests.File;

/// <summary>Smoke test for the P1 transport span opened by <see cref="FileProducer"/>.</summary>
public sealed class FileTelemetrySmokeTests : IDisposable
{
    private readonly string _tempDir;

    public FileTelemetrySmokeTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "redb-route-tel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task FileProducer_EmitsTransportSpanWithFileSystemTag()
    {
        var component = new FileComponent();
        var path = "/" + _tempDir.Replace("\\", "/");
        var uri = new EndpointUri("file", path, $"file://{path}", new Dictionary<string, string>());
        var endpoint = (FileEndpoint)component.CreateEndpoint(uri);
        var producer = (FileProducer)endpoint.CreateProducer();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var message = new Message { Body = System.Text.Encoding.UTF8.GetBytes("hi") };
        message.Headers[FileHeaders.FileName] = "smoke.txt";
        await producer.Process(new Exchange(message) { Pattern = ExchangePattern.InOnly });

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("redb.system").Should().Be("file");
        activity.GetTagItem("messaging.operation").Should().Be("write");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }
}
