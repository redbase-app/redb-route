#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Ф14 волна 3, end to end against the live Redis container: a hub keeps its connections in the
/// memory of one process, so with more than one replica a broadcast reaches only the clients of the
/// replica that sent it. The backplane is what fixes that, and the connector's job is to have a
/// seam for it — <c>ConfigureHubServices</c> — without depending on Redis itself.
/// <para>Requires redis on localhost:6379 (docker: <c>docker start redis</c>).</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SignalRBackplaneTests : IAsyncLifetime
{
    private const string RedisConnection = "localhost:6379";

    private readonly List<SharedHttpServerManager> _managers = [];
    private readonly List<IConsumer> _consumers = [];
    private readonly List<IProducer> _producers = [];
    private readonly List<IAsyncDisposable> _connections = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _connections) await c.DisposeAsync();
        foreach (var p in _producers) await p.Stop();
        foreach (var c in _consumers) await c.Stop();
        foreach (var m in _managers) await m.DisposeAsync();
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>One "replica": its own listener, its own component, both wired to one Redis.</summary>
    private (SignalRComponent component, SignalREndpoint endpoint, int port) StartReplica(string channelPrefix)
    {
        var manager = new SharedHttpServerManager();
        _managers.Add(manager);

        var port = GetFreePort();
        var component = new SignalRComponent
        {
            ServerManager = manager,
            HubServicesConfigurator = services => services
                .AddSignalR()
                .AddStackExchangeRedis(RedisConnection, o => o.Configuration.ChannelPrefix =
                    StackExchange.Redis.RedisChannel.Literal(channelPrefix)),
        };

        var endpoint = (SignalREndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{port}/feed"));

        return (component, endpoint, port);
    }

    [Fact]
    public async Task BroadcastFromOneReplica_ReachesClientsOfTheOther()
    {
        // A prefix per run keeps parallel runs of this suite from hearing each other.
        var prefix = $"redb-route-test-{Guid.NewGuid():N}";

        var alpha = StartReplica(prefix);
        var beta = StartReplica(prefix);

        foreach (var replica in new[] { alpha, beta })
        {
            await replica.endpoint.Start();
            var consumer = replica.endpoint.CreateConsumer(Substitute.For<IProcessor>());
            _consumers.Add(consumer);
            await consumer.Start();
        }

        // The client is attached to alpha only.
        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{alpha.port}/feed").Build();
        _connections.Add(connection);
        var received = new TaskCompletionSource<string>();
        connection.On<string>("Quote", q => received.TrySetResult(q));
        await connection.StartAsync();

        // The push comes from beta, which has no clients of its own.
        var pushEndpoint = (SignalREndpoint)beta.component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{beta.port}/feed?mode=Server&method=Quote"));
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("42")));

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("42",
            "с несколькими репликами broadcast без backplane доходит только до клиентов своей реплики");
    }

    [Fact]
    public async Task WithoutBackplane_BroadcastStaysOnItsOwnReplica()
    {
        // The other half of the same statement, and the reason the README says a backplane is
        // mandatory rather than nice to have.
        var alphaManager = new SharedHttpServerManager();
        var betaManager = new SharedHttpServerManager();
        _managers.Add(alphaManager);
        _managers.Add(betaManager);

        var alphaPort = GetFreePort();
        var betaPort = GetFreePort();
        var alpha = new SignalRComponent { ServerManager = alphaManager };
        var beta = new SignalRComponent { ServerManager = betaManager };

        var alphaEndpoint = (SignalREndpoint)alpha.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{alphaPort}/feed"));
        var betaEndpoint = (SignalREndpoint)beta.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{betaPort}/feed"));

        foreach (var endpoint in new[] { alphaEndpoint, betaEndpoint })
        {
            await endpoint.Start();
            var consumer = endpoint.CreateConsumer(Substitute.For<IProcessor>());
            _consumers.Add(consumer);
            await consumer.Start();
        }

        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{alphaPort}/feed").Build();
        _connections.Add(connection);
        var received = new TaskCompletionSource<string>();
        connection.On<string>("Quote", q => received.TrySetResult(q));
        await connection.StartAsync();

        var pushEndpoint = (SignalREndpoint)beta.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{betaPort}/feed?mode=Server&method=Quote"));
        var producer = pushEndpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        await producer.Process(new Exchange(new Message("42")));

        var arrived = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        arrived.Should().NotBeSameAs(received.Task,
            "без backplane реплики не знают друг о друге — это и есть причина, по которой он обязателен");
    }
}
#endif
