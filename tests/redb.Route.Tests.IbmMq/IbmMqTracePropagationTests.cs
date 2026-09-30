// Integration tests run on a single TFM: every TFM uses the same DEV queues, and a second run would take the messages.
#if NET9_0

using System.Diagnostics;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.IbmMq;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.IbmMq;

/// <summary>
/// W3C trace context through the MQ message properties, on both receive paths (poll and XMS listener), through the
/// connector against a real queue manager. The consumer opens a receive span per message: a child of the sender's
/// context, or a root without one — never a child of the activity the receive loop inherited from whoever started it,
/// and a message without properties (targetClient=Mq) reads as one without context — with the sender's baggage back on
/// it and the route's spans under it; a failed route marks it red. The producer writes the context of its own send span
/// over a copied one. <c>EnableTelemetry=false</c> opens none of these spans, while the ambient context still goes out.
/// Expects IBM MQ at localhost:1414, QM1, channel DEV.APP.SVRCONN, user app/admin.
/// </summary>
[Trait("Category", "Integration")]
[Collection("IbmMqIntegration")]
public sealed class IbmMqTracePropagationTests
{
    private const string Queue = "DEV.QUEUE.5";
    private const string Connection =
        "host=localhost&port=1414&channel=DEV.APP.SVRCONN&queueManager=QM1&user=app&password=admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    public static TheoryData<string> ReceiveModes => new() { "poll", "listener" };

    private static string Uri(string? extra = null) => $"wmq:{Queue}?{Connection}{(extra is null ? "" : "&" + extra)}";

