using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using OpenTelemetry;
using OpenTelemetry.Trace;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// Smoke test for the P1 transport span opened by <see cref="TcpProducer"/>.
/// </summary>
public sealed class TcpTelemetrySmokeTests : IAsyncLifetime
{
    private int _port;
    private TcpListener? _server;
    private CancellationTokenSource? _cts;

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _cts = new CancellationTokenSource();
        _server = new TcpListener(IPAddress.Loopback, _port);
        _server.Start();
        _ = AcceptLoop(_cts.Token);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts?.Cancel();
        _server?.Stop();
        _cts?.Dispose();
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _server!.AcceptTcpClientAsync(ct);
                _ = HandleClient(client, ct);
            }
        }
        catch { }
    }

    private static async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            while (!ct.IsCancellationRequested)
            {
                var data = await TcpCodec.ReadMessageAsync(stream, TcpFraming.TextLine, "\n", 8192, ct);
                if (data is null) break;
            }
        }
        catch { }
        finally { client.Dispose(); }
    }

    [Fact]
    public async Task TcpProducer_EmitsTransportSpanWithNetworkTags()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddSource(RouteActivitySource.SourceName)
            .AddInMemoryExporter(activities)
            .Build()!;

        var component = new TcpComponent();
        var pars = new Dictionary<string, string> { ["framing"] = "TextLine" };
        var uri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", pars);
        var endpoint = (TcpEndpoint)component.CreateEndpoint(uri);
        var producer = (TcpProducer)endpoint.CreateProducer();

        await producer.Start();
        try
        {
            await producer.Process(new Exchange(new Message("ping")));
        }
        finally
        {
            await producer.Stop();
        }

        tracer.ForceFlush(1000);
        activities.Should().NotBeEmpty();
        // Filter to THIS producer's span by its unique destination port — the in-memory
        // exporter listens on the process-global RouteActivitySource and can capture spans
        // emitted by other test classes running in parallel.
        var activity = activities.Single(a =>
            Equals(a.GetTagItem("messaging.destination.name"), $"127.0.0.1:{_port}"));
        activity.Source.Name.Should().Be(RouteActivitySource.SourceName);
        activity.Kind.Should().Be(ActivityKind.Client);
        activity.GetTagItem("network.transport").Should().Be("tcp");
        activity.GetTagItem("redb.route.endpoint").Should().NotBeNull();
        activity.GetTagItem("messaging.destination.name").Should().Be($"127.0.0.1:{_port}");
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
