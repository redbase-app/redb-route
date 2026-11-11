using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;

namespace redb.Route.Tests.Http;

/// <summary>
/// A listener that fails to start (its port still taken) leaves nothing behind on the shared host. The HTTP consumer
/// unregisters its route, so a start by hand afterwards registers once and a stop gives the port back; the shared
/// host lets go of the web application that did not start.
/// </summary>
[Collection("HttpServer")]
public sealed class ListenerFailedStartTests : IAsyncLifetime
{
    private readonly SharedHttpServerManager _servers = new();
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly int _port = global::redb.Route.Tests.Shared.TestPorts.Next();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _servers.DisposeAsync();
    }

    private TcpListener TakePort()
    {
        var squatter = new TcpListener(IPAddress.Loopback, _port);
        squatter.Start();
        return squatter;
    }

    [Fact]
    public async Task A_route_started_by_hand_after_a_failed_listener_start_registers_once_and_frees_the_port()
    {
        var calls = 0;
        await using var context = new RouteContext();
        context.AddComponent(new HttpComponent { ServerManager = _servers });
        context.AddRoutes(r => r.From($"http://127.0.0.1:{_port}/ping?inOut=true").RouteId("taken-port").Process(e =>
        {
            Interlocked.Increment(ref calls);
            e.In.Body = "pong";
        }));

        var squatter = TakePort();
        try
        {
            await context.Start();
            context.GetRoute("taken-port")!.Status.Should().Be(RouteStatus.Errored, "the port is taken");
        }
        finally
        {
            squatter.Stop();
        }

        await context.StartRoute("taken-port");
        (await _client.GetStringAsync($"http://127.0.0.1:{_port}/ping")).Should().Be("pong");
        Volatile.Read(ref calls).Should().Be(1);

        await context.Stop();
        var stopped = () => _client.GetAsync($"http://127.0.0.1:{_port}/ping");
        await stopped.Should().ThrowAsync<HttpRequestException>(
            "no stale registration of the failed start keeps the listener up after the route stopped");
    }

    [Fact]
    public async Task The_shared_host_lets_go_of_a_web_application_that_did_not_start()
    {
        var registration = _servers.RegisterRoute("127.0.0.1", _port, "/ping", "GET", ctx => ctx.Response.WriteAsync("pong"));

        var squatter = TakePort();
        try
        {
            var start = () => _servers.EnsureStarted("127.0.0.1", _port);
            await start.Should().ThrowAsync<Exception>("the port is taken");
            ServerEntryApp().Should().BeNull("the web application whose start failed is disposed, not kept");
        }
        finally
        {
            squatter.Stop();
        }

        await _servers.EnsureStarted("127.0.0.1", _port);
        (await _client.GetStringAsync($"http://127.0.0.1:{_port}/ping")).Should().Be("pong");

        _servers.UnregisterRoute(registration);
        await _servers.StopIfEmpty("127.0.0.1", _port);
    }

    private object? ServerEntryApp()
    {
        var field = typeof(SharedHttpServerManager).GetField("_servers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var servers = (System.Collections.IDictionary)field.GetValue(_servers)!;
        var entry = servers.Values.Cast<SharedHttpServerManager.ServerEntry>().Single();
        return entry.App;
    }
}
