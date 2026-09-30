// Integration tests run on a single TFM: all TFMs share the emulator's queue.1 and would take each other's messages.
#if NET9_0

using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using redb.Route.Abstractions;
using redb.Route.AzureServiceBus;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;

namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// A Service Bus message carries its trace context in the application properties, the <c>traceparent</c> value under
/// Azure's name <c>Diagnostic-Id</c>. The consumer opens a receive span per message: a child of the sender's context,
/// a root without one, with the sender's baggage back on it and the route's spans under it; a failed route marks it
/// red. The producer writes the context of its own send span, over a <c>Diagnostic-Id</c> copied from a received
/// message. <c>EnableTelemetry=false</c> opens none of these spans, while a context that came in still goes out.
/// Requires the Azure Service Bus emulator on localhost:5300 with queue.1.
/// </summary>
[Trait("Category", "Integration")]
[Collection(AsbEmulatorQueue.Name)]
public sealed class AzureServiceBusTracePropagationTests
{
    private const string ConnectionString =
        "Endpoint=sb://localhost:5300;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";
    private const string Queue = "queue.1";
    private const string DiagnosticId = "Diagnostic-Id";
    private static readonly string QueueUri = $"asb://{Queue}?connectionString={ConnectionString}";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private static RouteTelemetryProbe ReceiveSpansOf(string messageId)
        => new(a => a.Kind == ActivityKind.Consumer && RouteTelemetryProbe.Tag(a, "messaging.message.id") == messageId);

    /// <summary>Sends one message straight through the SDK, as a sender that is not redb would.</summary>
    private static async Task SendRaw(string messageId, IDictionary<string, object>? properties = null)
    {
        await using var client = new ServiceBusClient(ConnectionString);
        await using var sender = client.CreateSender(Queue);
        var message = new ServiceBusMessage(BinaryData.FromString(messageId)) { MessageId = messageId };
        foreach (var (key, value) in properties ?? new Dictionary<string, object>())
            message.ApplicationProperties[key] = value;
        await sender.SendMessageAsync(message);
    }

