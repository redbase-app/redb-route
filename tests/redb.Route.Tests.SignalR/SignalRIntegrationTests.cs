using System.Net;
using System.Net.Sockets;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// End-to-end integration tests: SignalRProducer (client mode) → SignalRConsumer (hub).
/// Real SignalR connections over loopback.
/// </summary>
public class SignalRIntegrationTests : IAsyncLifetime
{
    private int _port;
    private SignalRConsumer? _consumer;
    private SignalRProducer? _producer;

    private IExchange? _lastExchange;
    private readonly List<IExchange> _capturedExchanges = [];
    private Func<IExchange, Task>? _processorAction;

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
        if (_producer is not null) await _producer.Stop();
        if (_consumer is not null) await _consumer.Stop();
    }

    private async Task<(SignalRConsumer consumer, SignalRProducer producer)> CreatePair(
        bool inOut = false, string? method = null)
    {
        var consumerParams = new Dictionary<string, string>();
        if (inOut) consumerParams["inOut"] = "true";
        if (method is not null) consumerParams["method"] = method;

        var component = new SignalRComponent();

        // Consumer
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

        // Producer (client mode)
        var producerParams = new Dictionary<string, string> { ["mode"] = "Client" };
        if (inOut) producerParams["inOut"] = "true";
        if (method is not null) producerParams["method"] = method;

        var pPath = $"/127.0.0.1:{_port}/hub";
        var pUri = new EndpointUri("signalr", pPath, $"signalr:{pPath}", producerParams);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _producer = (SignalRProducer)pEndpoint.CreateProducer();
        await _producer.Start();

        // Wait for connection to be fully established
        await Task.Delay(200);

        return (_consumer, _producer);
    }

    // ── InOnly (fire-and-forget via SendAsync) ──

    [Fact]
    public async Task InOnly_ProducerToConsumer_MessageDelivered()
    {
        var (consumer, producer) = await CreatePair(inOut: false, method: "Send");

        var exchange = new Exchange(new Message("integration hello"));
        await producer.Process(exchange);
        await Task.Delay(300);

        // Filter out Connected event, look for the actual message
        IExchange? msgExchange;
        lock (_capturedExchanges)
        {
            msgExchange = _capturedExchanges.FirstOrDefault(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Method)
                && (string)ex.In.Headers[SignalRHeaders.Method]! == "Send"
                && !ex.In.Headers.ContainsKey(SignalRHeaders.Event));
        }

        msgExchange.Should().NotBeNull();
        msgExchange!.In.Body.Should().Be("integration hello");
    }

    [Fact]
    public async Task InOnly_MultipleMessages_AllDelivered()
    {
        var (consumer, producer) = await CreatePair(inOut: false, method: "Send");

        for (var i = 0; i < 10; i++)
            await producer.Process(new Exchange(new Message($"batch-{i}")));

        await Task.Delay(500);

        int messageCount;
        lock (_capturedExchanges)
        {
            messageCount = _capturedExchanges.Count(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Method)
                && (string)ex.In.Headers[SignalRHeaders.Method]! == "Send"
                && !ex.In.Headers.ContainsKey(SignalRHeaders.Event));
        }

        messageCount.Should().Be(10);
    }

    // ── InOut (request-response via InvokeAsync) ──

    [Fact]
    public async Task InOut_RequestResponse()
    {
        _processorAction = ex =>
        {
            var input = ex.In.Body?.ToString();
            ex.Out = new Message($"ECHO: {input}");
            return Task.CompletedTask;
        };

        var (consumer, producer) = await CreatePair(inOut: true, method: "Echo");

        var exchange = new Exchange(new Message("hello world"));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body.Should().NotBeNull();
        exchange.Out.Body!.ToString().Should().Be("ECHO: hello world");
    }

    [Fact]
    public async Task InOut_MultipleArgs()
    {
        _processorAction = ex =>
        {
            var args = ex.In.Body as object?[];
            if (args is { Length: >= 2 })
                ex.Out = new Message($"{args[0]}+{args[1]}");
            else
                ex.Out = new Message("no args");
            return Task.CompletedTask;
        };

        var (consumer, producer) = await CreatePair(inOut: true, method: "Combine");

        var exchange = new Exchange(new Message(new object?[] { "A", "B" }));
        await producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Be("A+B");
    }

    [Fact]
    public async Task InOut_NullBody()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message(ex.In.Body is null ? "was null" : "not null");
            return Task.CompletedTask;
        };

        var (consumer, producer) = await CreatePair(inOut: true, method: "Check");

        var exchange = new Exchange(new Message(null));
        await producer.Process(exchange);

        exchange.Out!.Body!.ToString().Should().Be("was null");
    }

    // ── Headers ──

    [Fact]
    public async Task Integration_ConsumerSetsHeaders()
    {
        var (consumer, producer) = await CreatePair(inOut: true, method: "Test");

        _processorAction = ex =>
        {
            ex.Out = new Message("ok");
            return Task.CompletedTask;
        };

        var exchange = new Exchange(new Message("hdr test"));
        await producer.Process(exchange);

        // Check the exchange that was received on the consumer side
        IExchange? msgExchange;
        lock (_capturedExchanges)
        {
            msgExchange = _capturedExchanges.LastOrDefault(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Method)
                && (string)ex.In.Headers[SignalRHeaders.Method]! == "Test");
        }

        msgExchange.Should().NotBeNull();
        var h = msgExchange!.In.Headers;
        h[SignalRHeaders.Method].Should().Be("Test");
        h[SignalRHeaders.ConnectionId].Should().NotBeNull();
        h[SignalRHeaders.HubPath].Should().Be("/hub");
        h[SignalRHeaders.Ssl].Should().Be("False");
        h[SignalRHeaders.Protocol].Should().Be("json");
    }

    // ── Expression support: method from options expression ──

    [Fact]
    public async Task InOnly_ExpressionMethod_ResolvesAtRuntime()
    {
        // Method is an expression — resolved from exchange header at runtime
        var component = new SignalRComponent();

        // Consumer
        var cPath = $"/127.0.0.1:{_port}/hub";
        var cUri = new EndpointUri("signalr", cPath, $"signalr:{cPath}", new Dictionary<string, string>());
        var cEndpoint = (SignalREndpoint)component.CreateEndpoint(cUri);

        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ex = ci.Arg<IExchange>();
                lock (_capturedExchanges) _capturedExchanges.Add(ex);
                return Task.CompletedTask;
            });

        _consumer = new SignalRConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer with expression method: ${header.hubMethod}
        var producerParams = new Dictionary<string, string>
        {
            ["mode"] = "Client",
            ["method"] = "${header.hubMethod}"
        };
        var pPath = $"/127.0.0.1:{_port}/hub";
        var pUri = new EndpointUri("signalr", pPath, $"signalr:{pPath}", producerParams);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _producer = (SignalRProducer)pEndpoint.CreateProducer();
        await _producer.Start();
        await Task.Delay(200);

        // Send message — expression resolves "hubMethod" header to "DynamicExpr"
        var msg = new Message("expression method test");
        msg.Headers["hubMethod"] = "DynamicExpr";
        var exchange = new Exchange(msg);
        await _producer.Process(exchange);
        await Task.Delay(300);

        IExchange? msgExchange;
        lock (_capturedExchanges)
        {
            msgExchange = _capturedExchanges.FirstOrDefault(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Method)
                && (string)ex.In.Headers[SignalRHeaders.Method]! == "DynamicExpr"
                && !ex.In.Headers.ContainsKey(SignalRHeaders.Event));
        }

        msgExchange.Should().NotBeNull("expression should resolve ${header.hubMethod} to 'DynamicExpr'");
        msgExchange!.In.Body.Should().Be("expression method test");
    }

    // ── Producer uses method from exchange header ──

    [Fact]
    public async Task InOnly_MethodFromExchangeHeader()
    {
        // No method in options → will read from exchange header
        var (consumer, producer) = await CreatePair(inOut: false);

        var exchange = new Exchange(new Message("dynamic method"));
        exchange.In.Headers[SignalRHeaders.Method] = "DynamicSend";
        await producer.Process(exchange);
        await Task.Delay(300);

        IExchange? msgExchange;
        lock (_capturedExchanges)
        {
            msgExchange = _capturedExchanges.FirstOrDefault(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Method)
                && (string)ex.In.Headers[SignalRHeaders.Method]! == "DynamicSend"
                && !ex.In.Headers.ContainsKey(SignalRHeaders.Event));
        }

        msgExchange.Should().NotBeNull();
        msgExchange!.In.Body.Should().Be("dynamic method");
    }

    // ── Request counts ──

    [Fact]
    public async Task Integration_RequestAndProcessedCounts()
    {
        var (consumer, producer) = await CreatePair(inOut: true, method: "Ping");

        _processorAction = ex =>
        {
            ex.Out = new Message("pong");
            return Task.CompletedTask;
        };

        for (var i = 0; i < 3; i++)
            await producer.Process(new Exchange(new Message("ping")));

        // Consumer processed at least 3 invocations + Connected event
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(3);
    }

    // ── Producer not started ──

    [Fact]
    public async Task Producer_NotStarted_Throws()
    {
        var component = new SignalRComponent();
        var pPath = $"/127.0.0.1:{_port}/hub";
        var pUri = new EndpointUri("signalr", pPath, $"signalr:{pPath}",
            new Dictionary<string, string> { ["mode"] = "Client", ["method"] = "Send" });
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        var producer = (SignalRProducer)pEndpoint.CreateProducer();

        var act = async () => await producer.Process(new Exchange(new Message("test")));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Connected event is received ──

    [Fact]
    public async Task Integration_ConnectedEventReceived()
    {
        var (consumer, producer) = await CreatePair(method: "Send");

        // By now, the producer connected, so Connected event should have been fired
        lock (_capturedExchanges)
        {
            _capturedExchanges.Should().Contain(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Event)
                && (string)ex.In.Headers[SignalRHeaders.Event]! == "Connected");
        }
    }

    // ── Direct mode (bridge=false) — calls hub method by name ──

    [Fact]
    public async Task DirectMode_CallsHubMethodByName()
    {
        // With bridge=false, producer calls the hub method directly by its real name.
        // RedbBridgeHub has method "Invoke(string method, object?[]? args)",
        // so we pass [method, args] as the body array — they become positional params.
        var consumerParams = new Dictionary<string, string> { ["inOut"] = "true" };
        var component = new SignalRComponent();

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
                if (_processorAction is not null) await _processorAction(ex);
            });

        _consumer = new SignalRConsumer(cEndpoint, processor, cEndpoint.EndpointOptions);
        await _consumer.Start();

        // Producer with bridge=false and method=Invoke (the actual hub method name)
        var producerParams = new Dictionary<string, string>
        {
            ["mode"] = "Client",
            ["method"] = "Invoke",
            ["bridge"] = "false",
            ["inOut"] = "true"
        };
        var pUri = new EndpointUri("signalr", cPath, $"signalr:{cPath}", producerParams);
        var pEndpoint = (SignalREndpoint)component.CreateEndpoint(pUri);
        _producer = (SignalRProducer)pEndpoint.CreateProducer();
        await _producer.Start();
        await Task.Delay(200);

        _processorAction = ex =>
        {
            ex.Out = new Message($"ECHO: {ex.In.Body}");
            return Task.CompletedTask;
        };

        // In direct mode, body array becomes positional args: Invoke(string method, object?[]? args)
        var exchange = new Exchange(new Message(new object?[] { "DirectTest", new object?[] { "hello direct" } }));
        await _producer.Process(exchange);

        exchange.Out.Should().NotBeNull();
        exchange.Out!.Body!.ToString().Should().Be("ECHO: hello direct");
    }
}
