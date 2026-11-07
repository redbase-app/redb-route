using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Core;
using redb.Route.Definitions;
using redb.Route.Expressions;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// <c>.SpanTag(key, value, target)</c>: which span receives the attribute, what value it gets, when nothing is set,
/// and which keys are refused. Every test opens a root span of a source of its own — standing for the host's span of
/// a request — and collects only the route spans of that trace, so parallel tests do not interfere. The inbound span
/// is opened the way a connector opens it (<see cref="RouteTelemetryExtensions.StartConsumerSpan{TCarrier}"/> with
/// <see cref="InboundParent.HostRequest"/>), since no core component has one.
/// </summary>
[Collection("Telemetry")]
public sealed class SpanTagTests : IAsyncLifetime
{
    private const string HostSourceName = "redb.Route.Tests.SpanTag.Host";
    private static readonly ActivitySource Host = new(HostSourceName);

    private readonly RouteContext _context = new();
    private readonly ActivityListener _hostListener;
    private ActivityTraceId _traceId;
    private readonly RouteTelemetryProbe _probe;

    public SpanTagTests()
    {
        _hostListener = new ActivityListener
        {
            // By name, not `source == Host`: the first instance initializes Host from inside AddActivityListener,
            // and the new source asks this listener while the field is still null.
            ShouldListenTo = source => source.Name == HostSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_hostListener);
        _probe = new RouteTelemetryProbe(activity => activity.TraceId == _traceId);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        _probe.Dispose();
        _hostListener.Dispose();
    }

    // ── Enclosing ──

    [Fact]
    public async Task Enclosing_TagsTheRouteSpan()
    {
        _context.AddRoutes(r => r.From("direct://spantag-route")
            .SpanTag("sap.order.id", new HeaderExpression("orderNo"))
            .Process(_ => { }));
        await _context.Start();

        using (var host = StartHost())
            await Send("direct://spantag-route", headers: new() { ["orderNo"] = "A-42" });

        RouteSpan().GetTagItem("sap.order.id").Should().Be("A-42");
    }

    [Fact]
    public async Task Enclosing_InsideTraced_TagsTheTracedSpanOnly()
    {
        _context.AddRoutes(r => r.From("direct://spantag-traced")
            .Traced("sap-ack")
                .SpanTag("sap.order.id", "fixed")
            .EndTraced());
        await _context.Start();

        using (StartHost())
            await Send("direct://spantag-traced");

        Span("sap-ack").GetTagItem("sap.order.id").Should().Be("fixed");
        RouteSpan().GetTagItem("sap.order.id").Should().BeNull("the nearest span of the engine receives it");
    }

    [Fact]
    public async Task Value_FromATemplateOverAProperty()
    {
        _context.AddRoutes(r => r.From("direct://spantag-template")
            .SetProperty("batch", "b-7")
            .SpanTag("flow.batch", RouteBuilder.Expr("batch ${property.batch}")));
        await _context.Start();

        using (StartHost())
            await Send("direct://spantag-template");

        RouteSpan().GetTagItem("flow.batch").Should().Be("batch b-7");
    }

    [Fact]
    public async Task Value_KeepsItsPrimitiveType()
    {
        _context.AddRoutes(r => r.From("direct://spantag-typed")
            .SpanTag("order.lines", new HeaderExpression("lines")));
        await _context.Start();

        using (StartHost())
            await Send("direct://spantag-typed", headers: new() { ["lines"] = 3 });

        RouteSpan().GetTagItem("order.lines").Should().Be(3);
    }

    [Fact]
    public async Task Value_NullOrEmpty_SetsNothing()
    {
        _context.AddRoutes(r => r.From("direct://spantag-empty")
            .SpanTag("order.missing", new HeaderExpression("absent"))
            .SpanTag("order.empty", new HeaderExpression("blank")));
        await _context.Start();

        using (StartHost())
            await Send("direct://spantag-empty", headers: new() { ["blank"] = "" });

        var span = RouteSpan();
        span.GetTagItem("order.missing").Should().BeNull();
        span.GetTagItem("order.empty").Should().BeNull();
    }

    [Fact]
    public async Task Value_ThatFailsToEvaluate_SetsNothing_AndTheExchangeSucceeds()
    {
        _context.AddRoutes(r => r.From("direct://spantag-failing")
            .SpanTag("order.id", new ThrowingExpression())
            .SetHeader("after", true));
        await _context.Start();

        IExchange exchange;
        using (StartHost())
            exchange = await Send("direct://spantag-failing");

        exchange.Exception.Should().BeNull("telemetry does not change the outcome of the message");
        exchange.In.Headers.Should().ContainKey("after");
        RouteSpan().GetTagItem("order.id").Should().BeNull();
    }

