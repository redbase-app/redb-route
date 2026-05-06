using FluentAssertions;
using NSubstitute;
using redb.Route.Abstractions;
using redb.Route.Components;
using redb.Route.Core;
using redb.Route.Processors;

namespace redb.Route.Tests.Core;

/// <summary>
/// Tests for <see cref="ProducerTemplate"/>: lifecycle, sync/async send,
/// request/reply, processor-based send, message send.
/// Uses DirectComponent for real endpoint integration.
/// </summary>
public class ProducerTemplateTests : IDisposable
{
    private readonly RouteContext _ctx = new("pt-test");
    private readonly ProducerTemplate _template;
    private readonly List<IExchange> _receivedExchanges = [];

    public ProducerTemplateTests()
    {
        _ctx.AddComponent(new DirectComponent());
        _template = new ProducerTemplate(_ctx);
    }

    public void Dispose()
    {
        if (_template.IsStarted)
            _template.Stop();
        _ctx.Dispose();
    }

    // ── Lifecycle ──

    [Fact]
    public void IsStarted_FalseByDefault()
    {
        _template.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void Start_SetsIsStarted()
    {
        _template.Start();
        _template.IsStarted.Should().BeTrue();
    }

    [Fact]
    public void Start_WhenAlreadyStarted_Throws()
    {
        _template.Start();
        var act = () => _template.Start();
        act.Should().Throw<InvalidOperationException>().WithMessage("*already started*");
    }

    [Fact]
    public void Stop_ClearsIsStarted()
    {
        _template.Start();
        _template.Stop();
        _template.IsStarted.Should().BeFalse();
    }

    [Fact]
    public void Stop_WhenNotStarted_Throws()
    {
        var act = () => _template.Stop();
        act.Should().Throw<InvalidOperationException>().WithMessage("*not started*");
    }

    [Fact]
    public void Send_WhenNotStarted_Throws()
    {
        var act = () => _template.Send("direct:test", "body");
        act.Should().Throw<InvalidOperationException>().WithMessage("*not started*");
    }

    [Fact]
    public void Context_ReturnsRouteContext()
    {
        _template.Context.Should().BeSameAs(_ctx);
    }

    [Fact]
    public void Constructor_NullContext_Throws()
    {
        var act = () => new ProducerTemplate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Sync Send ──

    [Fact]
    public async Task Send_ByUri_DeliversBody()
    {
        await SetupConsumer("direct:orders");
        _template.Start();

        _template.Send("direct:orders", "payload");

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("payload");
    }

    [Fact]
    public async Task Send_ByEndpoint_DeliversBody()
    {
        await SetupConsumer("direct:items");
        _template.Start();

        var endpoint = _ctx.GetEndpoint("direct:items");
        _template.Send(endpoint, "item-data");

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("item-data");
    }

    [Fact]
    public async Task Send_Exchange_DeliversExistingExchange()
    {
        await SetupConsumer("direct:raw");
        _template.Start();

        var exchange = new Exchange(new Message("raw-body"));
        exchange.In.Headers["type"] = "raw";
        var endpoint = _ctx.GetEndpoint("direct:raw");

        _template.Send(endpoint, exchange);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("raw-body");
        _receivedExchanges[0].In.Headers["type"].Should().Be("raw");
    }

    [Fact]
    public async Task Send_Message_WrapsInExchange()
    {
        await SetupConsumer("direct:msg");
        _template.Start();

        var message = new Message("msg-body");
        message.Headers["h1"] = "v1";
        var endpoint = _ctx.GetEndpoint("direct:msg");

        _template.Send(endpoint, (IMessage)message);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("msg-body");
        _receivedExchanges[0].In.Headers["h1"].Should().Be("v1");
    }

    [Fact]
    public async Task Send_MessageByUri_Works()
    {
        await SetupConsumer("direct:msg2");
        _template.Start();

        var message = new Message("uri-msg");
        _template.Send("direct:msg2", (IMessage)message);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("uri-msg");
    }

    [Fact]
    public async Task Send_WithProcessor_AppliesProcessorBeforeSending()
    {
        await SetupConsumer("direct:proc");
        _template.Start();

        var processor = new DelegateProcessor(async (ex, ct) =>
        {
            ex.In.Body = "transformed";
            ex.In.Headers["processed"] = true;
        });

        var endpoint = _ctx.GetEndpoint("direct:proc");
        _template.Send(endpoint, (IProcessor)processor);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("transformed");
        _receivedExchanges[0].In.Headers["processed"].Should().Be(true);
    }

    // ── Async Send ──

    [Fact]
    public async Task SendAsync_ByEndpoint_DeliversBody()
    {
        await SetupConsumer("direct:async1");
        _template.Start();

        var endpoint = _ctx.GetEndpoint("direct:async1");
        await _template.SendAsync(endpoint, "async-body");

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("async-body");
    }

    [Fact]
    public async Task SendAsync_ByUri_DeliversBody()
    {
        await SetupConsumer("direct:async2");
        _template.Start();

        await _template.SendAsync("direct:async2", "async-by-uri");

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("async-by-uri");
    }

    [Fact]
    public async Task SendAsync_Message_Works()
    {
        await SetupConsumer("direct:amsg");
        _template.Start();

        var endpoint = _ctx.GetEndpoint("direct:amsg");
        var msg = new Message("async-msg");
        await _template.SendAsync(endpoint, (IMessage)msg);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("async-msg");
    }

    [Fact]
    public async Task SendAsync_MessageByUri_Works()
    {
        await SetupConsumer("direct:amsg2");
        _template.Start();

        var msg = new Message("async-msg-uri");
        await _template.SendAsync("direct:amsg2", (IMessage)msg);

        _receivedExchanges.Should().HaveCount(1);
        _receivedExchanges[0].In.Body.Should().Be("async-msg-uri");
    }

    // ── Request/Reply ──

    [Fact]
    public async Task RequestBody_ReturnsResponseFromOut()
    {
        await SetupConsumer("direct:echo", exchange =>
        {
            exchange.Out = new Message($"echo:{exchange.In.Body}");
        });
        _template.Start();

        var result = await _template.RequestBody("direct:echo", "hello");
        result.Should().Be("echo:hello");
    }

    [Fact]
    public async Task RequestBody_NoOut_ReturnsInBody()
    {
        await SetupConsumer("direct:passthrough");
        _template.Start();

        var result = await _template.RequestBody("direct:passthrough", "fallback");
        result.Should().Be("fallback");
    }

    [Fact]
    public async Task RequestBody_Typed_ReturnsConvertedResult()
    {
        await SetupConsumer("direct:typed", exchange =>
        {
            exchange.Out = new Message(42);
        });
        _template.Start();

        var result = await _template.RequestBody<int>("direct:typed", "ignored");
        result.Should().Be(42);
    }

    [Fact]
    public async Task RequestBody_Typed_ByUri_Works()
    {
        await SetupConsumer("direct:tbyuri", exchange =>
        {
            exchange.Out = new Message("response-value");
        });
        _template.Start();

        var result = await _template.RequestBody<string>("direct:tbyuri", "request");
        result.Should().Be("response-value");
    }

    [Fact]
    public async Task RequestBody_SetsInOutPattern()
    {
        await SetupConsumer("direct:pattern");
        _template.Start();

        var endpoint = _ctx.GetEndpoint("direct:pattern");
        await _template.RequestBody(endpoint, "check-pattern");

        _receivedExchanges[0].Pattern.Should().Be(ExchangePattern.InOut);
    }

    // ── Producer Caching ──

    [Fact]
    public async Task Send_SameEndpointTwice_ReusesProducer()
    {
        // DirectComponent.CreateEndpoint caches endpoints, and now ProducerTemplate caches producers.
        // Sending twice to the same URI should only call CreateProducer once internally.
        await SetupConsumer("direct:cache-test");
        _template.Start();

        _template.Send("direct:cache-test", "first");
        _template.Send("direct:cache-test", "second");

        _receivedExchanges.Should().HaveCount(2);
        _receivedExchanges[0].In.Body.Should().Be("first");
        _receivedExchanges[1].In.Body.Should().Be("second");
    }

    [Fact]
    public async Task SendAsync_SameEndpointTwice_ReusesProducer()
    {
        await SetupConsumer("direct:async-cache");
        _template.Start();

        var endpoint = _ctx.GetEndpoint("direct:async-cache");
        await _template.SendAsync(endpoint, "a");
        await _template.SendAsync(endpoint, "b");

        _receivedExchanges.Should().HaveCount(2);
    }

    // ── IDisposable ──

    [Fact]
    public void Dispose_PreventsSubsequentSend()
    {
        _template.Start();
        _template.Dispose();

        var act = () => _template.Send("direct:test", "body");
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_PreventsSubsequentSendAsync()
    {
        _template.Start();
        _template.Dispose();

        var act = async () => await _template.SendAsync("direct:test", "body");
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        _template.Start();
        _template.Dispose();
        var act = () => _template.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Start_AfterDispose_Throws()
    {
        _template.Dispose();
        var act = () => _template.Start();
        act.Should().Throw<ObjectDisposedException>();
    }

    // ── Helper: captures exchanges for assertion ──

    private async Task SetupConsumer(string uri, Action<IExchange>? additionalAction = null)
    {
        var endpoint = _ctx.GetEndpoint(uri);
        var processor = new DelegateProcessor(async (exchange, ct) =>
        {
            _receivedExchanges.Add(exchange);
            additionalAction?.Invoke(exchange);
        });
        var consumer = endpoint.CreateConsumer(processor);
        await consumer.Start();
    }
}
