using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Kafka;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// The Kafka connector on the core tracing contract (<see cref="RouteTelemetryExtensions"/>), against the live cluster:
/// the receive span's parent comes from the record or nowhere, the sender's baggage arrives, a batch is a root linked to
/// every record, and EnableTelemetry=false opens nothing.
/// </summary>
[Collection("Telemetry")]
[Trait("Category", "Integration")]
public sealed class KafkaTracingTests : IDisposable
{
    private const string BootstrapServers = "localhost:29092,localhost:29094,localhost:29096";

    private readonly ConcurrentQueue<Activity> _stopped = new();
    private readonly ActivityListener _listener;

    public KafkaTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    /// <summary>The Kafka spans of <paramref name="topic"/> that ended: other tests of the collection use other topics.</summary>
    private List<Activity> Spans(string topic, ActivityKind kind) =>
        _stopped.Where(a => a.Kind == kind && Equals(a.GetTagItem("messaging.destination.name"), topic)).ToList();

    private static string Topic(string name) => $"trace-{name}-{Guid.NewGuid():N}";

    private static KafkaEndpoint Endpoint(RouteContext context, string topic, string parameters = "")
    {
        var component = context.GetComponent<KafkaComponent>() ?? AddKafka(context);
        return (KafkaEndpoint)component.CreateEndpoint(
            EndpointUriParser.Parse($"kafka://{topic}?brokers={BootstrapServers}&{parameters}"));
    }

    private static KafkaComponent AddKafka(RouteContext context)
    {
        var component = new KafkaComponent();
        context.AddComponent(component);
        return component;
    }