    [Fact]
    public async Task RouteTracingOff_SetsNothing_NotEvenOnTheTransportSpan_AndDoesNotEvaluate()
    {
        var evaluations = new CountingExpression("v");
        _context.AddRoutes(r => r.From("direct://spantag-untraced")
            .Tracing(false)
            .SpanTag("order.id", evaluations));
        await _context.Start();

        using (StartHost())
        using (var inbound = StartInbound())
            await Send("direct://spantag-untraced");

        evaluations.Count.Should().Be(0, "without a span to write to, the value is not evaluated");
        Span("orders receive").GetTagItem("order.id").Should().BeNull("the transport span is not the enclosing one");
    }

    // ── Inbound ──

    [Fact]
    public async Task Inbound_TagsTheTransportSpan_NotTheRouteNorTheHost()
    {
        _context.AddRoutes(r => r.From("direct://spantag-inbound")
            .SpanTag("sap.order.id", new HeaderExpression("orderNo"), SpanTarget.Inbound));
        await _context.Start();

        Activity host;
        using (host = StartHost())
        using (StartInbound())
            await Send("direct://spantag-inbound", headers: new() { ["orderNo"] = "A-1" });

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("A-1");
        RouteSpan().GetTagItem("sap.order.id").Should().BeNull();
        host.GetTagItem("sap.order.id").Should().BeNull("the host's span belongs to the host");
    }

    [Fact]
    public async Task Inbound_InARouteCalledThroughDirect_IsTheCallersInboundSpan()
    {
        _context.AddRoutes(r => r.From("direct://spantag-caller").To("direct://spantag-callee"));
        _context.AddRoutes(r => r.From("direct://spantag-callee")
            .SpanTag("sap.order.id", "from-callee", SpanTarget.Inbound));
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-caller");

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("from-callee");
    }

    [Fact]
    public async Task Inbound_WithoutATransportSpan_SetsNothing()
    {
        _context.AddRoutes(r => r.From("direct://spantag-noinbound")
            .SpanTag("sap.order.id", "x", SpanTarget.Inbound));
        await _context.Start();

        using (StartHost())
            await Send("direct://spantag-noinbound");

        _probe.Activities.Should().NotContain(a => a.GetTagItem("sap.order.id") != null);
    }

    [Fact]
    public async Task Inbound_FromADetachedBranch_AfterTheRequestEnded_SetsNothing()
    {
        var requestEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tapDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.AddRoutes(r => r.From("direct://spantag-main").WireTap("direct://spantag-tap"));
        _context.AddRoutes(r => r.From("direct://spantag-tap")
            .Process(async (_, ct) => await requestEnded.Task.WaitAsync(ct))
            .SpanTag("sap.order.id", "late", SpanTarget.Inbound)
            .Process(_ => tapDone.TrySetResult()));
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-main");
        requestEnded.SetResult();
        await tapDone.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Span("orders receive").GetTagItem("sap.order.id").Should().BeNull("the span had ended when the branch ran");
    }

    // ── In an OnException body ──

