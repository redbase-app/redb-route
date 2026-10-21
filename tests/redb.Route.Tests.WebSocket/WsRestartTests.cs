using System.Net.WebSockets;
using System.Text;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// A WebSocket consumer keeps serving after <c>Stop()</c> and <c>Start()</c> of the same route context: the stop
/// closes the listener's path, the start opens it again on the cached endpoint.
/// </summary>
public sealed class WsRestartTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly RouteContext _ctx = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        await _manager.DisposeAsync();
    }

    private static async Task<string> EchoAsync(int port, string text)
    {
        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/chat"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[64];
        var result = await client.ReceiveAsync(buffer, CancellationToken.None);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task Consumer_serves_after_the_context_is_stopped_and_started_again()
    {
        var port = global::redb.Route.Tests.Shared.TestPorts.Next();
        _ctx.AddComponent(new WsComponent { ServerManager = _manager });
        _ctx.AddRoutes(r => r.From($"ws://127.0.0.1:{port}/chat?inOut=true").Process(e => e.Out = new Message("echo:" + e.In.Body)));

        await _ctx.Start();
        (await EchoAsync(port, "first")).Should().Be("echo:first");

        await _ctx.Stop();
        await _ctx.Start();
        (await EchoAsync(port, "second")).Should().Be("echo:second", "WebSocket-приёмник после рестарта отвечает");
    }
}