    private static string ConsumerUri(string mode)
        => Uri(mode == "listener" ? "receiveMode=listener&waitInterval=200" : "waitInterval=200");

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private static RouteTelemetryProbe SpansOf(ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, "messaging.destination.name") == Queue);

    private static string Tag() => $"trace-{Guid.NewGuid():N}";

    /// <summary>Empties the queue so the test's message is the only one its consumer sees.</summary>
    private static async Task Drain()
    {
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        ctx.AddComponent(new IbmMqComponent());
        ctx.AddRoutes(r => r.From(Uri("waitInterval=200")).Process(_ => { }));
        await ctx.Start();
        await Task.Delay(1000);
    }

    /// <summary>Sends <paramref name="body"/> through a context of its own, under <paramref name="parent"/> or none.</summary>
    private static async Task Send(string body, string? extra = null, bool telemetry = true,
        IDictionary<string, object>? headers = null)
    {
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new IbmMqComponent());
        await ctx.Start();
        var exchange = new Exchange(new Message(body));
        foreach (var (key, value) in headers ?? new Dictionary<string, object>())
            exchange.In.Headers[key] = value;
        using var template = new ProducerTemplate(ctx);
        template.Start();
        await template.SendAsync(Uri(extra), exchange);
    }

    /// <summary>
    /// A context with one consuming route, started inside a host span: the receive loop inherits it, the way it inherits
    /// whatever activity the application held when it started the routes.
    /// </summary>
    private static async Task<RouteContext> StartConsumer(string mode, string tag, Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new IbmMqComponent());
        ctx.AddRoutes(r => r.From(ConsumerUri(mode)).RouteId($"wmq-{tag}")
            .Process(e => { if (e.In.Body?.ToString() == tag) step(e); }));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Arrived(Task task)
        => (await Task.WhenAny(task, Task.Delay(Wait))).Should().Be(task, "the message should reach the route");

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task A_message_without_context_opens_a_root_receive_span(string mode)
    {
        await Drain();
        var tag = Tag();
        using var probe = SpansOf(ActivityKind.Consumer);
        var done = new TaskCompletionSource();
        await using (await StartConsumer(mode, tag, _ => done.TrySetResult()))
        {
            await Send(tag, "targetClient=Mq", telemetry: false);   // raw MQMD, no properties at all
            await Arrived(done.Task);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.ParentSpanId.Should().Be(default(ActivitySpanId),
            "a message that carries no context starts a trace, not a child of the host's startup span");
        receive.GetTagItem("messaging.system").Should().Be("wmq");
        RouteTelemetryProbe.Tag(receive, RouteTelemetryProbe.EndpointTag).Should().StartWith("wmq:");
    }

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task A_message_with_context_continues_the_senders_trace_and_the_route_runs_under_it(string mode)
    {
        await Drain();
        var tag = Tag();
        using var receives = SpansOf(ActivityKind.Consumer);
        using var sends = SpansOf(ActivityKind.Producer);
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"wmq-{tag}");
        var done = new TaskCompletionSource();
        await using (await StartConsumer(mode, tag, _ => done.TrySetResult()))
        {
            await Send(tag);
            await Arrived(done.Task);
        }

        var send = sends.Activities.Should().ContainSingle().Subject;
        var receive = receives.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(send.TraceId);
        receive.ParentSpanId.Should().Be(send.SpanId);
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(receive.SpanId);
    }

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task The_senders_baggage_reaches_the_route(string mode)
    {
        await Drain();
        var tag = Tag();
        using var probe = SpansOf(ActivityKind.Consumer);
        var seen = new TaskCompletionSource<string?>();
        await using (await StartConsumer(mode, tag, _ => seen.TrySetResult(Activity.Current?.GetBaggageItem("tenant"))))
        {
            using (var caller = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                caller.AddBaggage("tenant", "t1");
                await Send(tag);
            }
            await Arrived(seen.Task);
        }

        seen.Task.Result.Should().Be("t1");
    }

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task A_failed_route_marks_the_receive_span_red(string mode)
    {
        await Drain();
        var tag = Tag();
        using var probe = SpansOf(ActivityKind.Consumer);
        var failed = new TaskCompletionSource();
        await using (await StartConsumer(mode, tag, _ =>
                     {
                         failed.TrySetResult();
                         throw new InvalidOperationException("route failed");
                     }))
        {
            await Send(tag, telemetry: false);
            await Arrived(failed.Task);
            var deadline = DateTime.UtcNow + Wait;
            while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task A_timeout_inside_the_route_marks_the_receive_span_red(string mode)
    {
        await Drain();
        var tag = Tag();
        using var probe = SpansOf(ActivityKind.Consumer);
        var thrown = new TaskCompletionSource();
        await using (await StartConsumer(mode, tag, _ =>
                     {
                         thrown.TrySetResult();
                         throw new TaskCanceledException("a call inside the route timed out");
                     }))
        {
            await Send(tag, telemetry: false);
            await Arrived(thrown.Task);
            var deadline = DateTime.UtcNow + Wait;
            while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities[0].Status.Should().Be(ActivityStatusCode.Error, "a cancellation nobody asked for is a failure");
    }

    [Theory]
    [MemberData(nameof(ReceiveModes))]
    public async Task Stopping_the_consumer_does_not_mark_the_interrupted_message_red(string mode)
    {
        await Drain();
        var tag = Tag();
        using var probe = SpansOf(ActivityKind.Consumer);
        var started = new TaskCompletionSource();
        var ctx = new RouteContext(options: new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromSeconds(2) });
        ctx.AddComponent(new IbmMqComponent());
        ctx.AddRoutes(r => r.From(ConsumerUri(mode)).Process(async (e, ct) =>
        {
            if (e.In.Body?.ToString() != tag) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        await ctx.Start();
        await Send(tag, telemetry: false);
        await Arrived(started.Task);

        await ctx.DisposeAsync();   // the drain gives up and cancels the processing token

        var deadline = DateTime.UtcNow + Wait;
        while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status != ActivityStatusCode.Error, "our own stop is not a failure");
    }

    [Fact]
    public async Task The_producer_sends_the_context_of_its_own_span_over_a_copied_one()
    {
        await Drain();
        var tag = Tag();
        using var sends = SpansOf(ActivityKind.Producer);
        using var receives = SpansOf(ActivityKind.Consumer);
        var done = new TaskCompletionSource();
        await using (await StartConsumer("poll", tag, _ => done.TrySetResult()))
        {
            using (new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                // What the header bridge copies from a received message. It travels on in the header catalogue; the
                // trace context is the traceparent property, and that one names this send.
                await Send(tag, headers: new Dictionary<string, object> { ["traceparent"] = Caller().Header });
            }
            await Arrived(done.Task);
        }

        var send = sends.Activities.Should().ContainSingle().Subject;
        var receive = receives.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(send.TraceId);
        receive.ParentSpanId.Should().Be(send.SpanId, "the next hop is a child of the send, not of the copied header");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_context_on()
    {
        await Drain();
        var tag = Tag();
        using var sends = SpansOf(ActivityKind.Producer);
        using var receives = SpansOf(ActivityKind.Consumer);
        var done = new TaskCompletionSource();
        ActivityTraceId outerTrace;
        ActivitySpanId outerSpan;
        await using (await StartConsumer("poll", tag, _ => done.TrySetResult()))
        {
            using (var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
            {
                (outerTrace, outerSpan) = (outer.TraceId, outer.SpanId);
                await Send(tag, telemetry: false);
            }
            await Arrived(done.Task);
        }
        sends.Activities.Should().BeEmpty("tracing is off for the sending context");
        var receive = receives.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(outerTrace, "with no send span the ambient context goes out");
        receive.ParentSpanId.Should().Be(outerSpan);

        var offTag = Tag();
        var offDone = new TaskCompletionSource();
        await using (await StartConsumer("poll", offTag, _ => offDone.TrySetResult(), telemetry: false))
        {
            await Send(offTag, telemetry: false);
            await Arrived(offDone.Task);
        }
        receives.Activities.Should().ContainSingle("the context with tracing off opens no receive span");
    }
}

#endif
