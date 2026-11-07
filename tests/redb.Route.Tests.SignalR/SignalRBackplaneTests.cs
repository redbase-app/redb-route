using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Http;
using redb.Route.SignalR;
using StackExchange.Redis;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Ф14 волна 3, end to end against the live Redis container: a hub keeps its connections in the memory of one
/// process, so with more than one replica a broadcast reaches only the clients of the replica that sent it. The
/// backplane (<c>UseRedisBackplane</c>, redb.Route.SignalR.StackExchangeRedis) fixes that, and keys the Redis channels
/// on the hub path so the hubs of one listener stay apart on one Redis.
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

    private static int GetFreePort() => global::redb.Route.Tests.Shared.TestPorts.Next();

    private static Action<IServiceCollection, SignalRHubDescriptor> Backplane(string? applicationPrefix = null)
        => new SignalRRegistrationOptions().UseRedisBackplane(RedisConnection, redis =>
        {
            if (applicationPrefix is not null)
                redis.Configuration.ChannelPrefix = RedisChannel.Literal(applicationPrefix);
        }).HubServices!;

    // The client sends the token in the Authorization header on negotiate and in access_token on the upgrade.
    private static Func<HttpContext, Task<ClaimsPrincipal?>> UserFromToken => ctx =>
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query["access_token"].ToString();
        return Task.FromResult<ClaimsPrincipal?>(string.IsNullOrEmpty(token)
            ? null
            : new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, token)], "test")));
    };

    /// <summary>
    /// One "replica": its own listener and its own component, wired to one Redis. A replica is a process of its own,
    /// so it holds its backplane channels apart from the other replicas of this test unless
    /// <paramref name="process"/> puts them in one.
    /// </summary>
    private SignalRComponent Replica(Action<IServiceCollection, SignalRHubDescriptor>? backplane,
        RedisBackplaneClaims? process = null)
    {
        var manager = new SharedHttpServerManager();
        _managers.Add(manager);
        var claims = process ?? new RedisBackplaneClaims();
        return new SignalRComponent
        {
            ServerManager = manager,
            HubServicesConfigurator = backplane is null ? null : (services, hub) =>
            {
                backplane(services, hub);
                services.AddSingleton(claims);
            },
            Authenticate = UserFromToken,
        };
    }

    /// <summary>Joins the group "g" on the method "Join".</summary>
    private sealed class GroupProcessor : IProcessor
    {
        public Task Process(IExchange exchange, CancellationToken ct = default)
        {
            if (exchange.In.GetHeader<string>(SignalRHeaders.Method) == "Join")
                exchange.In.Headers[SignalRHeaders.AddToGroup] = "g";
            return Task.CompletedTask;
        }
    }

    private async Task StartHubAsync(SignalRComponent component, int port, string hubPath)
    {
        var endpoint = (SignalREndpoint)component.CreateEndpoint(EndpointUriParser.Parse($"signalr://127.0.0.1:{port}{hubPath}"));
        await endpoint.Start();
        var consumer = endpoint.CreateConsumer(new GroupProcessor());
        _consumers.Add(consumer);
        await consumer.Start();
    }

    private async Task<HubConnection> ConnectAsync(int port, string hubPath, string user = "u1")
    {
        var connection = new HubConnectionBuilder().WithUrl($"http://127.0.0.1:{port}{hubPath}",
            o => o.AccessTokenProvider = () => Task.FromResult<string?>(user)).Build();
        _connections.Add(connection);
        await connection.StartAsync();
        return connection;
    }

    private async Task PushAsync(SignalRComponent component, int port, string hubPath, string body,
        Action<IMessage>? target = null)
    {
        var endpoint = (SignalREndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"signalr://127.0.0.1:{port}{hubPath}?mode=Server&method=Quote"));
        var producer = endpoint.CreateProducer();
        _producers.Add(producer);
        await producer.Start();
        var message = new Message(body);
        target?.Invoke(message);
        await producer.Process(new Exchange(message));
    }

    [Fact]
    public async Task BroadcastFromOneReplica_ReachesClientsOfTheOther()
    {
        // A prefix of the application per run keeps parallel runs of this suite from hearing each other.
        var backplane = Backplane($"redb-route-test-{Guid.NewGuid():N}");
        var alpha = Replica(backplane);
        var beta = Replica(backplane);
        var alphaPort = GetFreePort();
        var betaPort = GetFreePort();
        await StartHubAsync(alpha, alphaPort, "/feed");
        await StartHubAsync(beta, betaPort, "/feed");

        // The client is attached to alpha only; the push comes from beta, which has no clients of its own.
        var connection = await ConnectAsync(alphaPort, "/feed");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("Quote", q => received.TrySetResult(q));
        await PushAsync(beta, betaPort, "/feed", "42");

        (await received.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("42",
            "с несколькими репликами broadcast без backplane доходит только до клиентов своей реплики");
    }

    [Fact]
    public async Task HubsOfOneListener_StayApartOnOneRedis_WithoutAnExplicitPrefix()
    {
        // No prefix of the application: the hub path alone keys the channels. Paths unique to the run keep parallel
        // runs apart.
        var run = Guid.NewGuid().ToString("N");
        var left = $"/left-{run}";
        var right = $"/right-{run}";
        var alpha = Replica(Backplane());
        var beta = Replica(Backplane());
        var alphaPort = GetFreePort();
        var betaPort = GetFreePort();
        foreach (var (component, port) in new[] { (alpha, alphaPort), (beta, betaPort) })
        {
            await StartHubAsync(component, port, left);
            await StartHubAsync(component, port, right);
        }

        // The same user in the same group on both hubs of alpha.
        var toLeft = await ConnectAsync(alphaPort, left);
        var toRight = await ConnectAsync(alphaPort, right);
        var leftGot = new ConcurrentQueue<string>();
        var rightGot = new ConcurrentQueue<string>();
        toLeft.On<string>("Quote", leftGot.Enqueue);
        toRight.On<string>("Quote", rightGot.Enqueue);
        await toLeft.InvokeAsync<object?>("Invoke", "Join", Array.Empty<object?>());
        await toRight.InvokeAsync<object?>("Invoke", "Join", Array.Empty<object?>());

        // Pushed from the other replica, so every message travels through Redis.
        await PushAsync(beta, betaPort, right, "all");
        await PushAsync(beta, betaPort, right, "group", m =>
        {
            m.Headers[SignalRHeaders.Target] = "Group";
            m.Headers[SignalRHeaders.Group] = "g";
        });
        await PushAsync(beta, betaPort, right, "user", m =>
        {
            m.Headers[SignalRHeaders.Target] = "User";
            m.Headers[SignalRHeaders.TargetUser] = "u1";
        });

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (rightGot.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        await Task.Delay(500);

        rightGot.Should().BeEquivalentTo(["all", "group", "user"]);
        leftGot.Should().BeEmpty("каналы Redis ключуются путём хаба, иначе хабы одного слушателя слышат друг друга");
    }

    [Fact]
    public async Task SamePathOnTwoPorts_OnOneRedis_IsRefused()
    {
        var path = $"/twice-{Guid.NewGuid():N}";
        var process = new RedisBackplaneClaims();
        var first = Replica(Backplane(), process);
        var second = Replica(Backplane(), process);
        await StartHubAsync(first, GetFreePort(), path);

        var act = () => StartHubAsync(second, GetFreePort(), path);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*different paths, or different ChannelPrefix*");
    }

    [Fact]
    public async Task SamePathOnTwoPorts_WithDifferentPrefixes_BothServe()
    {
        var path = $"/twice-{Guid.NewGuid():N}";
        var process = new RedisBackplaneClaims();
        var first = Replica(Backplane("stand-a"), process);
        var second = Replica(Backplane("stand-b"), process);
        var secondPort = GetFreePort();
        await StartHubAsync(first, GetFreePort(), path);
        await StartHubAsync(second, secondPort, path);

        (await ConnectAsync(secondPort, path)).State.Should().Be(HubConnectionState.Connected);
    }

    [Fact]
    public async Task WithoutBackplane_BroadcastStaysOnItsOwnReplica()
    {
        // The other half of the same statement, and the reason the README says a backplane is mandatory rather than
        // nice to have.
        var alpha = Replica(null);
        var beta = Replica(null);
        var alphaPort = GetFreePort();
        var betaPort = GetFreePort();
        await StartHubAsync(alpha, alphaPort, "/feed");
        await StartHubAsync(beta, betaPort, "/feed");

        var connection = await ConnectAsync(alphaPort, "/feed");
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<string>("Quote", q => received.TrySetResult(q));
        await PushAsync(beta, betaPort, "/feed", "42");

        var arrived = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        arrived.Should().NotBeSameAs(received.Task,
            "без backplane реплики не знают друг о друге, это и есть причина, по которой он обязателен");
    }
}

/// <summary>
/// The channel prefix and the claim of <c>UseRedisBackplane</c>, without a Redis: the options are resolved, nothing
/// connects.
/// </summary>
public sealed class SignalRBackplanePrefixTests
{
    private static RedisOptions OptionsOf(Action<RedisOptions>? configure, string hubPath)
    {
        var services = new ServiceCollection();
        new SignalRRegistrationOptions().UseRedisBackplane("localhost:6379", configure)
            .HubServices!(services, new SignalRHubDescriptor("127.0.0.1", 5000, hubPath));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RedisOptions>>().Value;
    }

    [Fact]
    public void Without_a_prefix_the_hub_path_keys_the_channels()
        => OptionsOf(null, "/chat").Configuration.ChannelPrefix.ToString().Should().Be("/chat");

    [Fact]
    public void A_prefix_of_the_application_comes_first_and_the_hub_path_follows()
        => OptionsOf(r => r.Configuration.ChannelPrefix = RedisChannel.Literal("myapp"), "/chat")
            .Configuration.ChannelPrefix.ToString().Should().Be("myapp/chat");

    [Fact]
    public void Each_hub_container_gets_its_own_path()
    {
        OptionsOf(null, "/left").Configuration.ChannelPrefix.ToString().Should().Be("/left");
        OptionsOf(null, "/right").Configuration.ChannelPrefix.ToString().Should().Be("/right");
    }

    [Fact]
    public void A_component_built_by_hand_gets_the_same_backplane_after_its_own_services()
    {
        var own = false;
        var component = new SignalRComponent { HubServicesConfigurator = (_, _) => own = true }
            .UseRedisBackplane("localhost:6379", r => r.Configuration.ChannelPrefix = RedisChannel.Literal("honest"));

        var services = new ServiceCollection();
        component.HubServicesConfigurator!(services, new SignalRHubDescriptor("0.0.0.0", 5090, "/hub/ui"));
        using var provider = services.BuildServiceProvider();

        own.Should().BeTrue("свои сервисы компонента остаются");
        provider.GetRequiredService<IOptions<RedisOptions>>().Value.Configuration.ChannelPrefix.ToString()
            .Should().Be("honest/hub/ui");
    }

    [Fact]
    public async Task A_claim_is_released_when_its_hub_stops()
    {
        var redis = OptionsOf(null, "/x");
        var process = new RedisBackplaneClaims();
        IHostedService first = new RedisBackplaneClaim(new SignalRHubDescriptor("127.0.0.1", 5001, "/x"), redis, process);
        IHostedService second = new RedisBackplaneClaim(new SignalRHubDescriptor("127.0.0.1", 5002, "/x"), redis, process);

        await first.StartAsync(default);
        await second.Invoking(s => s.StartAsync(default)).Should().ThrowAsync<InvalidOperationException>();

        await first.StopAsync(default);
        await second.StartAsync(default);
        await second.StopAsync(default);
    }
}
