using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using redb.Route.Http;
using Xunit;

namespace redb.Route.Tests.Hosting;

/// <summary>
/// Characterization of <see cref="SharedHttpServerManager"/> as it behaves TODAY (Ф14 волна 1).
/// Written before the WebSocket/SignalR integration lands: these tests are the regression net
/// for the four transports that already share the host (Http, Grpc, Soap, As2), so any change
/// to the manager has to keep every assertion here green.
/// </summary>
public sealed class SharedHostCharacterizationTests : IAsyncLifetime
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

    // ── Multiplexing: the whole point of the shared host ──

    [Fact]
    public async Task TwoConnectorsOnOnePort_BothRoutesServed()
    {
        var port = GetFreePort();

        _manager.RegisterRoute("127.0.0.1", port, "/first", "GET",
            ctx => ctx.Response.WriteAsync("one"));
        _manager.RegisterRoute("127.0.0.1", port, "/second", "GET",
            ctx => ctx.Response.WriteAsync("two"));

        await _manager.EnsureStarted("127.0.0.1", port);

        _manager.ServerCount.Should().Be(1, "два коннектора на одном порту делят ОДИН Kestrel");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/first")).Should().Be("one");
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/second")).Should().Be("two");
    }

    [Fact]
    public async Task RouteRegisteredAfterStart_IsServed()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/early", "GET", ctx => ctx.Response.WriteAsync("early"));
        await _manager.EnsureStarted("127.0.0.1", port);

        // The route table is dynamic: a connector starting later still gets its path served.
        _manager.RegisterRoute("127.0.0.1", port, "/late", "GET", ctx => ctx.Response.WriteAsync("late"));

        (await _client.GetStringAsync($"http://127.0.0.1:{port}/late")).Should().Be("late");
    }

    [Fact]
    public async Task MethodFilter_IsHonored()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/only-post", "POST", ctx => ctx.Response.WriteAsync("posted"));
        await _manager.EnsureStarted("127.0.0.1", port);

        var post = await _client.PostAsync($"http://127.0.0.1:{port}/only-post", new StringContent(""));
        post.IsSuccessStatusCode.Should().BeTrue();

        // Path matched but method did not: 405, not 404 — the distinction is deliberate.
        var get = await _client.GetAsync($"http://127.0.0.1:{port}/only-post");
        get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);

        var missing = await _client.GetAsync($"http://127.0.0.1:{port}/nothing-here");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Listener-level conflicts must fail loud ──

    [Fact]
    public void SchemeConflictOnSamePort_Throws()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/plain", "GET", _ => Task.CompletedTask);

        var act = () => _manager.RegisterRoute("127.0.0.1", port, "/tls", "GET", _ => Task.CompletedTask,
            ssl: true, sslCertPath: "cert.pfx");

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("HTTPS");
    }

    [Fact]
    public void ProtocolConflictOnSamePort_Throws()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/h1", "GET", _ => Task.CompletedTask,
            protocol: redb.Route.Http.HttpProtocol.Http1And2);

        var act = () => _manager.RegisterRoute("127.0.0.1", port, "/h2", "POST", _ => Task.CompletedTask,
            protocol: redb.Route.Http.HttpProtocol.Http2);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("listening as");
    }

    // ── Lifecycle ──

    [Fact]
    public async Task StopIfEmpty_KeepsServerWhileRoutesRemain_StopsWhenLast_Unregistered()
    {
        var port = GetFreePort();
        var first = _manager.RegisterRoute("127.0.0.1", port, "/a", "GET", ctx => ctx.Response.WriteAsync("a"));
        var second = _manager.RegisterRoute("127.0.0.1", port, "/b", "GET", ctx => ctx.Response.WriteAsync("b"));
        await _manager.EnsureStarted("127.0.0.1", port);

        _manager.UnregisterRoute(first);
        await _manager.StopIfEmpty("127.0.0.1", port);
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/b"))
            .Should().Be("b", "сервер живёт, пока на нём остаётся хотя бы один маршрут");

        _manager.UnregisterRoute(second);
        await _manager.StopIfEmpty("127.0.0.1", port);

        var act = () => _client.GetAsync($"http://127.0.0.1:{port}/b");
        await act.Should().ThrowAsync<HttpRequestException>("последний маршрут снят — слушатель закрыт");
    }

    [Fact]
    public async Task GetBaseUrl_ReflectsScheme()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/x", "GET", _ => Task.CompletedTask);
        await _manager.EnsureStarted("127.0.0.1", port);

        _manager.GetBaseUrl("127.0.0.1", port).Should().Be($"http://127.0.0.1:{port}");
    }

    // ── WS-5: the bind address used to be a bare IPAddress.Parse ──

    [Fact]
    public async Task Host_localhost_Binds()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("localhost", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"));

        await _manager.EnsureStarted("localhost", port);

        (await _client.GetStringAsync($"http://localhost:{port}/x")).Should().Be("x",
            "IPAddress.Parse(\"localhost\") бросал FormatException, а localhost в URI консьюмера пишут постоянно");

        // Both loopbacks, not just the one we would have picked: a client that resolved localhost
        // to the other family has to connect too.
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/x")).Should().Be("x");
    }

    [Fact]
    public async Task Host_ThatResolvesToNothing_FailsWithANamedError()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("no-such-host.invalid", port, "/x", "GET", _ => Task.CompletedTask);

        var act = () => _manager.EnsureStarted("no-such-host.invalid", port);

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*no-such-host.invalid*");
    }

    [Fact]
    public async Task EnsureStarted_IsIdempotent()
    {
        var port = GetFreePort();
        _manager.RegisterRoute("127.0.0.1", port, "/x", "GET", ctx => ctx.Response.WriteAsync("x"));

        await _manager.EnsureStarted("127.0.0.1", port);
        await _manager.EnsureStarted("127.0.0.1", port);

        _manager.ServerCount.Should().Be(1);
        (await _client.GetStringAsync($"http://127.0.0.1:{port}/x")).Should().Be("x");
    }
}
