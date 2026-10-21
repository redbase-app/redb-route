using System.Net;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Http;
using Xunit;

namespace redb.Route.Tests.Hosting;

/// <summary>
/// Ф14 волна 1, reworked for 4.4.0: the two extension points the shared host has for transports that are not a route
/// table entry. Every listener accepts WebSocket upgrades, and <see cref="SharedHttpServerManager.RegisterEndpoints"/>
/// maps framework endpoints with a container of their own onto a listener, before or after it started.
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

    private static int GetFreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static ServiceProvider Services(string marker)
        => new ServiceCollection().AddRouting().AddSingleton(new Marker(marker)).BuildServiceProvider();

    // ── WebSocket upgrades on every listener ──

    [Fact]
    public async Task EveryListener_AcceptsWebSocketUpgrades()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/ws", "GET", async ctx =>
        {
            ctx.WebSockets.IsWebSocketRequest.Should().BeTrue("UseWebSockets стоит на каждом слушателе");
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
    public async Task EnableWebSockets_AcceptsConnectionAndEchoes()
    {
        var port = GetFreePort();

        _manager.EnableWebSockets("127.0.0.1", port, TimeSpan.FromSeconds(5));
        _manager.RegisterRoute("127.0.0.1", port, "/ws", "GET", async ctx =>
        {
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
    public async Task WebSocketUpgrades_LeavePlainHttpRoutesUntouched()
    {
        var port = GetFreePort();
        _manager.EnableWebSockets("127.0.0.1", port);
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", ctx => ctx.Response.WriteAsync("still http"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/plain")).Should().Be("still http");
    }

    [Fact]
    public async Task EnableWebSockets_AfterStart_ChangesNothing()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"));
        await _manager.EnsureStarted("127.0.0.1", port);

        _manager.Invoking(m => m.EnableWebSockets("127.0.0.1", port)).Should().NotThrow(
            "апгрейды уже принимаются, а keep-alive работающего слушателя не меняется");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/x")).Should().Be("x");
    }

    // ── Endpoints with a container of their own ──

    [Fact]
    public async Task RegisterEndpoints_ServesFromTheRegistrationsContainer()
    {
        var port = GetFreePort();
        using var services = Services("from-registration");

        using var registration = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/mapped", (HttpContext ctx) =>
                ctx.Response.WriteAsync(ctx.RequestServices.GetRequiredService<Marker>().Value)));

        // A plain route on the same listener keeps working alongside the mapped endpoint.
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", ctx => ctx.Response.WriteAsync("plain"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/mapped")).Should().Be("from-registration",
            "запрос к эндпоинту регистрации идёт в области её контейнера");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/plain")).Should().Be("plain");
    }

    [Fact]
    public async Task RegisterEndpoints_AfterStart_JoinsTheRunningListener()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", ctx => ctx.Response.WriteAsync("plain"));
        await _manager.EnsureStarted("127.0.0.1", port);
        using var services = Services("late");

        using var registration = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/late", (HttpContext ctx) =>
                ctx.Response.WriteAsync(ctx.RequestServices.GetRequiredService<Marker>().Value)));

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/late")).Should().Be("late",
            "эндпоинты встают в работающий слушатель");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/plain")).Should().Be("plain");
    }

    [Fact]
    public async Task MultipleRegistrations_EachWithItsOwnContainer()
    {
        var port = GetFreePort();
        using var oneServices = Services("one");
        using var twoServices = Services("two");

        using var one = _manager.RegisterEndpoints("127.0.0.1", port, oneServices,
            endpoints => endpoints.MapGet("/one", (HttpContext ctx) =>
                ctx.Response.WriteAsync(ctx.RequestServices.GetRequiredService<Marker>().Value)));
        using var two = _manager.RegisterEndpoints("127.0.0.1", port, twoServices,
            endpoints => endpoints.MapGet("/two", (HttpContext ctx) =>
                ctx.Response.WriteAsync(ctx.RequestServices.GetRequiredService<Marker>().Value)));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/one")).Should().Be("one");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/two")).Should().Be("two");
    }

    [Fact]
    public async Task DisposingARegistration_RemovesItsEndpointsOnly()
    {
        var port = GetFreePort();
        using var services = Services("x");
        var gone = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/gone", () => "gone"));
        using var kept = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/kept", () => "kept"));
        await _manager.EnsureStarted("127.0.0.1", port);

        gone.Dispose();
        gone.Dispose();

        using (var response = await _client.GetAsync($"http://127.0.0.1:{port}/gone"))
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/kept")).Should().Be("kept");
    }

    [Fact]
    public async Task ARegistrationKeepsTheListenerUp_UntilItIsDisposed()
    {
        var port = GetFreePort();
        using var services = Services("x");
        var registration = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/only", () => "only"));
        await _manager.EnsureStarted("127.0.0.1", port);

        await _manager.StopIfEmpty("127.0.0.1", port);
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/only")).Should().Be("only");

        registration.Dispose();
        await _manager.StopIfEmpty("127.0.0.1", port);
        _manager.ServerCount.Should().Be(0);
    }

    [Fact]
    public async Task ALiteralEndpoint_WinsOverTheRouteTable()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/same", "GET", ctx => ctx.Response.WriteAsync("route-table"));
        using var services = Services("x");
        var registration = _manager.RegisterEndpoints("127.0.0.1", port, services,
            endpoints => endpoints.MapGet("/same", () => "endpoint"));
        await _manager.EnsureStarted("127.0.0.1", port);

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/same")).Should().Be("endpoint",
            "catch-all таблицы маршрутов стоит последним");

        registration.Dispose();
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/same")).Should().Be("route-table");
    }

    [Fact]
    public void RegisterEndpoints_WithoutAnyEndpoint_FailsLoud()
    {
        using var services = Services("x");
        _manager.Invoking(m => m.RegisterEndpoints("127.0.0.1", GetFreePort(), services, _ => { }))
            .Should().Throw<ArgumentException>();
    }

    private sealed record Marker(string Value);
}
