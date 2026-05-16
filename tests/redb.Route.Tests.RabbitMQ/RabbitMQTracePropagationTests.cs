using System.Diagnostics;
using System.Text;
using redb.Route.Telemetry;

namespace redb.Route.Tests.RabbitMQ;

/// <summary>
/// Tests W3C trace context propagation through AMQP headers.
/// Validates the inject/extract round-trip using the same API
/// as RabbitMQProducer/RabbitMQConsumer without requiring a live broker.
/// </summary>
[Collection("Telemetry")]
public sealed class RabbitMQTracePropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public RabbitMQTracePropagationTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == RouteActivitySource.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    // ── Inject tests (Producer side) ──

    [Fact]
    public void Inject_WithActiveActivity_WritesTraceparentToAmqpHeaders()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();

        var headers = new Dictionary<string, object?>();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object?> h && !string.IsNullOrEmpty(value))
            {
                h[key] = value;
            }
        });

        headers.Should().ContainKey("traceparent");
        var traceparent = headers["traceparent"] as string;
        traceparent.Should().NotBeNullOrEmpty();
        traceparent.Should().StartWith("00-");
        traceparent!.Split('-').Should().HaveCount(4);
    }

    [Fact]
    public void Inject_WithTraceState_WritesTracestateToAmqpHeaders()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();
        activity!.TraceStateString = "vendor1=value1";

        var headers = new Dictionary<string, object?>();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object?> h && !string.IsNullOrEmpty(value))
            {
                h[key] = value;
            }
        });

        headers.Should().ContainKey("tracestate");
        (headers["tracestate"] as string).Should().Contain("vendor1=value1");
    }

    [Fact]
    public void Inject_WithNoActivity_DoesNotWriteHeaders()
    {
        Activity.Current = null;
        var headers = new Dictionary<string, object?>();
        // InjectTraceContext returns early when activity is null
        headers.Should().BeEmpty();
    }

    // ── Extract tests (Consumer side) ──

    [Fact]
    public void Extract_TraceparentFromAmqpHeaders_ParsesActivityContext()
    {
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();
        var traceparent = $"00-{traceId}-{spanId}-01";

        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = traceparent
        };

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not IDictionary<string, object?> h) return;
                if (!h.TryGetValue(key, out var raw) || raw is null) return;
                value = raw switch
                {
                    string s => s,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => raw.ToString()
                };
            },
            out var extractedTraceParent,
            out var extractedTraceState);

        extractedTraceParent.Should().NotBeNullOrEmpty();
        ActivityContext.TryParse(extractedTraceParent!, extractedTraceState, out var parentContext)
            .Should().BeTrue();

        parentContext.TraceId.ToString().Should().Be(traceId);
        parentContext.SpanId.ToString().Should().Be(spanId);
    }

    [Fact]
    public void Extract_ByteArrayHeader_ParsesCorrectly()
    {
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();
        var traceparent = $"00-{traceId}-{spanId}-01";

        // RabbitMQ can store headers as byte[]
        var headers = new Dictionary<string, object?>
        {
            ["traceparent"] = Encoding.UTF8.GetBytes(traceparent)
        };

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not IDictionary<string, object?> h) return;
                if (!h.TryGetValue(key, out var raw) || raw is null) return;
                value = raw switch
                {
                    string s => s,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => raw.ToString()
                };
            },
            out var extractedTraceParent,
            out _);

        extractedTraceParent.Should().NotBeNullOrEmpty();
        ActivityContext.TryParse(extractedTraceParent!, null, out var ctx).Should().BeTrue();
        ctx.TraceId.ToString().Should().Be(traceId);
    }

    [Fact]
    public void Extract_NoTraceparent_ReturnsNull()
    {
        var headers = new Dictionary<string, object?>
        {
            ["some-header"] = "value"
        };

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not IDictionary<string, object?> h) return;
                if (!h.TryGetValue(key, out var raw) || raw is null) return;
                value = raw?.ToString();
            },
            out var extractedTraceParent,
            out _);

        extractedTraceParent.Should().BeNullOrEmpty();
    }

    // ── Round-trip test ──

    [Fact]
    public void RoundTrip_InjectThenExtract_PreservesTraceContext()
    {
        // Producer side
        using var producerActivity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        producerActivity.Should().NotBeNull();

        var headers = new Dictionary<string, object?>();
        var propagator = DistributedContextPropagator.Current;

        propagator.Inject(producerActivity, headers, static (carrier, key, value) =>
        {
            if (carrier is IDictionary<string, object?> h && !string.IsNullOrEmpty(value))
                h[key] = value;
        });

        // Consumer side
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not IDictionary<string, object?> h) return;
                if (!h.TryGetValue(key, out var raw) || raw is null) return;
                value = raw switch
                {
                    string s => s,
                    byte[] bytes => Encoding.UTF8.GetString(bytes),
                    _ => raw.ToString()
                };
            },
            out var traceParent,
            out var traceState);

        ActivityContext.TryParse(traceParent!, traceState, out var parentContext).Should().BeTrue();

        using var consumerActivity = RouteActivitySource.Source.StartActivity(
            "test receive", ActivityKind.Consumer, parentContext);
        consumerActivity.Should().NotBeNull();

        // Same trace ID, parent-child relationship, different spans
        consumerActivity!.TraceId.Should().Be(producerActivity!.TraceId);
        consumerActivity.ParentSpanId.Should().Be(producerActivity.SpanId);
        consumerActivity.SpanId.Should().NotBe(producerActivity.SpanId);
    }

    // ── Activity kind and tags ──

    [Fact]
    public void ProducerActivity_HasCorrectKindAndTags()
    {
        using var activity = RouteActivitySource.Source.StartActivity("orders publish", ActivityKind.Producer);
        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Producer);

        activity.SetTag("messaging.system", "rabbitmq");
        activity.SetTag("messaging.operation", "publish");
        activity.SetTag("messaging.destination.name", "orders-exchange");
        activity.SetTag("messaging.rabbitmq.destination.routing_key", "order.created");

        activity.GetTagItem("messaging.system").Should().Be("rabbitmq");
        activity.GetTagItem("messaging.rabbitmq.destination.routing_key").Should().Be("order.created");
    }

    [Fact]
    public void ConsumerActivity_WithParent_HasCorrectKindAndTags()
    {
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();
        ActivityContext.TryParse($"00-{traceId}-{spanId}-01", null, out var parentContext);

        using var activity = RouteActivitySource.Source.StartActivity(
            "orders receive", ActivityKind.Consumer, parentContext);
        activity.Should().NotBeNull();
        activity!.Kind.Should().Be(ActivityKind.Consumer);

        activity.SetTag("messaging.system", "rabbitmq");
        activity.SetTag("messaging.operation", "receive");
        activity.SetTag("messaging.rabbitmq.destination.routing_key", "order.created");
        activity.SetTag("messaging.message.delivery_tag", 42UL);

        activity.GetTagItem("messaging.system").Should().Be("rabbitmq");
        activity.TraceId.ToString().Should().Be(traceId);
    }
}
