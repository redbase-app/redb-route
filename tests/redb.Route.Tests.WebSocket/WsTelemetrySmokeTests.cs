using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>Smoke test for the P1 transport span opened by <see cref="WsProducer"/>.</summary>
public sealed class WsTelemetrySmokeTests : IAsyncLifetime
{
    private int _port;
    private WsConsumer? _serverConsumer;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_serverConsumer is not null) await _serverConsumer.Stop();
    }

    [Fact]
    public async Task WsProducer_EmitsTransportSpanWithMessagingTags()
    {
        // Start an embedded WS server (consumer)
        var component = new WsComponent();
        var pars = new Dictionary<string, string> { ["messageType"] = "Text" };
        var uri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", pars);
        var endpoint = (WsEndpoint)component.CreateEndpoint(uri);

        var processor = Substitute.For<IProcessor>();
        _serverConsumer = new WsConsumer(endpoint, processor, endpoint.EndpointOptions);
        await _serverConsumer.Start();

        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var producer = (WsProducer)endpoint.CreateProducer();
        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message("ping")));
            await Task.Delay(100);
        }
        finally
        {
            await producer.Stop();
        }

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        var activity = activities.First();
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Producer);
        activity.GetTagItem("messaging.system").Should().Be("websocket");
        activity.GetTagItem("messaging.operation").Should().Be("send");
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
