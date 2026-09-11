using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// The statistics-ownership audit: in a ROUTED consumer the core's StatisticsProcessor counts
/// MessagesIn/Errors - the connector self-recording the same numbers double-counted them.
/// </summary>
public sealed class SignalRStatisticsOwnershipTests : IAsyncLifetime
{
    private RouteContext? _context;
    private HubConnection? _connection;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
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
    public async Task RoutedConsumer_CountsEachInvocationOnce()
    {
        var port = GetFreePort();
        var uri = $"signalr://127.0.0.1:{port}/owned?inOut=true";
        var invocations = 0;

        _context = new RouteContext();
        _context.AddComponent(new SignalRComponent());
        _context.AddRoutes(r => r.From(uri).Process(e =>
        {
            // The Connected lifecycle event is an exchange too - count invocations only.
            if (e.In.Body is not null) Interlocked.Increment(ref invocations);
            e.Out = new Message("ok");
        }));
        await _context.Start();

        _connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}/owned").Build();
        await _connection.StartAsync();
        await _connection.InvokeAsync<object?>("Invoke", "Send", new object?[] { "one" });

        invocations.Should().Be(1);
        // Exactly TWO exchanges reached the route: the Connected lifecycle event and the
        // invocation. The core counts each once; with the old self-recording this was 4.
        ((IEndpointStatistics)_context.GetEndpoint(uri)).MessagesIn.Should().Be(2,
            "ядро считает каждый обмен один раз; самозапись коннектора давала 4");
    }
}
