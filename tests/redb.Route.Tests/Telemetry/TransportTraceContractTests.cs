using System.Diagnostics;
using redb.Route.Configuration;
using redb.Route.Core;
using redb.Route.Telemetry;

namespace redb.Route.Tests.Telemetry;

/// <summary>
/// The tracing contract every transport shares, kept in the core once: an inbound span takes its parent from the
/// message or is a root, never a child of the receiving thread's ambient activity; the sender's baggage comes back;
/// the context goes out on a send; a failed call marks its span; and <c>EnableTelemetry=false</c> opens no transport
/// span either, so a trace never holds one without its route.
/// </summary>
public class TransportTraceContractTests
{
    private static readonly TraceHeaderReader<Dictionary<string, string>> Read =
        static (headers, name) => headers.TryGetValue(name, out var value) ? value : null;

    private static readonly TraceHeaderWriter<Dictionary<string, string>> Write =
        static (headers, name, value) => headers[name] = value;

    private static string Endpoint() => $"probe://contract-{Guid.NewGuid():N}";

    private static TransportSpan Receive(Dictionary<string, string> headers, string endpoint,
        InboundParent parent = InboundParent.Carrier, global::redb.Route.Core.RouteContext? context = null)
        => RouteTelemetryExtensions.StartConsumerSpan(context, "orders receive", ActivityKind.Consumer,
            "messaging.system", "probe", endpoint, headers, Read, parent, destination: "orders", operation: "receive");

    [Fact]
    public void An_inbound_span_continues_the_trace_the_message_carries()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        var sender = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var headers = new Dictionary<string, string> { ["traceparent"] = $"00-{sender.TraceId}-{sender.SpanId}-01" };

        using (var span = Receive(headers, endpoint))
        {
            span.Activity.Should().NotBeNull();
            span.Activity!.TraceId.Should().Be(sender.TraceId);
            span.Activity.ParentSpanId.Should().Be(sender.SpanId);
            span.Activity.Kind.Should().Be(ActivityKind.Consumer);
        }

        probe.Activities.Should().ContainSingle();
    }

    [Fact]
    public void Without_a_context_in_the_message_the_span_is_a_root_and_the_ambient_comes_back()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        using var ambient = new Activity("poll-loop").Start();

        using (var span = Receive([], endpoint))
        {
            span.Activity.Should().NotBeNull();
            span.Activity!.ParentSpanId.Should().Be(default(ActivitySpanId), "the poll loop did not send this message");
            span.Activity.TraceId.Should().NotBe(ambient.TraceId);
        }

        Activity.Current.Should().BeSameAs(ambient);
    }

    [Fact]
    public void With_a_host_request_span_the_host_span_is_the_parent()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        using var host = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn").Start();
        var other = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01";

        using (var span = Receive(new() { ["traceparent"] = other }, endpoint, InboundParent.HostRequest))
        {
            span.Activity!.ParentSpanId.Should().Be(host.SpanId);
            span.Activity.TraceId.Should().Be(host.TraceId);
        }

        Activity.Current.Should().BeSameAs(host);
    }

    [Fact]
    public void Without_a_host_span_the_host_request_mode_reads_the_carrier()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        var traceId = ActivityTraceId.CreateRandom();

        using var span = Receive(new() { ["traceparent"] = $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01" },
            endpoint, InboundParent.HostRequest);

        span.Activity!.TraceId.Should().Be(traceId);
    }

    [Fact]
    public void The_baggage_a_sender_injects_comes_back_on_the_inbound_span()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        var headers = new Dictionary<string, string>();
        using (var send = new Activity("send").Start())
        {
            send.AddBaggage("tenant", "t1");
            send.AddBaggage("region", "eu");
            RouteTelemetryExtensions.InjectTraceContext(send, headers, Write);
        }

        using var span = Receive(headers, endpoint);

        span.Activity!.GetBaggageItem("tenant").Should().Be("t1");
        span.Activity.GetBaggageItem("region").Should().Be("eu");
    }

    [Fact]
    public void Inject_replaces_a_context_copied_from_the_previous_hop()
    {
        var headers = new Dictionary<string, string> { ["traceparent"] = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01" };
        using var send = new Activity("send").Start();

        RouteTelemetryExtensions.InjectTraceContext(null, headers, Write);

        headers["traceparent"].Should().Be(send.Id, "without its own span the send carries the ambient context");
    }

    [Fact]
    public void Inject_writes_nothing_without_a_context()
    {
        var saved = Activity.Current;
        Activity.Current = null;
        try
        {
            var headers = new Dictionary<string, string>();
            RouteTelemetryExtensions.InjectTraceContext(null, headers, Write);
            headers.Should().BeEmpty();
        }
        finally
        {
            Activity.Current = saved;
        }
    }

    [Fact]
    public void A_failure_marks_the_span_red_with_an_exception_event()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);

        using (var span = Receive([], endpoint))
            span.Activity.RecordFailure(new InvalidOperationException("broker gone"));

        var activity = probe.Activities.Single();
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("broker gone");
        activity.Events.Should().Contain(e => e.Name == "exception");
    }

    [Fact]
    public void Tracing_off_for_the_context_opens_no_transport_span()
    {
        var endpoint = Endpoint();
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint);
        var context = new global::redb.Route.Core.RouteContext(options: new RouteEngineOptions { EnableTelemetry = false });
        using var ambient = new Activity("host").Start();

        using (var span = Receive(new() { ["traceparent"] = $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01" },
                   endpoint, context: context))
        {
            span.Activity.Should().BeNull();
            Activity.Current.Should().BeSameAs(ambient);
        }
        RouteTelemetryExtensions.StartTransportSpan(context, "send", ActivityKind.Producer, "messaging.system", "probe", endpoint)
            .Should().BeNull();

        probe.Activities.Should().BeEmpty();
        context.IsTracingEnabled().Should().BeFalse();
        ((global::redb.Route.Abstractions.IRouteContext?)null).IsTracingEnabled().Should().BeTrue();
    }

    [Fact]
    public void Every_inbound_span_carries_the_sanitized_endpoint()
    {
        var endpoint = Endpoint() + "?password=s3cret";
        using var probe = RouteTelemetryProbe.ForEndpointContaining(endpoint.Substring(0, endpoint.IndexOf('?')));

        string? tag;
        using (var span = Receive([], endpoint))
            tag = RouteTelemetryProbe.Tag(span.Activity!, RouteTelemetryProbe.EndpointTag);

        tag.Should().StartWith(endpoint.Substring(0, endpoint.IndexOf('?'))).And.NotContain("s3cret");
    }
}
