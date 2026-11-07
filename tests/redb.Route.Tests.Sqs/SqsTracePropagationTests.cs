using System.Diagnostics;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Sqs;
using redb.Route.Telemetry;
using redb.Route.Tests.Telemetry;
using Message = redb.Route.Core.Message;
using SqsDsl = redb.Route.Sqs.Fluent.Sqs;
using SnsDsl = redb.Route.Sqs.Fluent.Sns;

namespace redb.Route.Tests.Sqs;

/// <summary>
/// W3C trace context through SQS message attributes (and SNS attributes delivered raw to SQS), through the connector
/// against LocalStack. The consumer opens a receive span per message: a child of the sender's context, or a root without
/// one — never a child of the activity the receive loop inherited from whoever started it — with the sender's baggage
/// back on it and the route's spans under it; a failed route, a timeout inside it included, marks it red, our own stop
/// does not. The producers write the context of their own span over a copied one, and refuse a message whose attributes
/// the trace context would push past the limit of ten instead of dropping any. <c>EnableTelemetry=false</c> opens none
/// of these spans, while the ambient context still goes out.
/// Requires LocalStack at http://localhost:4566.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqsTracePropagationTests
{
    private const string ServiceUrl = "http://localhost:4566";
    private const string Region = "us-east-1";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static IAmazonSQS RawSqs() =>
        new AmazonSQSClient(new BasicAWSCredentials("test", "test"),
            new AmazonSQSConfig { ServiceURL = ServiceUrl, AuthenticationRegion = Region });

    private static string Q(string name) =>
        SqsDsl.Queue(name).ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test").AutoCreateQueue().Build();

    private static (ActivityTraceId TraceId, ActivitySpanId SpanId, string Header) Caller()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        return (traceId, spanId, $"00-{traceId}-{spanId}-01");
    }

    private static RouteTelemetryProbe SpansOf(string queue, ActivityKind kind)
        => new(a => a.Kind == kind && RouteTelemetryProbe.Tag(a, "messaging.destination.name") == queue);

    /// <summary>Sends one message straight through the SDK, as a sender that is not redb would.</summary>
    private static async Task SendRaw(string queue, IDictionary<string, string>? attributes = null)
    {
        using var sqs = RawSqs();
        var url = (await sqs.CreateQueueAsync(queue)).QueueUrl;
        var request = new SendMessageRequest { QueueUrl = url, MessageBody = "payload", MessageAttributes = [] };
        foreach (var (key, value) in attributes ?? new Dictionary<string, string>())
            request.MessageAttributes[key] = new MessageAttributeValue { DataType = "String", StringValue = value };
        await sqs.SendMessageAsync(request);
    }

    /// <summary>Receives one message straight through the SDK, attributes included.</summary>
    private static async Task<Amazon.SQS.Model.Message> ReceiveRaw(string queue)
    {
        using var sqs = RawSqs();
        var url = (await sqs.CreateQueueAsync(queue)).QueueUrl;
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url, WaitTimeSeconds = 2, MaxNumberOfMessages = 1, MessageAttributeNames = ["All"],
            });
            if (response.Messages is { Count: > 0 } messages)
                return messages[0];
        }
        throw new TimeoutException($"No message arrived on {queue}.");
    }

    private static async Task Send(string uri, IExchange exchange, bool telemetry = true)
    {
        await using var ctx = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = telemetry });
        ctx.AddComponent(new SqsComponent());
        ctx.AddComponent(new SnsComponent());
        await ctx.Start();
        using var template = new ProducerTemplate(ctx);
        template.Start();
        await template.SendAsync(uri, exchange);
    }

    /// <summary>A context with one consuming route, started inside a host span the receive loop inherits.</summary>
    private static async Task<RouteContext> StartConsumer(string queue, Func<IExchange, CancellationToken, Task> step,
        bool telemetry = true, TimeSpan? shutdown = null)
    {
        var options = new RouteEngineOptions { EnableTelemetry = telemetry };
        if (shutdown is { } timeout)
            options.ShutdownTimeout = timeout;
        var ctx = new RouteContext(options: options);
        ctx.AddComponent(new SqsComponent());
        ctx.AddRoutes(r => r.From(Q(queue)).RouteId($"sqs-{queue}").Process(step));
        using (new Activity("host startup").SetIdFormat(ActivityIdFormat.W3C).Start())
            await ctx.Start();
        return ctx;
    }

    private static Task<RouteContext> StartConsumer(string queue, Action<IExchange> step, bool telemetry = true)
        => StartConsumer(queue, (e, _) => { step(e); return Task.CompletedTask; }, telemetry);

    private static async Task Arrived(Task task)
        => (await Task.WhenAny(task, Task.Delay(Wait))).Should().Be(task, "the message should reach the route");

    private static async Task SpanEnded(RouteTelemetryProbe probe)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (probe.Activities.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    [Fact]
    public async Task A_message_without_context_opens_a_root_receive_span()
    {
        var queue = UniqueName("trace-root");
        using var probe = SpansOf(queue, ActivityKind.Consumer);
        var done = new TaskCompletionSource();
        await using (await StartConsumer(queue, _ => done.TrySetResult()))
        {
            await SendRaw(queue);
            await Arrived(done.Task);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.ParentSpanId.Should().Be(default(ActivitySpanId),
            "a message that carries no context starts a trace, not a child of the host's startup span");
        RouteTelemetryProbe.Tag(receive, RouteTelemetryProbe.EndpointTag).Should().StartWith("sqs:");
    }

    [Fact]
    public async Task A_message_with_context_continues_the_senders_trace_and_the_route_runs_under_it()
    {
        var queue = UniqueName("trace-child");
        using var probe = SpansOf(queue, ActivityKind.Consumer);
        using var routes = RouteTelemetryProbe.ForRouteIdPrefix($"sqs-{queue}");
        var caller = Caller();
        var done = new TaskCompletionSource();
        await using (await StartConsumer(queue, _ => done.TrySetResult()))
        {
            await SendRaw(queue, new Dictionary<string, string> { ["traceparent"] = caller.Header });
            await Arrived(done.Task);
        }

        var receive = probe.Activities.Should().ContainSingle().Subject;
        receive.TraceId.Should().Be(caller.TraceId);
        receive.ParentSpanId.Should().Be(caller.SpanId);
        routes.Activities.Should().ContainSingle().Which.ParentSpanId.Should().Be(receive.SpanId);
    }

    [Fact]
    public async Task The_senders_baggage_reaches_the_route()
    {
        var queue = UniqueName("trace-bag");
        using var probe = SpansOf(queue, ActivityKind.Consumer);
        var seen = new TaskCompletionSource<string?>();
        await using (await StartConsumer(queue, _ => seen.TrySetResult(Activity.Current?.GetBaggageItem("tenant"))))
        {
            await SendRaw(queue, new Dictionary<string, string> { ["traceparent"] = Caller().Header, ["baggage"] = "tenant=t1" });
            await Arrived(seen.Task);
        }

        seen.Task.Result.Should().Be("t1");
    }

    [Theory]
    [InlineData("route failed")]
    [InlineData("a call inside the route timed out")]
    public async Task A_failed_route_marks_the_receive_span_red(string failure)
    {
        var queue = UniqueName("trace-fail");
        using var probe = SpansOf(queue, ActivityKind.Consumer);
        var thrown = new TaskCompletionSource();
        await using (await StartConsumer(queue, _ =>
                     {
                         thrown.TrySetResult();
                         throw failure.Contains("timed out")
                             ? new TaskCanceledException(failure)
                             : new InvalidOperationException(failure);
                     }))
        {
            await SendRaw(queue);
            await Arrived(thrown.Task);
            await SpanEnded(probe);
        }

        probe.Activities.Should().NotBeEmpty();
        probe.Activities[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Stopping_the_consumer_does_not_mark_the_interrupted_message_red()
    {
        var queue = UniqueName("trace-stop");
        using var probe = SpansOf(queue, ActivityKind.Consumer);
        var started = new TaskCompletionSource();
        var ctx = await StartConsumer(queue, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, shutdown: TimeSpan.FromSeconds(2));
        await SendRaw(queue);
        await Arrived(started.Task);

        await ctx.DisposeAsync();   // the drain gives up and cancels the processing token

        await SpanEnded(probe);
        probe.Activities.Should().NotBeEmpty();
        probe.Activities.Should().OnlyContain(a => a.Status != ActivityStatusCode.Error, "our own stop is not a failure");
    }

    [Fact]
    public async Task The_producer_sends_the_context_of_its_own_span_over_a_copied_one()
    {
        var queue = UniqueName("trace-send");
        using var sends = SpansOf(queue, ActivityKind.Producer);
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();

        var exchange = new Exchange(new Message("payload"));
        // What the header bridge copies from a received message.
        exchange.In.Headers[SqsHeaders.MessageAttributePrefix + "traceparent"] = Caller().Header;
        await Send(Q(queue), exchange);

        var send = sends.Activities.Should().ContainSingle().Subject;
        var received = await ReceiveRaw(queue);
        received.MessageAttributes["traceparent"].StringValue.Should().Be($"00-{outer.TraceId}-{send.SpanId}-01");
    }

    [Fact]
    public async Task Tracing_off_opens_no_span_and_still_passes_the_context_on()
    {
        var queue = UniqueName("trace-off");
        using var spans = new RouteTelemetryProbe(a => RouteTelemetryProbe.Tag(a, "messaging.destination.name") == queue);
        using (var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            await Send(Q(queue), new Exchange(new Message("payload")), telemetry: false);
            var received = await ReceiveRaw(queue);
            received.MessageAttributes["traceparent"].StringValue.Should().Be(outer.Id,
                "with no send span the ambient context goes out");
        }

        var done = new TaskCompletionSource();
        await using (await StartConsumer(queue, _ => done.TrySetResult(), telemetry: false))
        {
            await SendRaw(queue, new Dictionary<string, string> { ["traceparent"] = Caller().Header });
            await Arrived(done.Task);
        }
        spans.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_the_trace_context_pushes_past_ten_attributes_is_refused_not_trimmed()
    {
        var queue = UniqueName("trace-limit");
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var exchange = new Exchange(new Message("payload"));
        for (var i = 0; i < 10; i++)
            exchange.In.Headers[$"h{i}"] = "v";

        var act = () => Send(Q(queue), exchange);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("10 from the exchange headers").And.Contain("trace context");
    }

    // ── SNS ──

    private static async Task<string> SubscribedTopic(string queue, string topic)
    {
        using var sqs = RawSqs();
        var url = (await sqs.CreateQueueAsync(queue)).QueueUrl;
        var arn = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = url, AttributeNames = ["QueueArn"],
        })).Attributes["QueueArn"];
        return SnsDsl.Topic(topic).ServiceUrl(ServiceUrl).Region(Region).Credentials("test", "test")
            .AutoCreateTopic().SubscribeSnsToSqs(arn).RawMessageDelivery().Build();
    }

    [Fact]
    public async Task Sns_tracing_off_opens_no_span_and_still_passes_the_context_on()
    {
        var queue = UniqueName("sns-off-q");
        var topic = UniqueName("sns-off");
        var snsUri = await SubscribedTopic(queue, topic);
        using var spans = new RouteTelemetryProbe(a => RouteTelemetryProbe.Tag(a, "messaging.destination.name") == topic);
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();

        await Send(snsUri, new Exchange(new Message("payload")), telemetry: false);

        var received = await ReceiveRaw(queue);
        received.MessageAttributes["traceparent"].StringValue.Should().Be(outer.Id);
        spans.Activities.Should().BeEmpty();
    }

    [Fact]
    public async Task Sns_a_message_the_trace_context_pushes_past_ten_attributes_is_refused_not_trimmed()
    {
        var topic = UniqueName("sns-limit");
        var snsUri = await SubscribedTopic(UniqueName("sns-limit-q"), topic);
        using var outer = new Activity("caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        var exchange = new Exchange(new Message("payload"));
        for (var i = 0; i < 10; i++)
            exchange.In.Headers[$"h{i}"] = "v";

        var act = () => Send(snsUri, exchange);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("10 from the exchange headers").And.Contain("trace context");
    }
}
