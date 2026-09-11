using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Http;
using Xunit;

namespace redb.Route.Tests.Hosting;

/// <summary>
/// Ф14 волна 1: the two extension points the shared host needs so WebSocket and SignalR can
/// live on it. Both are opt-in per listener, so the four transports that already share the host
/// (Http, Grpc, Soap, As2) keep a byte-for-byte identical pipeline.
/// </summary>
public sealed class SharedHostExtensibilityTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _manager = new();
    private readonly HttpClient _client = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
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

    // ── WebSocket support, opt-in per listener ──

    [Fact]
    public async Task WithoutEnableWebSockets_UpgradeIsNotAvailable()
    {
        var port = GetFreePort();
        bool? seenAsWebSocketRequest = null;

        _manager.RegisterRoute("127.0.0.1", port, "/ws", "GET", ctx =>
        {
            seenAsWebSocketRequest = ctx.WebSockets.IsWebSocketRequest;
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        });
        await _manager.EnsureStarted("127.0.0.1", port);

        using var ws = new ClientWebSocket();
        var act = () => ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
        await act.Should().ThrowAsync<WebSocketException>();

        seenAsWebSocketRequest.Should().BeFalse(
            "без UseWebSockets запрос не распознаётся как апгрейд — это поведение по умолчанию");
    }

    [Fact]
    public async Task EnableWebSockets_AcceptsConnectionAndEchoes()
    {
        var port = GetFreePort();

        _manager.EnableWebSockets("127.0.0.1", port);
        _manager.RegisterRoute("127.0.0.1", port, "/ws", "GET", async ctx =>
        {
            ctx.WebSockets.IsWebSocketRequest.Should().BeTrue();
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[64];
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            await socket.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Text,
                endOfMessage: true, CancellationToken.None);
        });
        await _manager.EnsureStarted("127.0.0.1", port);

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
        await client.SendAsync(Encoding.UTF8.GetBytes("ping"), WebSocketMessageType.Text, true, CancellationToken.None);

        var buf = new byte[64];
        var received = await client.ReceiveAsync(buf, CancellationToken.None);
        Encoding.UTF8.GetString(buf, 0, received.Count).Should().Be("ping");
    }

    [Fact]
    public async Task EnableWebSockets_LeavesPlainHttpRoutesUntouched()
    {
        var port = GetFreePort();
        _manager.EnableWebSockets("127.0.0.1", port);
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", ctx => ctx.Response.WriteAsync("still http"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/plain")).Should().Be("still http");
    }

    [Fact]
    public async Task EnableWebSockets_AfterStart_FailsLoud()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/x", "GET", _ => Task.CompletedTask);
        await _manager.EnsureStarted("127.0.0.1", port);

        var act = () => _manager.EnableWebSockets("127.0.0.1", port);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("already started",
                "middleware нельзя добавить в построенное приложение — это должно быть громко");
    }

    // ── Server configurators: services before Build(), endpoints after ──

    [Fact]
    public async Task ServerConfigurator_RegistersServicesAndEndpoints()
    {
        var port = GetFreePort();

        _manager.RegisterServerConfigurator("127.0.0.1", port,
            services: s => s.AddSingleton(new Marker("from-di")),
            endpoints: app => app.MapGet("/mapped", (HttpContext ctx) =>
                ctx.Response.WriteAsync(ctx.RequestServices.GetRequiredService<Marker>().Value)));

        // A plain route on the same listener keeps working alongside the mapped endpoint.
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", ctx => ctx.Response.WriteAsync("plain"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/mapped")).Should().Be("from-di",
            "конфигуратор обязан отработать до catch-all, иначе смапленный эндпоинт недостижим");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/plain")).Should().Be("plain");
    }

    [Fact]
    public async Task ServerConfigurator_AfterStart_FailsLoud()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/x", "GET", _ => Task.CompletedTask);
        await _manager.EnsureStarted("127.0.0.1", port);

        var act = () => _manager.RegisterServerConfigurator("127.0.0.1", port,
            services: s => s.AddSingleton(new Marker("too late")));

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("already started");
    }

    [Fact]
    public async Task MultipleConfigurators_AllApplied()
    {
        var port = GetFreePort();

        _manager.RegisterServerConfigurator("127.0.0.1", port,
            endpoints: app => app.MapGet("/one", () => "one"));
        _manager.RegisterServerConfigurator("127.0.0.1", port,
            endpoints: app => app.MapGet("/two", () => "two"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/one")).Should().Contain("one");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/two")).Should().Contain("two");
    }

    private sealed record Marker(string Value);
}
