using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// The statistics-ownership audit (plan KAFKA_HARDENING_AND_OPTIONS_SWEEP_PLAN, question 0).
/// In a ROUTED consumer the core's StatisticsProcessor already counts MessagesIn/BytesIn/Errors,
/// and a routed producer is counted by ToProcessor - the connector self-recording the same
/// numbers double-counted every one of them. One owner: the core counts the pipeline, the
/// connector counts only what the core cannot see.
/// </summary>
public sealed class WsStatisticsOwnershipTests : IAsyncLifetime
{
    private RouteContext? _context;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_context is not null) await _context.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task RoutedConsumer_CountsEachFrameOnce()
    {
        var port = GetFreePort();
        var uri = $"ws://127.0.0.1:{port}/count";
        var processed = 0;

        _context = new RouteContext();
        _context.AddComponent(new WsComponent());
        _context.AddRoutes(r => r.From(uri).Process(_ => Interlocked.Increment(ref processed)));
        await _context.Start();

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/count"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("hello"), WebSocketMessageType.Text, true, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref processed) < 1 && DateTime.UtcNow < deadline) await Task.Delay(100);
        processed.Should().Be(1);
        await Task.Delay(200);

        var stats = (IEndpointStatistics)_context.GetEndpoint(uri);
        stats.MessagesIn.Should().Be(1,
            "кадр один - ядро уже считает MessagesIn, самозапись коннектора задваивала");
        stats.BytesIn.Should().Be(5, "и байты тоже задваивались");
    }

    [Fact]
    public async Task RoutedProducer_CountsEachSendOnce()
    {
        var serverPort = GetFreePort();
        var serverUri = $"ws://127.0.0.1:{serverPort}/sink";
        var producerUri = $"ws://127.0.0.1:{serverPort}/sink?messageType=Text";

        _context = new RouteContext();
        _context.AddComponent(new WsComponent());
        _context.AddRoutes(r => r.From(serverUri).Process(_ => { }));
        _context.AddRoutes(r => r.From("direct:out").To(producerUri));
        await _context.Start();

        using var template = new ProducerTemplate(_context);
        template.Start();
        await template.SendAsync("direct:out", "payload");
        await Task.Delay(300);

        var stats = (IEndpointStatistics)_context.GetEndpoint(producerUri);
        stats.MessagesOut.Should().Be(1,
            "отправка одна - маршрутный .To() уже считает через ToProcessor, самозапись задваивала");
        stats.BytesOut.Should().Be(7, "wire-байты остаются за коннектором и пишутся один раз");
    }
}
