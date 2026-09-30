using System.Collections.Concurrent;
using System.Diagnostics;
using RabbitMQ.Client;
using redb.Route.Abstractions;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.RabbitMQ;
using redb.Route.Telemetry;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// The connector's spans follow the core's transport contract (<see cref="RouteTelemetryExtensions"/>): the receive
/// span takes its parent from the message only, the sender's baggage reaches the route, every span names its endpoint,
/// and <c>EnableTelemetry=false</c> opens none. Spans are told apart by the routing key, which is the test's own queue.
/// Expects RabbitMQ at localhost:5672 (admin/admin).
/// </summary>
[Collection("Telemetry")]
[Trait("Category", "Integration")]
public sealed class RabbitMQTracingTests : IDisposable
{
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;

    public RabbitMQTracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _spans.Enqueue,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task A_message_without_traceparent_starts_a_root_span_not_a_child_of_the_ambient_activity()
    {
        var queue = await DeclareQueueAsync();
        var seen = new TaskCompletionSource<Activity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (consumer, endpoint, context) = Consumer(queue, new RouteEngineOptions(), ex => seen.TrySetResult(Activity.Current));

        // The consumer's dispatch loop is created under this activity, so the delivery callback runs with it ambient.
        using (var host = RouteActivitySource.Source.StartActivity("host work"))
        {
            host.Should().NotBeNull();
            await consumer.Start();
        }

        await PublishRawAsync(queue);
        var routeActivity = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Stop(consumer, endpoint, context);

        var receive = ReceiveSpan(queue);
        receive.ParentSpanId.Should().Be(default(ActivitySpanId), "a message that names no parent starts a trace of its own");
        routeActivity.Should().BeSameAs(receive, "the route runs under the receive span");
    }

    [Fact]
    public async Task The_senders_baggage_reaches_the_route_and_both_spans_name_their_endpoint()
    {
        var queue = await DeclareQueueAsync();
        var baggage = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (consumer, endpoint, context) = Consumer(queue, new RouteEngineOptions(),
            _ => baggage.TrySetResult(Activity.Current?.GetBaggageItem("tenant")));
        await consumer.Start();

        var (producer, producerEndpoint) = Producer(queue, context);
        await producer.Start();
        using (var sender = RouteActivitySource.Source.StartActivity("sender"))
        {
            sender!.AddBaggage("tenant", "acme");
            await producer.Process(new Exchange(new Message("with baggage")));
        }

        (await baggage.Task.WaitAsync(TimeSpan.FromSeconds(15))).Should().Be("acme");
        await producer.Stop();
        await producerEndpoint.Stop();
        await Stop(consumer, endpoint, context);

        var publish = Span(queue, ActivityKind.Producer);
        var receive = ReceiveSpan(queue);
        receive.TraceId.Should().Be(publish.TraceId);
        publish.GetTagItem("redb.route.endpoint").Should().BeOfType<string>().Which.Should().Contain(queue);
        receive.GetTagItem("redb.route.endpoint").Should().BeOfType<string>().Which.Should().Contain(queue);
    }

    [Fact]
    public async Task With_telemetry_off_neither_side_opens_a_span()
    {
        var queue = await DeclareQueueAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (consumer, endpoint, context) = Consumer(queue, new RouteEngineOptions { EnableTelemetry = false },
            _ => received.TrySetResult());
        await consumer.Start();

        var (producer, producerEndpoint) = Producer(queue, context);
        await producer.Start();
        await producer.Process(new Exchange(new Message("untraced")));

        await received.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await producer.Stop();
        await producerEndpoint.Stop();
        await Stop(consumer, endpoint, context);

        SpansOf(queue).Should().BeEmpty("EnableTelemetry=false turns the transport spans off as well");
    }