    [Fact]
    public async Task InABuildersOnException_Inbound_TagsTheTransportSpanOfTheFailedRequest()
    {
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled(true)
                .SpanTag("sap.order.id", new PropertyExpression("orderNo"), SpanTarget.Inbound);
            r.From("direct://spantag-failed")
                .Process(e =>
                {
                    e.Properties["orderNo"] = "A-500";
                    throw new InvalidOperationException("boom");
                });
        });
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-failed");

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("A-500",
            "a trace that ended in a failure is found by the business key too");
    }

    [Fact]
    public async Task InARoutesOnException_Inbound_TagsTheTransportSpan()
    {
        _context.AddRoutes(r => r.From("direct://spantag-failed-route")
            .OnException<InvalidOperationException>().Handled(true)
                .SpanTag("sap.order.id", new PropertyExpression("orderNo"), SpanTarget.Inbound)
            .End()
            .Process(e =>
            {
                e.Properties["orderNo"] = "A-501";
                throw new InvalidOperationException("boom");
            }));
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-failed-route");

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("A-501");
    }

    [Fact]
    public async Task InAnOnException_Enclosing_TagsTheRouteSpan()
    {
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled(true)
                .SpanTag("sap.order.id", new PropertyExpression("orderNo"));
            r.From("direct://spantag-failed-enclosing")
                .Process(e =>
                {
                    e.Properties["orderNo"] = "A-502";
                    throw new InvalidOperationException("boom");
                });
        });
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-failed-enclosing");

        RouteSpan().GetTagItem("sap.order.id").Should().Be("A-502");
    }

    [Fact]
    public async Task InTheCallersOnException_AFailureOfARouteCalledThroughDirect_TagsTheInboundSpan()
    {
        _context.AddRoutes(r =>
        {
            r.OnException<InvalidOperationException>().Handled(true)
                .SpanTag("sap.order.id", new PropertyExpression("orderNo"), SpanTarget.Inbound);
            r.From("direct://spantag-failed-caller").To("direct://spantag-failed-callee");
        });
        _context.AddRoutes(r => r.From("direct://spantag-failed-callee")
            .Process(e =>
            {
                e.Properties["orderNo"] = "A-503";
                throw new InvalidOperationException("boom");
            }));
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-failed-caller");

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("A-503");
    }

    [Fact]
    public async Task InARouteConfigurationsOnException_Inbound_TagsTheTransportSpan()
    {
        // Handlers declared apart from the routes: the handler's steps are compiled into the failed route, so the
        // inbound span of its request is theirs to tag.
        _context.AddRouteConfiguration(c => c.OnException<InvalidOperationException>().Handled(true)
            .SpanTag("sap.order.id", new PropertyExpression("orderNo"), SpanTarget.Inbound));
        _context.AddRoutes(r => r.From("direct://spantag-failed-configuration")
            .Process(e =>
            {
                e.Properties["orderNo"] = "A-504";
                throw new InvalidOperationException("boom");
            }));
        await _context.Start();

        using (StartHost())
        using (StartInbound())
            await Send("direct://spantag-failed-configuration");

        Span("orders receive").GetTagItem("sap.order.id").Should().Be("A-504");
    }

    // ── Keys and literals ──

    [Theory]
    [InlineData("redb.route.id")]
    [InlineData("http.request.method")]
    [InlineData("messaging.destination.name")]
    [InlineData("exception.message")]
    [InlineData("sap.api.token")]
    [InlineData("order.password")]
    [InlineData("${header.key}")]
    [InlineData("Order.Id")]
    [InlineData("order..id")]
    [InlineData("order id")]
    public void Key_IsRefusedWhenTheRouteIsBuilt(string key)
    {
        var act = () => new RouteDefinition().From("direct://spantag-key").SpanTag(key, "v");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Literal_Empty_IsRefused()
    {
        var act = () => new RouteDefinition().From("direct://spantag-literal").SpanTag("order.id", "");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Literal_IsNotATemplate()
    {
        var definition = new RouteDefinition();
        definition.From("direct://spantag-literal-template").SpanTag("order.text", "${header.x}");

        definition.Outputs.OfType<SpanTagDefinition>().Single().Value.Should().BeOfType<ConstantExpression>();
    }

    // ── Helpers ──

    private Activity StartHost()
    {
        var host = Host.StartActivity("host-request", ActivityKind.Server)
            ?? throw new InvalidOperationException("The test's own listener must sample the host span.");
        _traceId = host.TraceId;
        return host;
    }

    // The span a consumer opens around the route; HostRequest keeps the host span as its parent.
    private TransportSpan StartInbound()
        => RouteTelemetryExtensions.StartConsumerSpan<object?>(
            _context, "orders receive", ActivityKind.Consumer, "messaging.system", "test", "test://orders",
            null, static (_, _) => null, InboundParent.HostRequest);

    private async Task<IExchange> Send(string uri, Dictionary<string, object?>? headers = null)
    {
        var producer = _context.GetEndpoint(uri).CreateProducer();
        await producer.Start();
        var message = new Message("body");
        foreach (var (key, value) in headers ?? [])
            message.Headers[key] = value;
        var exchange = new Exchange(message);
        await producer.Process(exchange);
        return exchange;
    }

    private Activity RouteSpan() => _probe.Activities.Single(a => a.OperationName.StartsWith("route:", StringComparison.Ordinal));

    private Activity Span(string name) => _probe.Activities.Single(a => a.OperationName == name);

    private sealed class ThrowingExpression : Expression
    {
        public override T Evaluate<T>(IExchange exchange) => throw new FormatException("not a number");

        public override void SetValue(IExchange exchange, object value) => throw new NotSupportedException();
    }

    private sealed class CountingExpression(object value) : Expression
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override T Evaluate<T>(IExchange exchange)
        {
            Interlocked.Increment(ref _count);
            return (T)value;
        }

        public override void SetValue(IExchange exchange, object value) => throw new NotSupportedException();
    }
}
