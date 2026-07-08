using System.Net;
using System.Net.Sockets;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Tcp;

namespace redb.Route.Tests.Tcp;

/// <summary>
/// Integration test validating Fix #6: TCP drain waits for full exchange lifecycle
/// (processing + InOut response + DisposeAsync) before allowing Stop to complete.
/// </summary>
[Trait("Category", "Integration")]
public class TcpDrainTests : IAsyncLifetime
{
    private int _port;
    private TcpConsumer? _consumer;

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

        var component = new TcpComponent();
        var parameters = new Dictionary<string, string> { ["framing"] = TcpFraming.TextLine.ToString() };

        var cUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var cEndpoint = (TcpEndpoint)component.CreateEndpoint(cUri);
        _consumer = new TcpConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        var pUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var pEndpoint = (TcpEndpoint)component.CreateEndpoint(pUri);
        var producer = (TcpProducer)pEndpoint.CreateProducer();
        await producer.Start();

        // Send message — processing takes 400ms
        _ = producer.Process(new Exchange(new Message("drain-test")));
        await processingStarted.Task;

        // Stop consumer immediately — drain should wait for processing to finish
        await _consumer.Stop();
        _consumer = null;

        processedCount.Should().Be(1,
            "in-flight message must complete before Stop returns (drain waits for full lifecycle)");

        await producer.Stop();
    }

    [Fact]
    public async Task Drain_InOutMode_ResponseSentWithinDrainScope()
    {
        var processingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                processingStarted.TrySetResult();
                var ex = ci.Arg<IExchange>();
                await Task.Delay(200, ci.Arg<CancellationToken>());
                ex.Out = new Message("PONG");
            });

        var component = new TcpComponent();
        var parameters = new Dictionary<string, string>
        {
            ["framing"] = TcpFraming.TextLine.ToString(),
            ["inOut"] = "true"
        };

        var cUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var cEndpoint = (TcpEndpoint)component.CreateEndpoint(cUri);
        _consumer = new TcpConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        var pUri = new EndpointUri("tcp", $"/127.0.0.1:{_port}", $"tcp:127.0.0.1:{_port}", parameters);
        var pEndpoint = (TcpEndpoint)component.CreateEndpoint(pUri);
        var producer = (TcpProducer)pEndpoint.CreateProducer();
        await producer.Start();

        // Send InOut request — response takes 200ms
        var exchange = new Exchange(new Message("PING"));
        var processTask = Task.Run(async () => await producer.Process(exchange));

        await processingStarted.Task;

        // Start stop in background — drain-safe token keeps response write alive
        var stopTask = Task.Run(async () =>
        {
            await _consumer!.Stop();
            _consumer = null;
        });

        // Both should complete — producer gets response, stop waits for drain
        await Task.WhenAll(processTask, stopTask);

        exchange.Out.Should().NotBeNull("InOut response should be received before drain completes");
        exchange.Out!.Body.Should().Be("PONG");

        await producer.Stop();
    }
}