    [Fact]
    public async Task A_traceparent_copied_from_the_incoming_message_is_replaced_by_the_send_span()
    {
        var queue = await DeclareQueueAsync();
        var context = new RouteContext();
        context.AddComponent(new RabbitMQComponent());
        var (producer, producerEndpoint) = Producer(queue, context);
        await producer.Start();

        // A consume-to-produce bridge carries the incoming headers over; that traceparent names the previous hop.
        const string previousHop = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";
        var exchange = new Exchange(new Message("bridged"));
        exchange.In.Headers["traceparent"] = previousHop;
        await producer.Process(exchange);
        await producer.Stop();
        await producerEndpoint.Stop();

        string? sent;
        await using (var connection = await ConnectAsync())
        await using (var channel = await connection.CreateChannelAsync())
        {
            var got = await channel.BasicGetAsync(queue, autoAck: true);
            got.Should().NotBeNull();
            sent = ReadHeader(got!.BasicProperties.Headers, "traceparent");
            await channel.QueueDeleteAsync(queue);
        }
        await context.DisposeAsync();

        var publish = Span(queue, ActivityKind.Producer);
        sent.Should().NotBe(previousHop).And.Contain(publish.SpanId.ToHexString(), "the next hop's parent is this send");
    }

    [Fact]
    public async Task An_ack_that_fails_after_a_successful_route_marks_the_receive_span_an_error()
    {
        var queue = await DeclareQueueAsync();
        var clientName = $"trace-ackfail-{Guid.NewGuid():N}";
        var routeDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RouteContext();
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(
            $"{Uri(queue)}&clientName={clientName}&automaticRecovery=false"));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            // The route succeeds, but its connection is gone before the consumer acks.
            await CloseBrokerConnectionAsync(clientName);
            routeDone.TrySetResult();
        });
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        await PublishRawAsync(queue);
        await routeDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => SpansOf(queue).Any(a => a.Kind == ActivityKind.Consumer));
        await consumer.Stop();
        await endpoint.Stop();
        await context.DisposeAsync();
        await DeleteQueueAsync(queue);

        ReceiveSpan(queue).Status.Should().Be(ActivityStatusCode.Error, "the delivery was not acknowledged, the unit of work did not end well");
    }

    [Fact]
    public async Task A_route_cancelled_by_stop_does_not_mark_the_receive_span_an_error()
    {
        var queue = await DeclareQueueAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RouteContext();
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(Uri(queue)));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(async ci =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ci.Arg<CancellationToken>());
        });
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(processor);
        consumer.DrainTimeout = TimeSpan.FromMilliseconds(200);
        await consumer.Start();

        await PublishRawAsync(queue);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await consumer.Stop();   // the drain gives up and cancels the route: a stop, not a failure
        await endpoint.Stop();
        await context.DisposeAsync();
        await DeleteQueueAsync(queue);

        ReceiveSpan(queue).Status.Should().NotBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task A_call_inside_the_route_that_times_out_marks_the_receive_span_an_error()
    {
        // HttpClient reports its own timeout as TaskCanceledException, an OperationCanceledException that no stop of
        // ours asked for: a failure, unlike a stop cancelling the route.
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var port = ((System.Net.IPEndPoint)silent.LocalEndpoint).Port;
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(200) };

        var queue = await DeclareQueueAsync();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new RouteContext();
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(Uri(queue)));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            try { await http.GetAsync($"http://127.0.0.1:{port}/"); }
            finally { failed.TrySetResult(); }
        });
        var consumer = (RabbitMQConsumer)endpoint.CreateConsumer(processor);
        await consumer.Start();

        await PublishRawAsync(queue);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await WaitForAsync(() => SpansOf(queue).Any(a => a.Kind == ActivityKind.Consumer));
        await consumer.Stop();
        await endpoint.Stop();
        await context.DisposeAsync();
        await DeleteQueueAsync(queue);

        SpansOf(queue).First(a => a.Kind == ActivityKind.Consumer).Status.Should().Be(ActivityStatusCode.Error,
            "a timeout inside the route is a failure, not a stop");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
    }

    private static async Task DeleteQueueAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(queue);
    }

    /// <summary>Has the broker close the connection named <paramref name="clientName"/>, and waits until it is gone.</summary>
    private static async Task CloseBrokerConnectionAsync(string clientName)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("admin:admin")));

        async Task<string?> FindAsync()
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("http://localhost:15672/api/connections"));
            foreach (var c in doc.RootElement.EnumerateArray())
                if (c.TryGetProperty("client_properties", out var p) && p.TryGetProperty("connection_name", out var n)
                    && n.GetString() == clientName)
                    return c.GetProperty("name").GetString();
            return null;
        }

        string? name = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (name is null && DateTime.UtcNow < deadline)
        {
            name = await FindAsync();
            if (name is null) await Task.Delay(250);
        }
        name.Should().NotBeNull($"the broker lists the connection '{clientName}'");

        (await http.DeleteAsync($"http://localhost:15672/api/connections/{System.Uri.EscapeDataString(name!)}")).EnsureSuccessStatusCode();
        while (await FindAsync() is not null && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        await Task.Delay(300);   // the close reaches the client
    }

    private static string? ReadHeader(IDictionary<string, object?>? headers, string name) =>
        headers is not null && headers.TryGetValue(name, out var raw)
            ? raw is byte[] bytes ? System.Text.Encoding.UTF8.GetString(bytes) : raw?.ToString()
            : null;

    // ───── Helpers ─────

    private IEnumerable<Activity> SpansOf(string queue) =>
        _spans.Where(a => a.GetTagItem("messaging.rabbitmq.destination.routing_key") as string == queue);

    private Activity Span(string queue, ActivityKind kind) => SpansOf(queue).Should().ContainSingle(a => a.Kind == kind).Subject;

    private Activity ReceiveSpan(string queue) => Span(queue, ActivityKind.Consumer);

    private static (RabbitMQConsumer, RabbitMQEndpoint, RouteContext) Consumer(
        string queue, RouteEngineOptions options, Action<IExchange> onMessage)
    {
        var context = new RouteContext(options: options);
        var component = new RabbitMQComponent();
        context.AddComponent(component);
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(Uri(queue)));
        var processor = Substitute.For<IProcessor>();
        processor.Process(Arg.Any<IExchange>(), Arg.Any<CancellationToken>())
            .Returns(ci => { onMessage(ci.Arg<IExchange>()); return Task.CompletedTask; });
        return ((RabbitMQConsumer)endpoint.CreateConsumer(processor), endpoint, context);
    }

    private static (RabbitMQProducer, RabbitMQEndpoint) Producer(string queue, RouteContext context)
    {
        var component = context.GetComponent<RabbitMQComponent>()!;
        var endpoint = (RabbitMQEndpoint)component.CreateEndpoint(EndpointUriParser.Parse(Uri(queue)));
        return ((RabbitMQProducer)endpoint.CreateProducer(), endpoint);
    }

    private static string Uri(string queue) => $"rabbitmq://{queue}?host=localhost&port=5672&username=admin&password=admin";

    private static async Task Stop(RabbitMQConsumer consumer, RabbitMQEndpoint endpoint, RouteContext context)
    {
        await consumer.Stop();
        await endpoint.Stop();
        await context.DisposeAsync();
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeleteAsync(endpoint.QueueName);
    }

    private static async Task<string> DeclareQueueAsync()
    {
        var queue = $"test-trace-{Guid.NewGuid():N}";
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: false, exclusive: false, autoDelete: false);
        return queue;
    }

    /// <summary>A message with no headers at all: no traceparent, no baggage.</summary>
    private static async Task PublishRawAsync(string queue)
    {
        await using var connection = await ConnectAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.BasicPublishAsync(string.Empty, queue, "plain"u8.ToArray());
    }

    private static async Task<IConnection> ConnectAsync()
        => await new ConnectionFactory { HostName = "localhost", Port = 5672, UserName = "admin", Password = "admin" }.CreateConnectionAsync();
}
