using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Integration tests for server-mode producer: broadcasts to connected clients
/// via IHubContext from the consumer's embedded Kestrel.
/// </summary>
public class SignalRServerProducerTests : IAsyncLifetime
{
    private int _port;
    private SignalRConsumer? _consumer;
    private SignalRProducer? _serverProducer;
    private readonly List<HubConnection> _clients = [];

    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = [];
    #pragma warning disable CS0649 // Field is never assigned to
    private Func<IExchange, Task>? _processorAction;
    #pragma warning restore CS0649

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
        {
            try { await c.StopAsync(); } catch { }
            await c.DisposeAsync();
        }

        if (_serverProducer is not null) await _serverProducer.Stop();
        if (_consumer is not null) await _consumer.Stop();
    }

    private async Task<(SignalRConsumer consumer, SignalRProducer serverProducer)> CreateServerPair(
        string? defaultGroup = null)
    {
        var component = new SignalRComponent();

        // Consumer
        var consumerParams = new Dictionary<string, string>();
        if (defaultGroup is not null) consumerParams["defaultGroup"] = defaultGroup;

        var cPath = $"/127.0.0.1:{_port}/hub";
        var cUri = new EndpointUri("signalr", cPath, $"signalr:{cPath}", consumerParams);
        var cEndpoint = (SignalREndpoint)component.CreateEndpoint(cUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var ex = ci.Arg<IExchange>();
                _lastExchange = ex;
                lock (_capturedExchanges) _capturedExchanges.Add(ex);
                if (_processorAction is not null)
                    await _processorAction(ex);
            });

        _consumer = new SignalRConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Server-mode Producer (same URI, mode=Server)
        var producerParams = new Dictionary<string, string> { ["mode"] = "Server" };
        var pUri = new EndpointUri("signalr", cPath, $"signalr:{cPath}", producerParams);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _serverProducer = (SignalRProducer)pEndpoint.CreateProducer();
        await _serverProducer.Start();

        return (_consumer, _serverProducer);
    }

    private async Task<HubConnection> ConnectClient(string hubPath = "/hub")
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{_port}{hubPath}")
            .Build();

        await connection.StartAsync();
        _clients.Add(connection);
        return connection;
    }

    // ── Broadcast to All ──

    [Fact]
    public async Task ServerMode_BroadcastToAll()
    {
        var (consumer, producer) = await CreateServerPair();

        var received = new List<string>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = await ConnectClient();
        client.On<string>("Notify", msg =>
        {
            lock (received) received.Add(msg);
            tcs.TrySetResult();
        });
        await Task.Delay(100);

        var exchange = new Exchange(new Message("hello broadcast"));
        exchange.In.Headers[SignalRHeaders.Method] = "Notify";
        await producer.Process(exchange);

        await Task.WhenAny(tcs.Task, Task.Delay(5_000));

        lock (received)
        {
            received.Should().Contain("hello broadcast");
        }
    }

    [Fact]
    public async Task ServerMode_BroadcastToMultipleClients()
    {
        var (consumer, producer) = await CreateServerPair();

        var received1 = new List<string>();
        var received2 = new List<string>();
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client1 = await ConnectClient();
        client1.On<string>("Msg", msg => { lock (received1) received1.Add(msg); tcs1.TrySetResult(); });

        var client2 = await ConnectClient();
        client2.On<string>("Msg", msg => { lock (received2) received2.Add(msg); tcs2.TrySetResult(); });

        await Task.Delay(100);

        var exchange = new Exchange(new Message("multi"));
        exchange.In.Headers[SignalRHeaders.Method] = "Msg";
        await producer.Process(exchange);

        await Task.WhenAny(Task.WhenAll(tcs1.Task, tcs2.Task), Task.Delay(5_000));

        lock (received1) received1.Should().Contain("multi");
        lock (received2) received2.Should().Contain("multi");
    }

    // ── Target group via header ──

    [Fact]
    public async Task ServerMode_SendToGroup()
    {
        var (consumer, producer) = await CreateServerPair(defaultGroup: "room1");

        var received = new List<string>();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = await ConnectClient();
        client.On<string>("GroupMsg", msg =>
        {
            lock (received) received.Add(msg);
            tcs.TrySetResult();
        });
        await Task.Delay(200); // wait for defaultGroup assignment

        var exchange = new Exchange(new Message("group hello"));
        exchange.In.Headers[SignalRHeaders.Method] = "GroupMsg";
        exchange.In.Headers[SignalRHeaders.Target] = "Group";
        exchange.In.Headers[SignalRHeaders.Group] = "room1";
        await producer.Process(exchange);

        await Task.WhenAny(tcs.Task, Task.Delay(5_000));

        lock (received)
        {
            received.Should().Contain("group hello");
        }
    }

    // ── Server-mode producer without consumer ──

    [Fact]
    public void ServerMode_NoConsumer_Throws()
    {
        var component = new SignalRComponent();
        var pPath = $"/127.0.0.1:{_port}/hub";
        var pUri = new EndpointUri("signalr", pPath, $"signalr:{pPath}",
            new Dictionary<string, string> { ["mode"] = "Server" });
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        var producer = (SignalRProducer)pEndpoint.CreateProducer();

        var act = async () => await producer.Start();
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
