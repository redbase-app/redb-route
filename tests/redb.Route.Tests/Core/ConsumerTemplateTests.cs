using FluentAssertions;
using redb.Route.Abstractions;
using redb.Route.Core;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="ConsumerTemplate"/>: lifecycle, SEDA polling (optimized path),
/// generic consumer-based polling, typed body, timeout, and no-wait semantics.
/// </summary>
public class ConsumerTemplateTests : IDisposable
{
    private readonly RouteContext _ctx = new("ct-test");
    private readonly ConsumerTemplate _consumer;
    private readonly ProducerTemplate _producer;

    public ConsumerTemplateTests()
    {
        _consumer = new ConsumerTemplate(_ctx);
        _producer = new ProducerTemplate(_ctx);
    }

    public void Dispose()
    {
        if (_producer.IsStarted) _producer.Stop();
        if (_consumer.IsStarted) _consumer.Stop();
        _producer.Dispose();
        _consumer.Dispose();
        _ctx.Dispose();
    }

    private void StartBoth()
    {
        _producer.Start();
        _consumer.Start();
    }

    /// <summary>Helper: send a body to a SEDA endpoint.</summary>
    private async Task SendToSeda(string uri, object body)
    {
        await _producer.SendAsync(uri, body);
    }

    // ── Lifecycle ──