    /// <summary>Receives the message with <paramref name="messageId"/> straight through the SDK, completing what it reads.</summary>
    private static async Task<ServiceBusReceivedMessage> ReceiveRaw(string messageId)
    {
        await using var client = new ServiceBusClient(ConnectionString);
        await using var receiver = client.CreateReceiver(Queue);
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(2));
            if (message is null) continue;
            await receiver.CompleteMessageAsync(message);
            if (message.MessageId == messageId) return message;
        }
        throw new TimeoutException($"Message {messageId} did not arrive on {Queue}.");
    }

    /// <summary>A context with one consuming route whose step is <paramref name="step"/>.</summary>
    private static async Task<RouteContext> StartConsumer(string routeId, Action<IExchange> step, bool telemetry = true)
    {
        var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new AzureServiceBusComponent());
        ctx.AddRoutes(r => r.From(QueueUri).RouteId(routeId).Process(step));
        await ctx.Start();
        return ctx;
    }

    private static string Tag() => Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task A_message_without_context_opens_a_root_receive_span()
    {
        var id = $"root-{Tag()}";
        using var probe = ReceiveSpansOf(id);
        var done = new TaskCompletionSource();
        await using (await StartConsumer($"asb-trace-{id}", e => { if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) == id) done.TrySetResult(); }))
        {
            await SendRaw(id);
            (await Task.WhenAny(done.Task, Task.Delay(Wait))).Should().Be(done.Task);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.ParentSpanId.Should().Be(default(ActivitySpanId), "a message that carries no context starts a trace");
        receive.Source.Name.Should().Be(RouteActivitySource.SourceName);
        receive.GetTagItem("messaging.system").Should().Be("azureservicebus");
        receive.GetTagItem("messaging.operation").Should().Be("receive");
        receive.GetTagItem("messaging.destination.name").Should().Be(Queue);
        RouteTelemetryProbe.Tag(receive, RouteTelemetryProbe.EndpointTag).Should().StartWith("asb://");
    }

    [Fact]
    public async Task A_message_with_context_continues_the_senders_trace_and_the_route_runs_under_it()
    {
        var id = $"child-{Tag()}";
        var routeId = $"asb-trace-{id}";
        using var probe = ReceiveSpansOf(id);
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix(routeId);
        var caller = Caller();
        var done = new TaskCompletionSource();
        await using (await StartConsumer(routeId, e => { if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) == id) done.TrySetResult(); }))
        {
            await SendRaw(id, new Dictionary<string, object> { [DiagnosticId] = caller.Header });
            (await Task.WhenAny(done.Task, Task.Delay(Wait))).Should().Be(done.Task);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(caller.TraceId);
        receive.ParentSpanId.Should().Be(caller.SpanId);
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(receive.SpanId,
            "the route runs inside the receive");
    }

    [Fact]
    public async Task The_senders_baggage_reaches_the_route()
    {
        var id = $"bag-{Tag()}";
        using var probe = ReceiveSpansOf(id);   // a listener: without one no span opens to carry the baggage
        var caller = Caller();
        var seen = new TaskCompletionSource<string?>();
        await using (await StartConsumer($"asb-trace-{id}", e =>
                     {
                         if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) == id)
                             seen.TrySetResult(Activity.Current?.GetBaggageItem("tenant"));
                     }))
        {
            await SendRaw(id, new Dictionary<string, object> { [DiagnosticId] = caller.Header, ["baggage"] = "tenant=t1" });
            (await Task.WhenAny(seen.Task, Task.Delay(Wait))).Should().Be(seen.Task);
        }

        seen.Task.Result.Should().Be("t1");
    }

    [Fact]
    public async Task A_failed_route_marks_the_receive_span_red()
    {
        var id = $"fail-{Tag()}";
        using var probe = ReceiveSpansOf(id);
        var failed = new TaskCompletionSource();
        await using (await StartConsumer($"asb-trace-{id}", e =>
                     {
                         if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) != id) return;
                         failed.TrySetResult();
                         throw new InvalidOperationException("route failed");
                     }))
        {
            await SendRaw(id);
            (await Task.WhenAny(failed.Task, Task.Delay(Wait))).Should().Be(failed.Task);
            // The span ends after settlement; give the handler the time to get there.
            var deadline = DateTime.UtcNow + Wait;
            while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(100);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_inside_the_route_marks_the_receive_span_red()
    {
        var id = $"timeout-{Tag()}";
        using var probe = ReceiveSpansOf(id);
        var thrown = new TaskCompletionSource();
        await using (await StartConsumer($"asb-trace-{id}", e =>
                     {
                         if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) != id) return;
                         thrown.TrySetResult();
                         throw new TaskCanceledException("a call inside the route timed out");
                     }))
        {
            await SendRaw(id);
            (await Task.WhenAny(thrown.Task, Task.Delay(Wait))).Should().Be(thrown.Task);
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
        var id = $"stop-{Tag()}";
        using var probe = ReceiveSpansOf(id);
        var started = new TaskCompletionSource();
        var ctx = new RouteContext(options: new RouteEngineOptions { ShutdownTimeout = TimeSpan.FromSeconds(2) });
        ctx.AddComponent(new AzureServiceBusComponent());
        ctx.AddRoutes(r => r.From(QueueUri).RouteId($"asb-trace-{id}").Process(async (e, ct) =>
        {
            if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) != id) return;
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }));
        await ctx.Start();
        await SendRaw(id);
        (await Task.WhenAny(started.Task, Task.Delay(Wait))).Should().Be(started.Task);

        await ctx.DisposeAsync();   // the processor stops and cancels the handler's token

        var deadline = DateTime.UtcNow + Wait;
        while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status != ActivityStatusCode.Error, "our own stop is not a failure");
    }

    [Fact]
    public async Task The_producer_sends_the_context_of_its_own_span_over_a_copied_one()
    {
        var id = $"send-{Tag()}";
        var stale = Caller();
        await using var ctx = new RouteContext();
        ctx.AddComponent(new AzureServiceBusComponent());
        await ctx.Start();
        using var sends = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Producer
                                                       && a.GetTagItem("messaging.destination.name") as string == Queue);
        using var outer = new Activity("caller").Start();

        var exchange = new Exchange(new Message(id));
        exchange.In.Headers[AzureServiceBusHeaders.MessageId] = id;
        exchange.In.Headers[DiagnosticId] = stale.Header;   // what the header bridge copies from a received message
        using (var template = new ProducerTemplate(ctx))
        {
            template.Start();
            await template.SendAsync(QueueUri, exchange);
        }
        var send = sends.Activities.Single(a => a.TraceId == outer.TraceId);

        var received = await ReceiveRaw(id);
        received.ApplicationProperties[DiagnosticId].Should().Be($"00-{outer.TraceId}-{send.SpanId}-01",
            "the next hop is a child of the send, not of the hop the copied header names");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_context_on()
    {
        var id = $"off-{Tag()}";
        using var receives = ReceiveSpansOf(id);
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        ctx.AddComponent(new AzureServiceBusComponent());
        await ctx.Start();
        using var sends = new RouteTelemetryProbe(a => a.Kind == ActivityKind.Producer
                                                       && a.GetTagItem("messaging.destination.name") as string == Queue);
        using var outer = new Activity("caller").Start();

        var exchange = new Exchange(new Message(id));
        exchange.In.Headers[AzureServiceBusHeaders.MessageId] = id;
        using (var template = new ProducerTemplate(ctx))
        {
            template.Start();
            await template.SendAsync(QueueUri, exchange);
        }

        var received = await ReceiveRaw(id);
        received.ApplicationProperties[DiagnosticId].Should().Be(outer.Id,
            "with no send span the ambient context goes out");
        sends.Activities.Where(a => a.TraceId == outer.TraceId).Should().BeEmpty();

        var done = new TaskCompletionSource();
        await using (await StartConsumer($"asb-trace-{id}", e => { if (e.In.GetHeader<string>(AzureServiceBusHeaders.MessageId) == id) done.TrySetResult(); }, telemetry: false))
        {
            await SendRaw(id, new Dictionary<string, object> { [DiagnosticId] = Caller().Header });
            (await Task.WhenAny(done.Task, Task.Delay(Wait))).Should().Be(done.Task);
        }
        receives.Activities.Should().BeEmpty();
    }
}

#endif
