using System.Diagnostics;
using Amqp;
using Amqp.Framing;
using redb.Route.Abstractions;
using redb.Route.Amqp;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using AmqpMessage = global::Amqp.Message;
using Message = redb.Route.Core.Message;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// W3C trace context through AMQP application-properties, through the connector itself against a real broker. The
/// consumer opens a receive span per message: a child of the sender's context, or a root without one — never a child
/// of the activity the receive loop inherited from whoever started it — with the sender's baggage back on it and the
/// route's spans under it; a failed route marks it red. The producer writes the context of its own send span over a
/// copied one. <c>EnableTelemetry=false</c> opens none of these spans, while the ambient context still goes out.
/// Expects ActiveMQ Artemis at localhost:5673 (admin/admin).
/// </summary>
[Trait("Category", "Integration")]
public sealed class AmqpTracePropagationTests
{
    private const string Broker = "localhost";
    private const int Port = 5673;
    private const string User = "admin";
    private const string Password = "admin";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    private static string NewAddress() => $"trace.{Guid.NewGuid():N}";

    private static string Uri(string address) => $"amqp://{address}?host={Broker}&port={Port}&user={User}&password={Password}";

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private static RouteTelemetryProbe SpansOf(string address, ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, "messaging.destination.name") == address);

    /// <summary>Sends one message straight through AMQPNetLite, as a sender that is not redb would.</summary>
    private static async Task SendRaw(string address, IDictionary<string, string>? properties = null)
    {
        var connection = await Connection.Factory.CreateAsync(new Address(Broker, Port, User, Password, scheme: "AMQP"));
        try
        {
            var session = new Session(connection);
            var sender = new SenderLink(session, "trace-test-sender", address);
            var message = new AmqpMessage("payload");
            if (properties is not null)
            {
                message.ApplicationProperties = new ApplicationProperties();
                foreach (var (key, value) in properties)
                    message.ApplicationProperties.Map[key] = value;
            }
            await sender.SendAsync(message);
            await sender.CloseAsync();
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>Receives one message straight through AMQPNetLite.</summary>
    private static async Task<AmqpMessage> ReceiveRaw(string address)
    {
        var connection = await Connection.Factory.CreateAsync(new Address(Broker, Port, User, Password, scheme: "AMQP"));
        try
        {
            var receiver = new ReceiverLink(new Session(connection), "trace-test-receiver", address);
            var message = await receiver.ReceiveAsync(Wait)
                          ?? throw new TimeoutException($"No message arrived on {address}.");
            receiver.Accept(message);
            await receiver.CloseAsync();
            return message;
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>
    /// A context with one consuming route, started inside a host span: the receive loop inherits it, the way it inherits
    /// whatever activity the application held when it started the routes.
    /// </summary>
    private static async Task<RouteContext> StartConsumer(string address, Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new AmqpComponent());
        ctx.AddRoutes(r => r.From(Uri(address)).RouteId($"amqp-{address}").Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static async Task Arrived(TaskCompletionSource done)
        => (await Task.WhenAny(done.Task, Task.Delay(Wait))).Should().Be(done.Task, "the message should reach the route");

    [Fact]
    public async Task A_message_without_context_opens_a_root_receive_span()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        var done = new TaskCompletionSource();
        await using (await StartConsumer(address, _ => done.TrySetResult()))
        {
            await SendRaw(address);
            await Arrived(done);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.ParentSpanId.Should().Be(default(ActivitySpanId),
            "a message that carries no context starts a trace, not a child of the host's startup span");
        receive.GetTagItem("messaging.system").Should().Be("amqp");
        RouteTelemetryProbe.Tag(receive, RouteTelemetryProbe.EndpointTag).Should().StartWith("amqp://");
    }

    [Fact]
    public async Task A_message_with_context_continues_the_senders_trace_and_the_route_runs_under_it()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"amqp-{address}");
        var caller = Caller();
        var done = new TaskCompletionSource();
        await using (await StartConsumer(address, _ => done.TrySetResult()))
        {
            await SendRaw(address, new Dictionary<string, string> { ["traceparent"] = caller.Header });
            await Arrived(done);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(caller.TraceId);
        receive.ParentSpanId.Should().Be(caller.SpanId);
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(receive.SpanId);
    }

    [Fact]
    public async Task The_senders_baggage_reaches_the_route()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        var seen = new TaskCompletionSource<string?>();
        await using (await StartConsumer(address, _ => seen.TrySetResult(Activity.Current?.GetBaggageItem("tenant"))))
        {
            await SendRaw(address, new Dictionary<string, string> { ["traceparent"] = Caller().Header, ["baggage"] = "tenant=t1" });
            (await Task.WhenAny(seen.Task, Task.Delay(Wait))).Should().Be(seen.Task);
        }

        seen.Task.Result.Should().Be("t1");
    }

    [Fact]
    public async Task A_failed_route_marks_the_receive_span_red()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        var failed = new TaskCompletionSource();
        await using (await StartConsumer(address, _ =>
                     {
                         failed.TrySetResult();
                         throw new InvalidOperationException("route failed");
                     }))
        {
            await SendRaw(address);
            await Arrived(failed);
            var deadline = DateTime.UtcNow + Wait;
            while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_inside_the_route_marks_the_receive_span_red()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        var thrown = new TaskCompletionSource();
        await using (await StartConsumer(address, _ =>
                     {
                         thrown.TrySetResult();
                         throw new TaskCanceledException("a call inside the route timed out");
                     }))
        {
            await SendRaw(address);
            await Arrived(thrown);
            var deadline = DateTime.UtcNow + Wait;
            while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities[0].Status.Should().Be(ActivityStatusCode.Error, "a cancellation nobody asked for is a failure");
    }

    [Fact]
    public async Task Stopping_the_consumer_does_not_mark_the_interrupted_message_red()
    {
        var address = NewAddress();
        using var probe = SpansOf(address, ActivityKind.Consumer);
        var started = new TaskCompletionSource();
        var ctx = new RouteContext(options: new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromSeconds(2) });
        ctx.AddComponent(new AmqpComponent());
        ctx.AddRoutes(r => r.From(Uri(address)).Process(async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        await ctx.Start();
        await SendRaw(address);
        await Arrived(started);

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
        var address = NewAddress();
        using var sends = SpansOf(address, ActivityKind.Producer);
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AmqpComponent());
        await ctx.Start();
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();

        var exchange = new Exchange(new Message("payload"));
        exchange.In.Headers["traceparent"] = Caller().Header;   // what the header bridge copies from a received message
        using (var template = new ProducerTemplate(ctx))
        {
            template.Start();
            await template.SendAsync(Uri(address), exchange);
        }

        var send = sends.Activities.Should().ContainSingle().Subject;
        var received = await ReceiveRaw(address);
        received.ApplicationProperties.Map["traceparent"].Should().Be($"00-{outer.TraceId}-{send.SpanId}-01");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_context_on()
    {
        var address = NewAddress();
        using var spans = new RouteTelemetryProbe(a => RouteTelemetryProbe.Tag(a, "messaging.destination.name") == address);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        ctx.AddComponent(new AmqpComponent());
        await ctx.Start();
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();

        using (var template = new ProducerTemplate(ctx))
        {
            template.Start();
            await template.SendAsync(Uri(address), new Exchange(new Message("payload")));
        }

        var received = await ReceiveRaw(address);
        received.ApplicationProperties.Map["traceparent"].Should().Be(outer.Id, "with no send span the ambient context goes out");

        var done = new TaskCompletionSource();
        await using (await StartConsumer(address, _ => done.TrySetResult(), telemetry: false))
        {
            await SendRaw(address, new Dictionary<string, string> { ["traceparent"] = Caller().Header });
            await Arrived(done);
        }
        spans.Activities.Should().BeEmpty();
    }
}
