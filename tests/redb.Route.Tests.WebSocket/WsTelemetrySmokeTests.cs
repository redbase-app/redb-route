using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Tests.Shared;
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

        using var capture = new SpanCapture();   // this test's spans only

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

        var activities = capture.Spans;

        // The capture keeps only this test's trace; pick the producer's span in it by kind and by its own port,
        // never by position.
        var activity = activities.Should().ContainSingle(a =>
            a.Kind == ActivityKind.Producer && IsOwnEndpoint(a, _port)).Subject;
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.GetTagItem("messaging.system").Should().Be("websocket");
        activity.GetTagItem("messaging.operation").Should().Be("send");
    }

    private static bool IsOwnEndpoint(Activity activity, int port) =>
        activity.GetTagItem("redb.route.endpoint") is string endpoint && endpoint.Contains($":{port}/");

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
