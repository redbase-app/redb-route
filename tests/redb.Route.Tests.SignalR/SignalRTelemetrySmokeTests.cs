using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.SignalR;
using redb.Route.Telemetry;

namespace redb.Route.Tests.SignalR;

/// <summary>Smoke test for the P1 transport span opened by <see cref="SignalRProducer"/> (Server mode).</summary>
public sealed class SignalRTelemetrySmokeTests : IAsyncLifetime
{
    private int _port;
    private SignalRConsumer? _consumer;
    private SignalRProducer? _producer;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_producer is not null) await _producer.Stop();
        if (_consumer is not null) await _consumer.Stop();
    }

    [Fact]
    public async Task SignalRProducer_EmitsTransportSpanWithMessagingTags()
    {
        var component = new SignalRComponent();

        var path = $"/127.0.0.1:{_port}/hub";

        // Consumer (server)
        var cUri = new EndpointUri("signalr", path, $"signalr:{path}", new Dictionary<string, string>());
        var cEndpoint = (SignalREndpoint)component.CreateEndpoint(cUri);
        var processor = Substitute.For<IProcessor>();
        _consumer = new SignalRConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer (server mode)
        var pPars = new Dictionary<string, string> { ["mode"] = "Server", ["method"] = "Send" };
        var pUri = new EndpointUri("signalr", path, $"signalr:{path}", pPars);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _producer = (SignalRProducer)pEndpoint.CreateProducer();
        await _producer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        await _producer.Process(new Exchange(new Message("hello")));

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("messaging.system").Should().Be("signalr");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
