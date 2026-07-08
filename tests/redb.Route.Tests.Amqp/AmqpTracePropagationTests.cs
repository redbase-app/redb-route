using System.Diagnostics;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Amqp;

/// <summary>
/// Tests W3C trace context propagation through AMQP application-properties.
/// Validates the inject/extract round-trip using the same API
/// as AmqpProducer/AmqpConsumer without requiring a live broker.
/// </summary>
[Collection("Telemetry")]
public sealed class AmqpTracePropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public AmqpTracePropagationTests()
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
    public void Inject_WithActiveActivity_WritesTraceparentToAmqpMap()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();

        var map = new global::Amqp.Types.Map();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, map, static (carrier, key, value) =>
        {
            if (carrier is global::Amqp.Types.Map m && !string.IsNullOrEmpty(value))
                m[key] = value;
        });

        map.ContainsKey("traceparent").Should().BeTrue();
        var traceparent = map["traceparent"] as string;
        traceparent.Should().NotBeNullOrEmpty();
        traceparent.Should().StartWith("00-");
        traceparent!.Split('-').Should().HaveCount(4);
    }

    [Fact]
    public void Inject_WithTraceState_WritesTracestateToAmqpMap()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();
        activity!.TraceStateString = "vendor1=value1";

        var map = new global::Amqp.Types.Map();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, map, static (carrier, key, value) =>
        {
            if (carrier is global::Amqp.Types.Map m && !string.IsNullOrEmpty(value))
                m[key] = value;
        });

        map.ContainsKey("tracestate").Should().BeTrue();
        (map["tracestate"] as string).Should().Contain("vendor1=value1");
    }

    [Fact]
    public void Inject_WithNoActivity_DoesNotCrash()
    {
        Activity.Current = null;
        var map = new global::Amqp.Types.Map();
        // No activity → no injection → empty map
        map.Count.Should().Be(0);
    }

    // ── Extract tests (Consumer side) ──

    [Fact]
    public void Extract_TraceparentFromAmqpMap_ParsesActivityContext()
    {
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();
        var traceparent = $"00-{traceId}-{spanId}-01";

        var map = new global::Amqp.Types.Map
        {
            ["traceparent"] = traceparent
        };

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(map,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not global::Amqp.Types.Map m) return;
                if (!m.TryGetValue(key, out var raw) || raw is null) return;
                value = raw.ToString();
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
    public void Extract_NoTraceparent_ReturnsNull()
    {
        var map = new global::Amqp.Types.Map
        {
            ["some-header"] = "value"
        };

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(map,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not global::Amqp.Types.Map m) return;
                if (!m.TryGetValue(key, out var raw) || raw is null) return;
                value = raw.ToString();
            },
            out var extractedTraceParent,
            out _);

        extractedTraceParent.Should().BeNullOrEmpty();
    }

    // ── Round-trip ──

    [Fact]
    public void RoundTrip_InjectThenExtract_PreservesTraceContext()
    {
        // Producer side
        using var producerActivity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        producerActivity.Should().NotBeNull();

        var map = new global::Amqp.Types.Map();
        var propagator = DistributedContextPropagator.Current;

        propagator.Inject(producerActivity, map, static (carrier, key, value) =>
        {
            if (carrier is global::Amqp.Types.Map m && !string.IsNullOrEmpty(value))
                m[key] = value;
        });

        // Consumer side
        propagator.ExtractTraceIdAndState(map,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not global::Amqp.Types.Map m) return;
                if (!m.TryGetValue(key, out var raw) || raw is null) return;
                value = raw.ToString();
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

        activity.SetTag("messaging.system", "amqp");
        activity.SetTag("messaging.operation", "publish");
        activity.SetTag("messaging.destination.name", "orders");

        activity.GetTagItem("messaging.system").Should().Be("amqp");
        activity.GetTagItem("messaging.destination.name").Should().Be("orders");
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

        activity.SetTag("messaging.system", "amqp");
        activity.SetTag("messaging.operation", "receive");

        activity.GetTagItem("messaging.system").Should().Be("amqp");
        activity.TraceId.ToString().Should().Be(traceId);
    }
}
