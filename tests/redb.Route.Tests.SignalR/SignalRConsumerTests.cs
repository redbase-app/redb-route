using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.SignalR;

namespace redb.Route.Tests.SignalR;

/// <summary>
/// Tests for SignalRConsumer. Starts a real embedded SignalR hub (Kestrel-based)
/// and connects to it with Microsoft.AspNetCore.SignalR.Client.HubConnection.
/// </summary>
public class SignalRConsumerTests : IAsyncLifetime
{
    private SignalRConsumer? _consumer;
    private int _port;

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
        if (_consumer is not null)
            await _consumer.Stop();
    }

    private SignalRConsumer CreateConsumer(bool inOut = false, string? method = null,
        string? defaultGroup = null, string hubPath = "/hub")
    {
        var parameters = new Dictionary<string, string>();
        if (inOut) parameters["inOut"] = "true";
        if (method is not null) parameters["method"] = method;
        if (defaultGroup is not null) parameters["defaultGroup"] = defaultGroup;

        var component = new SignalRComponent();
        var path = $"/127.0.0.1:{_port}{hubPath}";
        var uri = new EndpointUri("signalr", path, $"signalr:{path}", parameters);
        var endpoint = (SignalREndpoint)component.CreateEndpoint(uri);

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

        _consumer = new SignalRConsumer(endpoint, processor, endpoint.EndpointOptions);
        return _consumer;
    }

    private static async Task<HubConnection> ConnectClient(
        int port, string hubPath = "/hub")
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:{port}{hubPath}")
            .Build();

        await connection.StartAsync();
        return connection;
    }

    // ── Basic reception ──

    [Fact]
    public async Task Consumer_ReceivesInvocation()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "hello signalr" });
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("hello signalr");
    }

    [Fact]
    public async Task Consumer_ReceivesMultipleArgs()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "user1", "hello" });
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        var args = _lastExchange!.In.Body as object?[];
        args.Should().NotBeNull();
        args.Should().HaveCount(2);
    }

    [Fact]
    public async Task Consumer_MultipleInvocations_AllProcessed()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        for (var i = 0; i < 5; i++)
            await client.InvokeAsync("Invoke", "Send", new object?[] { $"msg-{i}" });

        await Task.Delay(300);

        // Filter out Connected event exchange
        int messageCount;
        lock (_capturedExchanges)
        {
            messageCount = _capturedExchanges.Count(ex =>
                !ex.In.Headers.ContainsKey(SignalRHeaders.Event));
        }
        messageCount.Should().Be(5);
        // ProcessedCount includes Connected event
        consumer.ProcessedCount.Should().BeGreaterThanOrEqualTo(5);
    }

    // ── Headers ──

    [Fact]
    public async Task Consumer_SetsMethodHeader()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "SendMessage", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange!.In.Headers[SignalRHeaders.Method].Should().Be("SendMessage");
    }

    [Fact]
    public async Task Consumer_SetsConnectionIdHeader()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange!.In.Headers[SignalRHeaders.ConnectionId].Should().NotBeNull();
        ((string)_lastExchange.In.Headers[SignalRHeaders.ConnectionId]!).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Consumer_SetsHubPathHeader()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange!.In.Headers[SignalRHeaders.HubPath].Should().Be("/hub");
    }

    [Fact]
    public async Task Consumer_SetsSslHeader()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange!.In.Headers[SignalRHeaders.Ssl].Should().Be("False");
    }

    [Fact]
    public async Task Consumer_SetsProtocolHeader()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange!.In.Headers[SignalRHeaders.Protocol].Should().Be("json");
    }

    // ── ExchangePattern ──

    [Fact]
    public async Task Consumer_InOnly_PatternIsInOnly()
    {
        var consumer = CreateConsumer(inOut: false);
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", new object?[] { "fire-and-forget" });
        await Task.Delay(200);

        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOnly);
    }

    [Fact]
    public async Task Consumer_InOut_PatternIsInOut()
    {
        _processorAction = ex =>
        {
            ex.Out = new Message("reply");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(inOut: true);
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        var result = await client.InvokeAsync<object?>("Invoke", "Send", new object?[] { "request" });

        _lastExchange!.Pattern.Should().Be(ExchangePattern.InOut);
    }

    // ── InOut response ──

    [Fact]
    public async Task Consumer_InOut_ReturnsOutBody()
    {
        _processorAction = ex =>
        {
            var input = ex.In.Body?.ToString();
            ex.Out = new Message($"echo: {input}");
            return Task.CompletedTask;
        };

        var consumer = CreateConsumer(inOut: true);
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        var result = await client.InvokeAsync<object?>("Invoke", "Send", new object?[] { "hello" });

        result.Should().NotBeNull();
        result!.ToString().Should().Be("echo: hello");
    }

    // ── Method filter ──

    [Fact]
    public async Task Consumer_MethodFilter_RejectsNonMatching()
    {
        var consumer = CreateConsumer(method: "AllowedMethod");
        await consumer.Start();

        await using var client = await ConnectClient(_port);

        var act = async () => await client.InvokeAsync("Invoke", "ForbiddenMethod", new object?[] { "test" });
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Consumer_MethodFilter_AcceptsMatching()
    {
        var consumer = CreateConsumer(method: "AllowedMethod");
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "AllowedMethod", new object?[] { "test" });
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().Be("test");
    }

    // ── Connected/Disconnected events ──

    [Fact]
    public async Task Consumer_OnConnected_FiresEvent()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await Task.Delay(300);

        lock (_capturedExchanges)
        {
            _capturedExchanges.Should().Contain(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Event)
                && (string)ex.In.Headers[SignalRHeaders.Event]! == "Connected");
        }
    }

    [Fact]
    public async Task Consumer_OnDisconnected_FiresEvent()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        var client = await ConnectClient(_port);
        await Task.Delay(100);
        await client.StopAsync();
        await client.DisposeAsync();
        await Task.Delay(300);

        lock (_capturedExchanges)
        {
            _capturedExchanges.Should().Contain(ex =>
                ex.In.Headers.ContainsKey(SignalRHeaders.Event)
                && (string)ex.In.Headers[SignalRHeaders.Event]! == "Disconnected");
        }
    }

    // ── BaseUrl ──

    [Fact]
    public async Task Consumer_BaseUrl_IsSet()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.BaseUrl.Should().NotBeNull();
        consumer.BaseUrl.Should().Contain(_port.ToString());
    }

    // ── ProcessedCount ──

    [Fact]
    public async Task Consumer_ProcessedCount_Increments()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        consumer.ProcessedCount.Should().Be(0);

        await using var client = await ConnectClient(_port);
        // Connected event fires first
        await Task.Delay(200);
        var countAfterConnect = consumer.ProcessedCount;
        countAfterConnect.Should().BeGreaterThanOrEqualTo(1); // at least Connected event

        await client.InvokeAsync("Invoke", "Send", new object?[] { "one" });
        await Task.Delay(200);
        consumer.ProcessedCount.Should().BeGreaterThan(countAfterConnect);
    }

    // ── Processor exception ──

    [Fact]
    public async Task Consumer_ProcessorThrows_ReturnsHubException()
    {
        _processorAction = _ => throw new InvalidOperationException("boom");

        var consumer = CreateConsumer(inOut: true);
        await consumer.Start();

        await using var client = await ConnectClient(_port);

        var act = async () => await client.InvokeAsync<object?>("Invoke", "Send", new object?[] { "error test" });
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*boom*");
    }

    // ── Null body ──

    [Fact]
    public async Task Consumer_NullArgs_BodyIsNull()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await client.InvokeAsync("Invoke", "Send", (object?[]?)null);
        await Task.Delay(200);

        _lastExchange.Should().NotBeNull();
        _lastExchange!.In.Body.Should().BeNull();
    }

    // ── Stop gracefully ──

    [Fact]
    public async Task Consumer_Stop_Succeeds()
    {
        var consumer = CreateConsumer();
        await consumer.Start();

        await using var client = await ConnectClient(_port);
        await Task.Delay(100);

        await consumer.Stop();
        _consumer = null; // prevent double-stop
    }
}