    [Fact]
    public void IsStarted_FalseByDefault()
    {
        _consumer.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void Start_SetsIsStarted()
    {
        _consumer.Start();
        _consumer.IsStarted.Should().BeTrue();
    }

    [Fact]
    public void Start_WhenAlreadyStarted_Throws()
    {
        _consumer.Start();
        var act = () => _consumer.Start();
        act.Should().Throw<InvalidOperationException>().WithMessage("*already started*");
    }

    [Fact]
    public void Stop_ClearsIsStarted()
    {
        _consumer.Start();
        _consumer.Stop();
        _consumer.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void Stop_WhenNotStarted_Throws()
    {
        var act = () => _consumer.Stop();
        act.Should().Throw<InvalidOperationException>().WithMessage("*not started*");
    }

    [Fact]
    public async Task Receive_WhenNotStarted_Throws()
    {
        var act = () => _consumer.Receive("seda:test");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not started*");
    }

    [Fact]
    public void Context_ReturnsRouteContext()
    {
        _consumer.Context.Should().BeSameAs(_ctx);
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new ConsumerTemplate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Dispose_PreventsSubsequentCalls()
    {
        _consumer.Start();
        _consumer.Dispose();
        var act = () => _consumer.Receive("seda:test");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    // ── SEDA: Receive (blocking) ──

    [Fact]
    public async Task Receive_Seda_ReturnsExchange()
    {
        StartBoth();
        await SendToSeda("seda:queue1", "hello");

        var result = await _consumer.Receive("seda:queue1");
        result.In.Body.Should().Be("hello");
    }

    [Fact]
    public async Task Receive_Seda_FIFO_Order()
    {
        StartBoth();

        for (var i = 0; i < 5; i++)
            await SendToSeda("seda:ordered", i);

        for (var i = 0; i < 5; i++)
        {
            var result = await _consumer.Receive("seda:ordered");
            result.In.Body.Should().Be(i);
        }
    }

    [Fact]
    public async Task Receive_Seda_Endpoint_Overload()
    {
        StartBoth();

        var endpoint = _ctx.GetEndpoint("seda:ep-overload");
        await _producer.SendAsync(endpoint, "via-endpoint");

        var result = await _consumer.Receive(endpoint);
        result.In.Body.Should().Be("via-endpoint");
    }

    // ── SEDA: Receive with timeout ──

    [Fact]
    public async Task Receive_Seda_WithTimeout_ReturnsExchange()
    {
        StartBoth();
        await SendToSeda("seda:timeout1", "with-timeout");

        var result = await _consumer.Receive("seda:timeout1", TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.In.Body.Should().Be("with-timeout");
    }

    [Fact]
    public async Task Receive_Seda_Timeout_ReturnsNull()
    {
        _consumer.Start();

        // No messages in queue — should timeout
        var result = await _consumer.Receive("seda:empty", TimeSpan.FromMilliseconds(100));
        result.Should().BeNull();
    }

    [Fact]
    public async Task Receive_Seda_Timeout_Endpoint_Overload()
    {
        _consumer.Start();

        var endpoint = _ctx.GetEndpoint("seda:ep-timeout");
        var result = await _consumer.Receive(endpoint, TimeSpan.FromMilliseconds(100));
        result.Should().BeNull();
    }

    // ── SEDA: ReceiveNoWait ──

    [Fact]
    public async Task ReceiveNoWait_Seda_WithMessage_Returns()
    {
        StartBoth();
        await SendToSeda("seda:nowait1", "instant");

        var result = await _consumer.ReceiveNoWait("seda:nowait1");
        result.Should().NotBeNull();
        result!.In.Body.Should().Be("instant");
    }

    [Fact]
    public async Task ReceiveNoWait_Seda_Empty_ReturnsNull()
    {
        _consumer.Start();

        var result = await _consumer.ReceiveNoWait("seda:nowait-empty");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ReceiveNoWait_Seda_Endpoint_Overload()
    {
        _consumer.Start();

        var endpoint = _ctx.GetEndpoint("seda:ep-nowait");
        var result = await _consumer.ReceiveNoWait(endpoint);
        result.Should().BeNull();
    }

    // ── Typed body ──

    [Fact]
    public async Task ReceiveBody_ReturnsBody()
    {
        StartBoth();
        await SendToSeda("seda:body1", 42);

        var body = await _consumer.ReceiveBody("seda:body1");
        body.Should().Be(42);
    }

    [Fact]
    public async Task ReceiveBody_Typed_ReturnsTyped()
    {
        StartBoth();
        await SendToSeda("seda:typed", "typed-value");

        var body = await _consumer.ReceiveBody<string>("seda:typed");
        body.Should().Be("typed-value");
    }

    [Fact]
    public async Task ReceiveBody_Typed_Converts()
    {
        StartBoth();
        await SendToSeda("seda:convert", 42);

        var body = await _consumer.ReceiveBody<string>("seda:convert");
        body.Should().Be("42");
    }

    [Fact]
    public async Task ReceiveBody_Typed_WithTimeout_ReturnsDefault()
    {
        _consumer.Start();

        var body = await _consumer.ReceiveBody<string>("seda:nobody", TimeSpan.FromMilliseconds(100));
        body.Should().BeNull();
    }

    [Fact]
    public async Task ReceiveBody_Typed_WithTimeout_ReturnsValue()
    {
        StartBoth();
        await SendToSeda("seda:bodytimeout", "got-it");

        var body = await _consumer.ReceiveBody<string>("seda:bodytimeout", TimeSpan.FromSeconds(5));
        body.Should().Be("got-it");
    }

    // ── Cancellation ──

    [Fact]
    public async Task Receive_Seda_Cancellation_Throws()
    {
        _consumer.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var act = () => _consumer.Receive("seda:cancel", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Integration: ProducerTemplate → ConsumerTemplate via SEDA ──

    [Fact]
    public async Task ProducerToConsumer_Seda_Roundtrip()
    {
        StartBoth();

        await _producer.SendAsync("seda:roundtrip", "ping");

        var result = await _consumer.Receive("seda:roundtrip", TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.In.Body.Should().Be("ping");
    }

    [Fact]
    public async Task ProducerToConsumer_Seda_MultipleMessages()
    {
        StartBoth();

        for (var i = 0; i < 10; i++)
            await _producer.SendAsync("seda:multi", $"msg-{i}");

        for (var i = 0; i < 10; i++)
        {
            var body = await _consumer.ReceiveBody<string>("seda:multi");
            body.Should().Be($"msg-{i}");
        }
    }

    // ── Generic consumer path (Direct endpoint via bridge) ──

    [Fact]
    public async Task Receive_Direct_ViaGenericBridge()
    {
        StartBoth();

        var endpoint = _ctx.GetEndpoint("direct:poll-test");

        // Send a message on another thread after a small delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            _producer.Send(endpoint, "from-direct");
        });

        var result = await _consumer.Receive(endpoint, TimeSpan.FromSeconds(5));
        result.Should().NotBeNull();
        result!.In.Body.Should().Be("from-direct");
    }

    [Fact]
    public async Task ReceiveNoWait_Direct_ReturnsNull_WhenEmpty()
    {
        _consumer.Start();

        // Direct with no messages — generic path, zero timeout → null
        var result = await _consumer.ReceiveNoWait("direct:no-msg");
        result.Should().BeNull();
    }
}
