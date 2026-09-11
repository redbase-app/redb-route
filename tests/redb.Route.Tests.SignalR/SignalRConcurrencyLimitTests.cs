using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;
using SignalRDsl = redb.Route.SignalR.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Admission limits on the SignalR hub (план HTTP_CONCURRENCY_LIMITS_PLAN, волна В4):
/// <c>maxConnections</c> aborts an over-limit connection at OnConnected — before the Connected
/// lifecycle event reaches the pipeline — and counts it in Rejected;
/// <c>maxParallelInvocationsPerClient</c> is SignalR's own per-client invocation parallelism.
/// </summary>
public sealed class SignalRConcurrencyLimitTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly List<IAsyncDisposable> _connections = [];
    private SignalRConsumer? _consumer;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        if (_consumer is not null) await _consumer.Stop();
        await _manager.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class EchoProcessor : redb.Route.Abstractions.IProcessor
    {
        public Task Process(redb.Route.Abstractions.IExchange exchange, CancellationToken ct = default)
        {
            exchange.Out = new Message(exchange.In.Body);
            return Task.CompletedTask;
        }
    }

    private async Task<(SignalREndpoint Endpoint, int Port)> StartHub(string extraParams)
    {
        var port = GetFreePort();
        var component = new SignalRComponent { ServerManager = _manager };
        var uri = EndpointUriParser.Parse($"signalr://127.0.0.1:{port}/gate?{extraParams}");
        var endpoint = (SignalREndpoint)component.CreateEndpoint(uri);
        await endpoint.Start();
        _consumer = (SignalRConsumer)endpoint.CreateConsumer(new EchoProcessor());
        await _consumer.Start();
        return (endpoint, port);
    }

    private async Task<HubConnection> ConnectAsync(int port)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{port}/gate")
            .Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    [Fact]
    public async Task OverMaxConnections_IsAborted_AndCountedAsRejected()
    {
        var (endpoint, port) = await StartHub("inOut=true&maxConnections=1");

        var first = await ConnectAsync(port);
        first.State.Should().Be(HubConnectionState.Connected);

        // The second connection must not survive: the hub aborts it at OnConnected. Depending on
        // the transport handshake the client sees the failure either as a StartAsync exception or
        // as an immediately-dead connection.
        HubConnection? second = null;
        try
        {
            second = await ConnectAsync(port);
            // Give the abort a moment to propagate.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (second.State == HubConnectionState.Connected && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            second.State.Should().NotBe(HubConnectionState.Connected,
                "второе соединение сверх maxConnections=1 обязано быть оборвано");
        }
        catch (Exception)
        {
            // StartAsync failing outright is the equally valid outcome.
        }

        endpoint.Rejected.Should().BeGreaterThanOrEqualTo(1,
            "каждый отбитый connect считается (транспортный fallback может пробовать несколько раз)");

        // The slot frees on disconnect: after the first client leaves, a new one gets in.
        await first.DisposeAsync();
        await Task.Delay(300);
        var third = await ConnectAsync(port);
        third.State.Should().Be(HubConnectionState.Connected, "слот освободился после дисконнекта");
    }

    [Fact]
    public async Task UriAndDsl_CarryTheOptions()
    {
        var built = SignalRDsl.Hub("127.0.0.1:5000/chatHub")
            .MaxConnections(10)
            .MaxParallelInvocationsPerClient(4)
            .Build();

        built.Should().Contain("maxConnections=10")
            .And.Contain("maxParallelInvocationsPerClient=4");

        var component = new SignalRComponent { ServerManager = _manager };
        var endpoint = (SignalREndpoint)component.CreateEndpoint(EndpointUriParser.Parse(built));
        endpoint.EndpointOptions.MaxConnections.Should().Be(10);
        endpoint.EndpointOptions.MaxParallelInvocationsPerClient.Should().Be(4);
        await Task.CompletedTask;
    }
}