    private static async Task ProduceRaw(string topic, params string?[] traceparents)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = BootstrapServers }).Build();
        foreach (var traceparent in traceparents)
        {
            var headers = new Headers();
            if (traceparent is not null)
                headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));
            await producer.ProduceAsync(topic, new Message<string, string> { Value = "x", Headers = headers });
        }
    }

    /// <summary>Consumes until <paramref name="count"/> exchanges reached the route, capturing what the route saw.</summary>
    private static async Task<List<(IExchange Exchange, Activity? Current)>> Consume(
        KafkaEndpoint endpoint, int count = 1, Exception? routeFails = null)
    {
        var seen = new ConcurrentQueue<(IExchange, Activity?)>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen.Enqueue((call.Arg<IExchange>(), Activity.Current));
                if (seen.Count >= count)
                    done.TrySetResult();
                return routeFails is null ? Task.CompletedTask : Task.FromException(routeFails);
            });

        var consumer = (KafkaConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();
        try
        {
            await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await consumer.Stop();
        }
        return seen.ToList();
    }

    private static string Group() => $"groupId=trace-{Guid.NewGuid():N}&autoOffsetReset=earliest";

    [Fact]
    public async Task A_record_without_traceparent_is_a_root_even_under_an_ambient_activity()
    {
        var topic = Topic("root");
        await ProduceRaw(topic, [null]);
        await using var context = new RouteContext();
        var endpoint = Endpoint(context, topic, Group());

        // The poll loop inherits the activity current when the consumer starts.
        using (var ambient = RouteActivitySource.Source.StartActivity("host work"))
        {
            ambient.Should().NotBeNull();
            await Consume(endpoint);
        }

        var receive = Spans(topic, ActivityKind.Consumer).Should().ContainSingle().Subject;
        receive.ParentSpanId.Should().Be(default(ActivitySpanId), "a record nobody traced starts its own trace");
        receive.GetTagItem("redb.route.endpoint").Should().NotBeNull("the core contract tags every transport span");
    }

    [Fact]
    public async Task Baggage_travels_from_the_send_to_the_route_that_receives_it()
    {
        // The receive side used to read traceparent only: the sender's baggage stopped at Kafka.
        var topic = Topic("baggage");
        await using var context = new RouteContext();
        var producer = (KafkaProducer)Endpoint(context, topic).CreateProducer();
        await producer.Start();
        ActivityTraceId sentTrace;
        using (var sender = RouteActivitySource.Source.StartActivity("sender"))
        {
            sender!.AddBaggage("tenant", "acme");
            sentTrace = sender.TraceId;
            await producer.Process(new Exchange(new Message("with-baggage")));
        }
        await producer.Stop();

        var (_, current) = (await Consume(Endpoint(context, topic, Group()))).Single();

        current.Should().NotBeNull();
        current!.GetBaggageItem("tenant").Should().Be("acme", "the route runs under the receive span, with the sender's baggage");
        current.TraceId.Should().Be(sentTrace);
        var publish = Spans(topic, ActivityKind.Producer).Should().ContainSingle().Subject;
        publish.GetTagItem("redb.route.endpoint").Should().NotBeNull();
        Spans(topic, ActivityKind.Consumer).Single().ParentSpanId.Should().Be(publish.SpanId);
    }

    [Fact]
    public async Task A_batch_is_a_root_linked_to_the_context_of_every_record()
    {
        // The batch span used to take batch[0]'s context as its parent and nothing from the others.
        var topic = Topic("batch");
        var first = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var second = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        await ProduceRaw(topic,
            $"00-{first.TraceId}-{first.SpanId}-01",
            $"00-{second.TraceId}-{second.SpanId}-01");
        await using var context = new RouteContext();

        // A long poll window gathers both records into one batch.
        await Consume(Endpoint(context, topic, $"{Group()}&maxPollRecords=10&pollTimeoutMs=5000"));

        var batch = Spans(topic, ActivityKind.Consumer).Should().ContainSingle().Subject;
        batch.GetTagItem("messaging.batch.message_count").Should().Be(2);
        batch.ParentSpanId.Should().Be(default(ActivitySpanId), "no record of the batch is its parent");
        batch.TraceId.Should().NotBe(first.TraceId).And.NotBe(second.TraceId);
        batch.Links.Select(l => l.Context.TraceId).Should().BeEquivalentTo([first.TraceId, second.TraceId]);
    }

    [Fact]
    public async Task EnableTelemetry_false_opens_no_kafka_span()
    {
        var topic = Topic("off");
        await using var context = new RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        var producer = (KafkaProducer)Endpoint(context, topic).CreateProducer();
        await producer.Start();
        await producer.Process(new Exchange(new Message("untraced")));
        await producer.Stop();

        await Consume(Endpoint(context, topic, Group()));

        Spans(topic, ActivityKind.Producer).Should().BeEmpty();
        Spans(topic, ActivityKind.Consumer).Should().BeEmpty();
    }

    [Fact]
    public async Task A_trace_header_bridged_in_another_case_is_replaced_by_the_sends_context()
    {
        // Review of 1e6964ac: the writer removed "traceparent" exactly, the reader matches without case and takes the
        // first. A route http -> kafka bridged the caller's "Traceparent", which stayed on the record ahead of ours.
        var topic = Topic("case");
        var caller = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";
        await using var context = new RouteContext();
        var producer = (KafkaProducer)Endpoint(context, topic).CreateProducer();
        await producer.Start();
        var exchange = new Exchange(new Message("bridged"));
        exchange.In.Headers["Traceparent"] = caller;
        await producer.Process(exchange);
        await producer.Stop();

        (await Consume(Endpoint(context, topic, Group()))).Should().ContainSingle();

        var publish = Spans(topic, ActivityKind.Producer).Should().ContainSingle().Subject;
        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.ParentSpanId.Should().Be(publish.SpanId, "the receive continues this send, not the HTTP caller");
    }

    [Fact]
    public async Task A_route_failing_on_a_record_marks_the_receive_span()
    {
        var topic = Topic("route-fails");
        await ProduceRaw(topic, [null]);
        await using var context = new RouteContext();

        await Consume(Endpoint(context, topic, Group()), routeFails: new InvalidOperationException("route failed"));

        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_route_failing_on_a_batch_marks_the_batch_span()
    {
        var topic = Topic("batch-fails");
        await ProduceRaw(topic, null, null);
        await using var context = new RouteContext();

        await Consume(Endpoint(context, topic, $"{Group()}&maxPollRecords=10&pollTimeoutMs=5000"),
            routeFails: new InvalidOperationException("route failed"));

        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_of_a_call_inside_the_route_marks_the_receive_span()
    {
        // An HttpClient timeout comes as a TaskCanceledException, an OperationCanceledException by type; it is a failure
        // of the record all the same. Only the consumer's own cancellation is not.
        var topic = Topic("route-timeout");
        await ProduceRaw(topic, [null]);
        await using var context = new RouteContext();

        await Consume(Endpoint(context, topic, Group()),
            routeFails: new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_timeout_of_a_call_inside_the_route_marks_the_batch_span()
    {
        var topic = Topic("batch-timeout");
        await ProduceRaw(topic, null, null);
        await using var context = new RouteContext();

        await Consume(Endpoint(context, topic, $"{Group()}&maxPollRecords=10&pollTimeoutMs=5000"),
            routeFails: new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Stopping_the_consumer_mid_route_does_not_mark_the_receive_span()
    {
        var topic = Topic("route-stopped");
        await ProduceRaw(topic, [null]);
        await using var context = new RouteContext();
        var inRoute = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                inRoute.TrySetResult();
                // The route waits on the consumer's processing token: only the stop ends it.
                return Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
            });
        var consumer = (KafkaConsumer)Endpoint(context, topic, Group()).CreateConsumer(processor);
        await consumer.Start();
        await inRoute.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A stop bounded by this token force-cancels the in-flight route when it runs out.
        using var stopWithin = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await consumer.Stop(stopWithin.Token);

        Spans(topic, ActivityKind.Consumer).Should().ContainSingle()
            .Which.Status.Should().NotBe(ActivityStatusCode.Error, "the consumer's own stop is not a failure of the record");
    }

    [Fact]
    public async Task A_failed_send_marks_its_span()
    {
        var topic = Topic("fail");
        await using var context = new RouteContext();
        var producer = (KafkaProducer)Endpoint(context, topic).CreateProducer();
        await producer.Start();

        // Over librdkafka's default message.max.bytes: refused before it leaves the client.
        var send = () => producer.Process(new Exchange(new Message(new byte[2_000_000])));

        await send.Should().ThrowAsync<KafkaException>();
        await producer.Stop();
        Spans(topic, ActivityKind.Producer).Should().ContainSingle()
            .Which.Status.Should().Be(ActivityStatusCode.Error);
    }
}
