using System.Net;
using System.Net.Sockets;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.WebSocket;

namespace redb.Route.Tests.WebSocket;

/// <summary>
/// Integration tests validating Fix #3: WsConsumer now has InflightDrainGuard.
/// Stop() drains in-flight messages before tearing down the Kestrel server.
/// </summary>
[Trait("Category", "Integration")]
public class WsDrainTests : IAsyncLifetime
{
    private int _port;
    private WsConsumer? _consumer;
    private WsProducer? _producer;

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
        if (_producer is not null)
        {
            try { await _producer.Stop(); }
            catch { /* already stopped */ }
        }
        if (_consumer is not null)
        {
            try { await _consumer.Stop(); }
            catch { /* already stopped */ }
        }
    }

    [Fact]
    public async Task Drain_SlowProcessing_CompletesBeforeStopReturns()
    {
        var processedCount = 0;
        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                processingStarted.TrySetResult();
                await Task.Delay(400, ci.Arg<CancellationToken>());
                Interlocked.Increment(ref processedCount);
            });

        var component = new WsComponent();
        var parameters = new Dictionary<string, string>();

        var cUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var cEndpoint = (WsEndpoint)component.CreateEndpoint(cUri);
        _consumer = new WsConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        var pUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var pEndpoint = (WsEndpoint)component.CreateEndpoint(pUri);
        _producer = (WsProducer)pEndpoint.CreateProducer();
        await _producer.Start();

        // Send message — processing takes 400ms
        _ = _producer.Process(new Exchange(new Message("drain-test")));
        await processingStarted.Task;

        // Stop consumer — InflightDrainGuard should wait for processing to finish
        await _consumer.Stop();
        _consumer = null;

        processedCount.Should().Be(1,
            "in-flight message must complete before Stop returns (InflightDrainGuard)");
    }

    [Fact]
    public async Task Drain_MultipleMessages_AllCompleteBeforeStop()
    {
        var processedCount = 0;
        var allStarted = new CountdownEvent(3);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                allStarted.Signal();
                await Task.Delay(300, ci.Arg<CancellationToken>());
                Interlocked.Increment(ref processedCount);
            });

        var component = new WsComponent();
        var parameters = new Dictionary<string, string>();

        var cUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
        var cEndpoint = (WsEndpoint)component.CreateEndpoint(cUri);
        _consumer = new WsConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Send 3 messages from separate producers (each opens its own WS connection)
        var producers = new List<WsProducer>();
        for (int i = 0; i < 3; i++)
        {
            var pUri = new EndpointUri("ws", $"/127.0.0.1:{_port}/ws", $"ws:127.0.0.1:{_port}/ws", parameters);
            var pEndpoint = (WsEndpoint)component.CreateEndpoint(pUri);
            var p = (WsProducer)pEndpoint.CreateProducer();
            await p.Start();
            producers.Add(p);
            _ = p.Process(new Exchange(new Message($"msg-{i}")));
        }

        allStarted.Wait(TimeSpan.FromSeconds(10));

        // Stop consumer — should drain all 3 in-flight messages
        await _consumer.Stop();
        _consumer = null;

        processedCount.Should().Be(3,
            "all in-flight messages must complete before Stop returns");

        foreach (var p in producers)
            await p.Stop();
    }
}
