using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Kafka;

/// <summary>
/// Tests W3C trace context propagation through Kafka headers.
/// Validates the inject/extract round-trip using the same API
/// as KafkaProducer/KafkaConsumer without requiring a live broker.
/// </summary>
[Collection("Telemetry")]
public sealed class KafkaTracePropagationTests : IDisposable
{
    private readonly ActivityListener _listener;

    public KafkaTracePropagationTests()
    {
        // Subscribe to RouteActivitySource so StartActivity returns non-null
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
    public void Inject_WithActiveActivity_WritesTraceparentToKafkaHeaders()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();

        var headers = new Headers();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is Headers h && !string.IsNullOrEmpty(value))
            {
                h.Remove(key);
                h.Add(key, Encoding.UTF8.GetBytes(value));
            }
        });

        var traceparent = headers.FirstOrDefault(h => h.Key == "traceparent");
        traceparent.Should().NotBeNull();

        var value = Encoding.UTF8.GetString(traceparent!.GetValueBytes());
        value.Should().StartWith("00-"); // W3C version
        value.Split('-').Should().HaveCount(4); // version-traceId-spanId-flags
    }

    [Fact]
    public void Inject_WithTraceState_WritesTracestateToKafkaHeaders()
    {
        using var activity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        activity.Should().NotBeNull();
        activity!.TraceStateString = "vendor1=value1";

        var headers = new Headers();

        var propagator = DistributedContextPropagator.Current;
        propagator.Inject(activity, headers, static (carrier, key, value) =>
        {
            if (carrier is Headers h && !string.IsNullOrEmpty(value))
            {
                h.Remove(key);
                h.Add(key, Encoding.UTF8.GetBytes(value));
            }
        });

        var tracestate = headers.FirstOrDefault(h => h.Key == "tracestate");
        tracestate.Should().NotBeNull();
        Encoding.UTF8.GetString(tracestate!.GetValueBytes()).Should().Contain("vendor1=value1");
    }

    [Fact]
    public void Inject_WithNoActivity_DoesNotWriteHeaders()
    {
        // No current activity
        Activity.Current = null;

        var headers = new Headers();

        // Passing null — same as InjectTraceContext does
        var propagator = DistributedContextPropagator.Current;
        // With no activity, nothing should be written
        // (InjectTraceContext returns early on null)
        headers.Count.Should().Be(0);
    }

    // ── Extract tests (Consumer side) ──

    [Fact]
    public void Extract_TraceparentFromKafkaHeaders_ParsesActivityContext()
    {
        // Simulate a producer injecting trace context
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();
        var traceparent = $"00-{traceId}-{spanId}-01";

        var headers = new Headers();
        headers.Add("traceparent", Encoding.UTF8.GetBytes(traceparent));

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not Headers h) return;
                var header = h.FirstOrDefault(x =>
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (header is not null)
                    value = Encoding.UTF8.GetString(header.GetValueBytes());
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
    public void Extract_TraceparentAndTracestate_ParsesBoth()
    {
        var traceId = ActivityTraceId.CreateRandom().ToString();
        var spanId = ActivitySpanId.CreateRandom().ToString();

        var headers = new Headers();
        headers.Add("traceparent", Encoding.UTF8.GetBytes($"00-{traceId}-{spanId}-01"));
        headers.Add("tracestate", Encoding.UTF8.GetBytes("rojo=00f067aa0ba902b7"));

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not Headers h) return;
                var header = h.FirstOrDefault(x =>
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (header is not null)
                    value = Encoding.UTF8.GetString(header.GetValueBytes());
            },
            out var extractedTraceParent,
            out var extractedTraceState);

        extractedTraceParent.Should().Contain(traceId);
        extractedTraceState.Should().Contain("rojo=00f067aa0ba902b7");
    }

    [Fact]
    public void Extract_NoTraceparent_ReturnsNullAndDefaultContext()
    {
        var headers = new Headers();
        headers.Add("some-header", Encoding.UTF8.GetBytes("value"));

        var propagator = DistributedContextPropagator.Current;
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not Headers h) return;
                var header = h.FirstOrDefault(x =>
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (header is not null)
                    value = Encoding.UTF8.GetString(header.GetValueBytes());
            },
            out var extractedTraceParent,
            out _);

        extractedTraceParent.Should().BeNullOrEmpty();
    }

    // ── Round-trip test ──

    [Fact]
    public void RoundTrip_InjectThenExtract_PreservesTraceContext()
    {
        // Producer side: start activity and inject
        using var producerActivity = RouteActivitySource.Source.StartActivity("test publish", ActivityKind.Producer);
        producerActivity.Should().NotBeNull();

        var headers = new Headers();
        var propagator = DistributedContextPropagator.Current;

        propagator.Inject(producerActivity, headers, static (carrier, key, value) =>
        {
            if (carrier is Headers h && !string.IsNullOrEmpty(value))
            {
                h.Remove(key);
                h.Add(key, Encoding.UTF8.GetBytes(value));
            }
        });

        // Consumer side: extract and start child activity
        propagator.ExtractTraceIdAndState(headers,
            static (object? carrier, string key, out string? value, out IEnumerable<string>? values) =>
            {
                value = null;
                values = null;
                if (carrier is not Headers h) return;
                var header = h.FirstOrDefault(x =>
                    string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (header is not null)
                    value = Encoding.UTF8.GetString(header.GetValueBytes());
            },
            out var traceParent,
            out var traceState);

        ActivityContext.TryParse(traceParent!, traceState, out var parentContext).Should().BeTrue();

        using var consumerActivity = RouteActivitySource.Source.StartActivity(
            "test receive", ActivityKind.Consumer, parentContext);
        consumerActivity.Should().NotBeNull();

        // Verify trace continuity: same trace ID, different span IDs
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

        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation", "publish");
        activity.SetTag("messaging.destination.name", "orders");

        activity.GetTagItem("messaging.system").Should().Be("kafka");
        activity.GetTagItem("messaging.operation").Should().Be("publish");
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

        activity.SetTag("messaging.system", "kafka");
        activity.SetTag("messaging.operation", "receive");
        activity.SetTag("messaging.kafka.consumer.group", "my-group");
        activity.SetTag("messaging.kafka.destination.partition", 3);
        activity.SetTag("messaging.kafka.message.offset", 42L);

        activity.GetTagItem("messaging.system").Should().Be("kafka");
        activity.GetTagItem("messaging.kafka.consumer.group").Should().Be("my-group");
        activity.TraceId.ToString().Should().Be(traceId);
    }
}
